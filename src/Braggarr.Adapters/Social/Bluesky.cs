using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Adapters.Social;

public sealed record BlueskyCredentials(string Handle, string AppPassword, string Service = "https://bsky.social");

/// <summary>
/// Posts to Bluesky with an app password. The record key is derived from the delivery, so a retry of a post that already went out
/// is refused as a duplicate instead of posting twice; Braggarr reads that refusal as success.
/// </summary>
public sealed partial class BlueskyPublisher(HttpClient http, BlueskyCredentials credentials, TimeProvider time, string videoService = BlueskyPublisher.VideoService)
	: IPublisher
{
	/// <summary>Bluesky's video service: it processes a video before the post exists, so the post never shows a missing video.</summary>
	public const string VideoService = "https://video.bsky.app";

	private Session? _session;

	public Network Network => Network.Bluesky;

	public async Task<Receipt> PublishAsync(PublishRequest request, CancellationToken ct)
	{
		var rkey = RecordKey(request.Key, request.At);
		var session = _session ??= await SignInAsync(ct);
		var images = new JsonArray();
		JsonObject? video = null;
		foreach (var file in request.Media)
		{
			if (file.IsVideo)
			{
				video = await VideoEmbedAsync(file, session, ct);
				continue;
			}
			// As a JsonNode: JsonArray.Add<T> would need reflection.
			images.Add((JsonNode)new JsonObject { ["alt"] = file.Alt, ["image"] = Node(await UploadAsync(file, session, ct)) });
		}
		var post = new JsonObject
		{
			["$type"] = "app.bsky.feed.post",
			["text"] = request.Variant.Text,
			["createdAt"] = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
			["facets"] = Facets(request.Variant.Text)
		};
		if (video is not null)
		{
			post["embed"] = video;
		}
		else if (images.Count > 0)
		{
			post["embed"] = new JsonObject { ["$type"] = "app.bsky.embed.images", ["images"] = images };
		}
		var record = new JsonObject
		{
			["repo"] = session.Did,
			["collection"] = "app.bsky.feed.post",
			["rkey"] = rkey,
			["record"] = post
		};

		var response = await SendAsync("com.atproto.repo.createRecord", record, session, ct);
		if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest && await IsExpiredAsync(response, ct))
		{
			response.Dispose();
			_session = session = await SignInAsync(ct);
			record["repo"] = session.Did;
			response = await SendAsync("com.atproto.repo.createRecord", record, session, ct);
		}
		using (response)
		{
			var receipt = new Receipt($"at://{session.Did}/app.bsky.feed.post/{rkey}", $"https://bsky.app/profile/{session.Handle}/post/{rkey}");
			if (response.IsSuccessStatusCode)
			{
				return receipt;
			}
			var body = await response.Content.ReadAsStringAsync(ct);
			if (body.Contains("already exists", StringComparison.OrdinalIgnoreCase))
			{
				return receipt; // the earlier attempt went out
			}
			// Any refusal, a 5xx included, is safe to retry: the record key makes a second attempt a duplicate, not a second post.
			throw new PublishException($"Bluesky answered {(int)response.StatusCode}: {body}");
		}
	}

	/// <summary>Checks the credentials by signing in.</summary>
	public async Task<string> TestAsync(CancellationToken ct) => (_session = await SignInAsync(ct)).Handle;

	private async Task<Session> SignInAsync(CancellationToken ct)
	{
		using var response = await http.PostAsJsonAsync(
			Xrpc("com.atproto.server.createSession"),
			new SignIn(credentials.Handle, credentials.AppPassword),
			WebJson.Default.SignIn,
			ct
		);
		if (!response.IsSuccessStatusCode)
		{
			throw new PublishException($"Bluesky refused the sign-in for {credentials.Handle} ({(int)response.StatusCode}). Check the handle and app password.");
		}
		return (await response.Content.ReadFromJsonAsync(WebJson.Default.Session, ct))!;
	}

	/// <summary>Uploads an image as a blob; the answer's <c>blob</c> object goes into the post as it came back.</summary>
	private async Task<JsonElement> UploadAsync(MediaFile image, Session session, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, Xrpc("com.atproto.repo.uploadBlob")) { Content = new ByteArrayContent(image.Bytes) };
		request.Content.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessJwt);
		using var response = await http.SendAsync(request, ct);
		if (!response.IsSuccessStatusCode)
		{
			throw new PublishException($"Bluesky refused the image {image.Path} ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
		}
		using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
		return document.RootElement.GetProperty("blob").Clone();
	}

	/// <summary>
	/// Sends the video to Bluesky's video service with a service token for the account's PDS, waits for the processing job to hand
	/// back the blob, and returns the <c>app.bsky.embed.video</c> embed. A video the service already processed comes back as
	/// "already_exists" with its blob, which is used the same way.
	/// </summary>
	private async Task<JsonObject> VideoEmbedAsync(MediaFile video, Session session, CancellationToken ct)
	{
		var pds = PdsHost(session) ?? new Uri(credentials.Service).Host;
		var exp = time.GetUtcNow().AddMinutes(30).ToUnixTimeSeconds();
		using var auth = new HttpRequestMessage(HttpMethod.Get,
			Xrpc($"com.atproto.server.getServiceAuth?aud={Uri.EscapeDataString($"did:web:{pds}")}&lxm=com.atproto.repo.uploadBlob&exp={exp}"));
		auth.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessJwt);
		using var authorized = await http.SendAsync(auth, ct);
		if (!authorized.IsSuccessStatusCode)
		{
			throw new PublishException($"Bluesky refused a token for the video service ({(int)authorized.StatusCode}): {await authorized.Content.ReadAsStringAsync(ct)}");
		}
		var token = JsonDocument.Parse(await authorized.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("token").GetString()!;

		var name = Path.GetFileName(video.Path.Replace(':', '/'));
		using var upload = new HttpRequestMessage(HttpMethod.Post,
			$"{videoService.TrimEnd('/')}/xrpc/app.bsky.video.uploadVideo?did={Uri.EscapeDataString(session.Did)}&name={Uri.EscapeDataString(name)}")
		{
			Content = new ByteArrayContent(video.Bytes)
		};
		upload.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
		upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		using var uploaded = await http.SendAsync(upload, ct);
		var job = Parse(await uploaded.Content.ReadAsStringAsync(ct));
		if (!uploaded.IsSuccessStatusCode && !(job.TryGetProperty("blob", out _) || job.TryGetProperty("jobId", out _)))
		{
			throw new PublishException($"Bluesky's video service refused {video.Path} ({(int)uploaded.StatusCode}): {job}");
		}

		var blob = Blob(job);
		for (var checks = 0; blob is null; checks++)
		{
			if (checks >= 300)
			{
				throw new PublishException($"Bluesky did not finish processing {video.Path} within ten minutes.");
			}
			var id = job.GetProperty("jobId").GetString()!;
			await Task.Delay(TimeSpan.FromSeconds(2), time, ct);
			using var check = await http.GetAsync($"{videoService.TrimEnd('/')}/xrpc/app.bsky.video.getJobStatus?jobId={Uri.EscapeDataString(id)}", ct);
			var status = Parse(await check.Content.ReadAsStringAsync(ct));
			job = status.TryGetProperty("jobStatus", out var inner) ? inner : status;
			blob = Blob(job);
			if (blob is null && (!job.TryGetProperty("state", out var state) || state.GetString() == "JOB_STATE_FAILED"))
			{
				throw new PublishException($"Bluesky could not process {video.Path}: {(job.TryGetProperty("error", out var error) ? error.ToString() : "no reason given")}.");
			}
		}

		var embed = new JsonObject { ["$type"] = "app.bsky.embed.video", ["video"] = Node(blob.Value), ["alt"] = video.Alt };
		if (video.Video is { } shape)
		{
			embed["aspectRatio"] = new JsonObject { ["width"] = shape.Width, ["height"] = shape.Height };
		}
		return embed;

		// An answer that is not JSON (a proxy's error page) reads as an empty object: no job, no blob.
		static JsonElement Parse(string text)
		{
			try
			{
				return JsonDocument.Parse(text).RootElement.Clone();
			}
			catch (JsonException)
			{
				return JsonDocument.Parse("{}").RootElement.Clone();
			}
		}

		static JsonElement? Blob(JsonElement job) =>
			job.TryGetProperty("blob", out var b) ? b.Clone()
			: job.TryGetProperty("jobStatus", out var s) && s.TryGetProperty("blob", out var nested) ? nested.Clone()
			: null;
	}

	/// <summary>The host of the account's own PDS, from the DID document the session carries; a service token is for it.</summary>
	private static string? PdsHost(Session session) =>
		session.DidDoc is { ValueKind: JsonValueKind.Object } doc && doc.TryGetProperty("service", out var services)
			? services.EnumerateArray()
				.Where(s => s.TryGetProperty("id", out var id) && id.GetString()?.EndsWith("#atproto_pds", StringComparison.Ordinal) == true)
				.Select(s => Uri.TryCreate(s.GetProperty("serviceEndpoint").GetString(), UriKind.Absolute, out var uri) ? uri.Host : null)
				.FirstOrDefault()
			: null;

	private async Task<HttpResponseMessage> SendAsync(string method, JsonObject body, Session session, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, Xrpc(method)) { Content = JsonContent.Create<JsonNode>(body, WebJson.Default.JsonNode) };
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessJwt);
		return await http.SendAsync(request, ct);
	}

	private static async Task<bool> IsExpiredAsync(HttpResponseMessage response, CancellationToken ct) =>
		(await response.Content.ReadAsStringAsync(ct)).Contains("ExpiredToken", StringComparison.Ordinal);

	private Uri Xrpc(string method) => new($"{credentials.Service.TrimEnd('/')}/xrpc/{method}");

	/// <summary>Link facets: Bluesky shows a URL as a link only when told where it is, in UTF-8 byte offsets.</summary>
	internal static JsonArray Facets(string text) => new(
	[
		.. Url().Matches(text).Select(m => (JsonNode?)new JsonObject
		{
			["index"] = new JsonObject
			{
				["byteStart"] = Encoding.UTF8.GetByteCount(text.AsSpan(0, m.Index)),
				["byteEnd"] = Encoding.UTF8.GetByteCount(text.AsSpan(0, m.Index + m.Length))
			},
			["features"] = new JsonArray(new JsonObject { ["$type"] = "app.bsky.richtext.facet#link", ["uri"] = m.Value.TrimEnd('.', ',', ')', '!', '?') })
		})
	]);

	/// <summary>A blob as Bluesky sent it back, to go into the post as it is.</summary>
	private static JsonNode Node(JsonElement element) => JsonNode.Parse(element.GetRawText())!;

	/// <summary>
	/// A TID (Bluesky's sortable record key) that is the same on every attempt of one delivery: the post's time in microseconds plus
	/// an offset under a second from the delivery key, and a clock id from the key.
	/// </summary>
	internal static string RecordKey(DeliveryKey key, DateTimeOffset at)
	{
		var hash = System.IO.Hashing.XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(key.ToString()));
		var micros = ((ulong)at.ToUnixTimeMilliseconds() * 1000) + (hash % 1_000_000);
		var value = ((micros & ((1UL << 53) - 1)) << 10) | ((hash >> 20) & 0x3FF);
		const string alphabet = "234567abcdefghijklmnopqrstuvwxyz";
		var chars = new char[13];
		for (var i = 12; i >= 0; i--)
		{
			chars[i] = alphabet[(int)(value & 31)];
			value >>= 5;
		}
		return new string(chars);
	}

	[GeneratedRegex(@"https?://[^\s]+")]
	private static partial Regex Url();

	internal sealed record Session(string Did, string Handle, string AccessJwt, JsonElement? DidDoc = null);

	internal sealed record SignIn(string Identifier, string Password);
}
