using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Adapters.Social;

/// <summary>A member token with <c>w_member_social</c>, and who it posts as. LinkedIn gives self-serve apps no refresh: it lasts 60 days.</summary>
public sealed record LinkedInCredentials(string AccessToken, string PersonUrn, string Name, DateTimeOffset ExpiresAt);

/// <summary>
/// Posts to the owner's LinkedIn profile through the versioned Posts API. LinkedIn has no idempotency key, so only a refusal
/// (4xx) counts as "not posted"; a server error or no answer leaves the delivery unknown, for the owner to check.
/// </summary>
public sealed class LinkedInPublisher(HttpClient http, LinkedInCredentials credentials, TimeProvider time, string version = LinkedInPublisher.Version) : IPublisher
{
	/// <summary>The API version, YYYYMM. LinkedIn keeps a version for about a year; 202610 was current in October 2026.</summary>
	public const string Version = "202610";


	public Network Network => Network.LinkedIn;

	public async Task<Receipt> PublishAsync(PublishRequest request, CancellationToken ct)
	{
		var images = new List<(string Id, string Alt)>();
		string? video = null;
		foreach (var file in request.Media)
		{
			if (file.IsVideo)
			{
				video = await UploadVideoAsync(file, ct);
				continue;
			}
			images.Add((await UploadAsync(file, ct), file.Alt));
		}

		var post = new JsonObject
		{
			["author"] = credentials.PersonUrn,
			["commentary"] = Little(request.Variant.Text),
			["visibility"] = "PUBLIC",
			["distribution"] = new JsonObject { ["feedDistribution"] = "MAIN_FEED", ["targetEntities"] = new JsonArray(), ["thirdPartyDistributionChannels"] = new JsonArray() },
			["lifecycleState"] = "PUBLISHED",
			["isReshareDisabledByAuthor"] = false
		};
		if (video is not null)
		{
			post["content"] = new JsonObject { ["media"] = new JsonObject { ["id"] = video } };
		}
		else if (images.Count == 1)
		{
			post["content"] = new JsonObject { ["media"] = new JsonObject { ["id"] = images[0].Id, ["altText"] = images[0].Alt } };
		}
		else if (images.Count > 1)
		{
			post["content"] = new JsonObject
			{
				["multiImage"] = new JsonObject { ["images"] = new JsonArray([.. images.Select(i => new JsonObject { ["id"] = i.Id, ["altText"] = i.Alt })]) }
			};
		}

		using var message = Rest(HttpMethod.Post, "rest/posts", WebJson.Body(post));
		using var response = await http.SendAsync(message, ct);
		if (response.StatusCode == HttpStatusCode.Created && response.Headers.TryGetValues("x-restli-id", out var ids) && ids.FirstOrDefault() is { } urn)
		{
			return new Receipt(urn, $"https://www.linkedin.com/feed/update/{urn}/");
		}
		var body = await response.Content.ReadAsStringAsync(ct);
		if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.Conflict)
		{
			throw new PublishException($"LinkedIn refused the post ({(int)response.StatusCode}): {body}");
		}
		// A 5xx, a 409 or a 201 without an id: LinkedIn may have posted it. Only the owner can tell.
		throw new HttpRequestException($"LinkedIn answered {(int)response.StatusCode} without saying whether it posted: {body}");
	}

	/// <summary>Who the token posts as, from LinkedIn's OpenID userinfo: the person URN and the name.</summary>
	public static async Task<(string PersonUrn, string Name)> WhoAsync(HttpClient http, string accessToken, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.linkedin.com/v2/userinfo");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
		using var response = await http.SendAsync(request, ct);
		if (!response.IsSuccessStatusCode)
		{
			throw new PublishException($"LinkedIn refused the token ({(int)response.StatusCode}). It needs the openid, profile and w_member_social scopes.");
		}
		var me = (await response.Content.ReadFromJsonAsync(WebJson.Default.UserInfo, ct))!;
		return ($"urn:li:person:{me.Sub}", me.Name ?? me.Sub);
	}

	/// <summary>
	/// LinkedIn's commentary is "little" text: these characters are markup and must be escaped, or the post is cut off at the first
	/// one. <c>#</c> is left alone so hashtags stay hashtags.
	/// </summary>
	public static string Little(string text)
	{
		var escaped = new StringBuilder(text.Length);
		foreach (var c in text)
		{
			if (c is '\\' or '|' or '{' or '}' or '@' or '[' or ']' or '(' or ')' or '<' or '>' or '*' or '_' or '~')
			{
				_ = escaped.Append('\\');
			}
			_ = escaped.Append(c);
		}
		return escaped.ToString();
	}

	private async Task<string> UploadAsync(MediaFile image, CancellationToken ct)
	{
		if (image.ContentType is not ("image/png" or "image/jpeg" or "image/gif"))
		{
			throw new PublishException($"LinkedIn takes PNG, JPEG and GIF images; {image.Path} is {image.ContentType}.");
		}
		using var initialize = Rest(HttpMethod.Post, "rest/images?action=initializeUpload",
			WebJson.Body(new JsonObject { ["initializeUploadRequest"] = new JsonObject { ["owner"] = credentials.PersonUrn } }));
		using var started = await http.SendAsync(initialize, ct);
		if (!started.IsSuccessStatusCode)
		{
			throw new PublishException($"LinkedIn refused to take the image {image.Path} ({(int)started.StatusCode}): {await started.Content.ReadAsStringAsync(ct)}");
		}
		var upload = (await started.Content.ReadFromJsonAsync(WebJson.Default.Initialized, ct))!.Value;

		using var put = new HttpRequestMessage(HttpMethod.Put, upload.UploadUrl) { Content = new ByteArrayContent(image.Bytes) };
		put.Content.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
		put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
		using var uploaded = await http.SendAsync(put, ct);
		if (!uploaded.IsSuccessStatusCode)
		{
			throw new PublishException($"LinkedIn refused the bytes of {image.Path} ({(int)uploaded.StatusCode}).");
		}
		return upload.Image;
	}

	/// <summary>
	/// Uploads a video through the Videos API: register it with its size, PUT each byte range LinkedIn hands out and keep the ETag
	/// each answers with, finalize with those in order, then wait until LinkedIn has processed it. Every step before the post is
	/// safe to repeat, so any refusal is a <see cref="PublishException"/>.
	/// </summary>
	private async Task<string> UploadVideoAsync(MediaFile video, CancellationToken ct)
	{
		using var initialize = Rest(HttpMethod.Post, "rest/videos?action=initializeUpload", WebJson.Body(new JsonObject
		{
			["initializeUploadRequest"] = new JsonObject
			{
				["owner"] = credentials.PersonUrn,
				["fileSizeBytes"] = video.Bytes.LongLength,
				["uploadCaptions"] = false,
				["uploadThumbnail"] = false
			}
		}));
		using var started = await http.SendAsync(initialize, ct);
		if (!started.IsSuccessStatusCode)
		{
			throw new PublishException($"LinkedIn refused to take the video ({(int)started.StatusCode}): {await started.Content.ReadAsStringAsync(ct)}");
		}
		var upload = (await started.Content.ReadFromJsonAsync(WebJson.Default.VideoInitialized, ct))!.Value;

		var etags = new List<string>();
		foreach (var part in upload.UploadInstructions)
		{
			var length = (int)(part.LastByte - part.FirstByte + 1);
			using var put = new HttpRequestMessage(HttpMethod.Put, part.UploadUrl) { Content = new ByteArrayContent(video.Bytes, (int)part.FirstByte, length) };
			put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
			put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
			using var sent = await http.SendAsync(put, ct);
			if (!sent.IsSuccessStatusCode || !sent.Headers.TryGetValues("ETag", out var etag))
			{
				throw new PublishException($"LinkedIn refused part {etags.Count + 1} of the video ({(int)sent.StatusCode}).");
			}
			etags.Add(etag.First());
		}

		using var finalize = Rest(HttpMethod.Post, "rest/videos?action=finalizeUpload", WebJson.Body(new JsonObject
		{
			["finalizeUploadRequest"] = new JsonObject
			{
				["video"] = upload.Video,
				["uploadToken"] = upload.UploadToken ?? "",
				["uploadedPartIds"] = new JsonArray([.. etags.Select(e => JsonValue.Create(e))])
			}
		}));
		using var finalized = await http.SendAsync(finalize, ct);
		if (!finalized.IsSuccessStatusCode)
		{
			throw new PublishException($"LinkedIn refused to finish the video ({(int)finalized.StatusCode}): {await finalized.Content.ReadAsStringAsync(ct)}");
		}

		for (var waited = 0; waited < 300; waited++)
		{
			using var check = Rest(HttpMethod.Get, $"rest/videos/{Uri.EscapeDataString(upload.Video)}", null);
			using var answer = await http.SendAsync(check, ct);
			var state = answer.IsSuccessStatusCode ? (await answer.Content.ReadFromJsonAsync(WebJson.Default.VideoState, ct))! : new VideoState(null, null);
			switch (state.Status)
			{
				case "AVAILABLE":
					return upload.Video;
				case "PROCESSING_FAILED":
					throw new PublishException($"LinkedIn could not process the video: {state.ProcessingFailureReason ?? "no reason given"}.");
			}
			await Task.Delay(TimeSpan.FromSeconds(2), time, ct);
		}
		throw new PublishException("LinkedIn did not finish processing the video within ten minutes.");
	}

	private HttpRequestMessage Rest(HttpMethod method, string path, HttpContent? content)
	{
		var request = new HttpRequestMessage(method, $"https://api.linkedin.com/{path}") { Content = content };
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
		request.Headers.Add("LinkedIn-Version", version);
		request.Headers.Add("X-Restli-Protocol-Version", "2.0.0");
		return request;
	}

	internal sealed record UserInfo(string Sub, string? Name);

	internal sealed record Upload(string UploadUrl, string Image);

	internal sealed record Initialized(Upload Value);

	internal sealed record VideoPart(string UploadUrl, long FirstByte, long LastByte);

	internal sealed record VideoUpload(string Video, IReadOnlyList<VideoPart> UploadInstructions, string? UploadToken);

	internal sealed record VideoInitialized(VideoUpload Value);

	internal sealed record VideoState(string? Status, string? ProcessingFailureReason);
}
