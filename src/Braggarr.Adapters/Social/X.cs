using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Adapters.Social;

/// <summary>
/// The app's consumer key and secret, and the owner's access token and secret, all from X's developer portal. User-context OAuth
/// 1.0a needs no sign-in round trip, so it works without an https address.
/// </summary>
public sealed record XCredentials(string ConsumerKey, string ConsumerSecret, string AccessToken, string AccessTokenSecret, string Username = "");

/// <summary>
/// Posts to X through API v2. Images go up through <c>/2/media/upload</c> with their alt text set by <c>/2/media/metadata</c>.
/// X has no idempotency key, so only a 4xx counts as not posted; anything else leaves the delivery unknown.
/// </summary>
public sealed class XPublisher(HttpClient http, XCredentials credentials, TimeProvider time) : IPublisher
{
	public const string Api = "https://api.x.com";


	public Network Network => Network.X;

	public async Task<Receipt> PublishAsync(PublishRequest request, CancellationToken ct)
	{
		var mediaIds = new List<string>();
		foreach (var file in request.Media)
		{
			mediaIds.Add(file.IsVideo ? await UploadVideoAsync(file, ct) : await UploadAsync(file, ct));
		}

		var body = new JsonObject { ["text"] = request.Variant.Text };
		if (mediaIds.Count > 0)
		{
			body["media"] = new JsonObject { ["media_ids"] = new JsonArray([.. mediaIds.Select(id => JsonValue.Create(id))]) };
		}
		using var message = Signed(HttpMethod.Post, $"{Api}/2/tweets", WebJson.Body(body));
		using var response = await http.SendAsync(message, ct);
		var text = await response.Content.ReadAsStringAsync(ct);
		if (response.StatusCode == HttpStatusCode.Created)
		{
			var id = JsonDocument.Parse(text).RootElement.GetProperty("data").GetProperty("id").GetString()!;
			var who = credentials.Username.Length > 0 ? credentials.Username : "i";
			return new Receipt(id, $"https://x.com/{who}/status/{id}");
		}
		if ((int)response.StatusCode is >= 400 and < 500)
		{
			throw new PublishException($"X refused the post ({(int)response.StatusCode}): {text}");
		}
		throw new HttpRequestException($"X answered {(int)response.StatusCode} without saying whether it posted: {text}");
	}

	/// <summary>The account the keys belong to.</summary>
	public async Task<string> WhoAsync(CancellationToken ct)
	{
		using var message = Signed(HttpMethod.Get, $"{Api}/2/users/me", null);
		using var response = await http.SendAsync(message, ct);
		if (!response.IsSuccessStatusCode)
		{
			throw new PublishException($"X refused the keys ({(int)response.StatusCode}). The access token needs read and write permission.");
		}
		return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("data").GetProperty("username").GetString()!;
	}

	private async Task<string> UploadAsync(MediaFile image, CancellationToken ct)
	{
		using var form = new MultipartFormDataContent();
		var file = new ByteArrayContent(image.Bytes);
		file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
		form.Add(file, "media", Path.GetFileName(image.Path));
		form.Add(new StringContent(image.ContentType == "image/gif" ? "tweet_gif" : "tweet_image"), "media_category");
		using var upload = Signed(HttpMethod.Post, $"{Api}/2/media/upload", form);
		using var uploaded = await http.SendAsync(upload, ct);
		if (!uploaded.IsSuccessStatusCode)
		{
			throw new PublishException($"X refused the image {image.Path} ({(int)uploaded.StatusCode}): {await uploaded.Content.ReadAsStringAsync(ct)}");
		}
		var id = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("data").GetProperty("id").GetString()!;

		using var alt = Signed(HttpMethod.Post, $"{Api}/2/media/metadata",
			WebJson.Body(new JsonObject { ["id"] = id, ["metadata"] = new JsonObject { ["alt_text"] = new JsonObject { ["text"] = image.Alt } } }));
		using var described = await http.SendAsync(alt, ct);
		if (!described.IsSuccessStatusCode)
		{
			throw new PublishException($"X refused the alt text for {image.Path} ({(int)described.StatusCode}): {await described.Content.ReadAsStringAsync(ct)}");
		}
		return id;
	}

	/// <summary>A video goes up in parts of at most 4 MB (X takes up to 8; 5 is its advice).</summary>
	public const int ChunkBytes = 4 * 1024 * 1024;

	/// <summary>
	/// Uploads a video in chunks: initialize with its type and size, append each part by index, finalize, then wait while X
	/// processes it, as long as it asks. Then its alt text, like an image's.
	/// </summary>
	private async Task<string> UploadVideoAsync(MediaFile video, CancellationToken ct)
	{
		using var initialize = Signed(HttpMethod.Post, $"{Api}/2/media/upload/initialize",
			WebJson.Body(new JsonObject { ["media_type"] = "video/mp4", ["total_bytes"] = video.Bytes.LongLength, ["media_category"] = "tweet_video" }));
		using var started = await http.SendAsync(initialize, ct);
		var body = await started.Content.ReadAsStringAsync(ct);
		if (!started.IsSuccessStatusCode)
		{
			throw new PublishException($"X refused to take the video ({(int)started.StatusCode}): {body}");
		}
		var id = JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("id").GetString()!;

		for (int offset = 0, index = 0; offset < video.Bytes.Length; offset += ChunkBytes, index++)
		{
			using var form = new MultipartFormDataContent
			{
				{ new StringContent(index.ToString(System.Globalization.CultureInfo.InvariantCulture)), "segment_index" },
				{ new ByteArrayContent(video.Bytes, offset, Math.Min(ChunkBytes, video.Bytes.Length - offset)), "media", "chunk" }
			};
			using var append = Signed(HttpMethod.Post, $"{Api}/2/media/upload/{id}/append", form);
			using var appended = await http.SendAsync(append, ct);
			if (!appended.IsSuccessStatusCode)
			{
				throw new PublishException($"X refused part {index + 1} of the video ({(int)appended.StatusCode}): {await appended.Content.ReadAsStringAsync(ct)}");
			}
		}

		using var finalize = Signed(HttpMethod.Post, $"{Api}/2/media/upload/{id}/finalize", null);
		using var finalized = await http.SendAsync(finalize, ct);
		body = await finalized.Content.ReadAsStringAsync(ct);
		if (!finalized.IsSuccessStatusCode)
		{
			throw new PublishException($"X refused to finish the video ({(int)finalized.StatusCode}): {body}");
		}
		var processing = Processing(body);
		for (var checks = 0; processing is { State: "pending" or "in_progress" }; checks++)
		{
			if (checks >= 120)
			{
				throw new PublishException("X did not finish processing the video.");
			}
			await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(processing.Value.After, 1, 30)), time, ct);
			using var status = Signed(HttpMethod.Get, $"{Api}/2/media/upload?command=STATUS&media_id={id}", null);
			using var answer = await http.SendAsync(status, ct);
			processing = Processing(await answer.Content.ReadAsStringAsync(ct));
		}
		if (processing is { State: "failed" })
		{
			throw new PublishException("X could not process the video.");
		}

		using var alt = Signed(HttpMethod.Post, $"{Api}/2/media/metadata",
			WebJson.Body(new JsonObject { ["id"] = id, ["metadata"] = new JsonObject { ["alt_text"] = new JsonObject { ["text"] = video.Alt } } }));
		// The description helps, but a video X will not describe still goes out: the upload is done and the post can carry it.
		using var described = await http.SendAsync(alt, ct);
		return id;

		static (string State, int After)? Processing(string json)
		{
			try
			{
				return JsonDocument.Parse(json).RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("processing_info", out var info)
					? (info.GetProperty("state").GetString() ?? "", info.TryGetProperty("check_after_secs", out var after) ? after.GetInt32() : 1)
					: null;
			}
			catch (JsonException)
			{
				return ("pending", 5);
			}
		}
	}

	private HttpRequestMessage Signed(HttpMethod method, string url, HttpContent? content)
	{
		var request = new HttpRequestMessage(method, url) { Content = content };
		var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
		var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
		request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", OAuth1.Header(method.Method, url, [], credentials, nonce, timestamp));
		return request;
	}
}

/// <summary>
/// OAuth 1.0a with HMAC-SHA1 (RFC 5849). JSON and multipart bodies are not signed; only the query string and form fields are, and
/// Braggarr sends neither to X.
/// </summary>
public static class OAuth1
{
	public static string Header(string method, string url, IReadOnlyList<KeyValuePair<string, string>> formFields, XCredentials keys, string nonce, string timestamp)
	{
		var oauth = new SortedDictionary<string, string>(StringComparer.Ordinal)
		{
			["oauth_consumer_key"] = keys.ConsumerKey,
			["oauth_nonce"] = nonce,
			["oauth_signature_method"] = "HMAC-SHA1",
			["oauth_timestamp"] = timestamp,
			["oauth_token"] = keys.AccessToken,
			["oauth_version"] = "1.0"
		};
		oauth["oauth_signature"] = Signature(method, url, [.. oauth, .. formFields], keys.ConsumerSecret, keys.AccessTokenSecret);
		return string.Join(", ", oauth.Select(p => $"{Escape(p.Key)}=\"{Escape(p.Value)}\""));
	}

	public static string Signature(string method, string url, IEnumerable<KeyValuePair<string, string>> parameters, string consumerSecret, string tokenSecret)
	{
		var uri = new Uri(url);
		var query = string.IsNullOrEmpty(uri.Query)
			? []
			: uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Select(p => KeyValuePair.Create(Uri.UnescapeDataString(p[0]), Uri.UnescapeDataString(p.Length > 1 ? p[1] : "")));
		var normalized = string.Join("&", parameters.Concat(query)
			.Select(p => (Key: Escape(p.Key), Value: Escape(p.Value)))
			.OrderBy(p => p.Key, StringComparer.Ordinal).ThenBy(p => p.Value, StringComparer.Ordinal)
			.Select(p => $"{p.Key}={p.Value}"));
		var baseUrl = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
		var baseString = $"{method.ToUpperInvariant()}&{Escape(baseUrl)}&{Escape(normalized)}";
		var key = Encoding.ASCII.GetBytes($"{Escape(consumerSecret)}&{Escape(tokenSecret)}");
#pragma warning disable CA5350 // OAuth 1.0a signs with HMAC-SHA1; X accepts no other method.
		return Convert.ToBase64String(HMACSHA1.HashData(key, Encoding.ASCII.GetBytes(baseString)));
#pragma warning restore CA5350
	}

	/// <summary>RFC 3986 percent-encoding: everything but unreserved characters.</summary>
	public static string Escape(string value) => Uri.EscapeDataString(value);
}
