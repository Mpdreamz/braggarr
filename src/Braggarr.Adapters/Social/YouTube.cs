using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Adapters.Social;

/// <summary>The owner's own Google Cloud OAuth client: a web application, with Braggarr's callback as its redirect URI.</summary>
public sealed record YouTubeClient(string ClientId, string ClientSecret);

/// <summary>A connected channel: the client, the refresh token Google gave at consent, and the channel's name.</summary>
public sealed record YouTubeCredentials(string ClientId, string ClientSecret, string RefreshToken, string Channel);

/// <summary>
/// Google's OAuth for YouTube (web server flow): consent once with offline access for a refresh token, then a fresh access token
/// per hour. Upload, and read the channel's name to say who is connected.
/// </summary>
public static class GoogleOAuth
{
	public const string AuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth";
	public const string TokenUrl = "https://oauth2.googleapis.com/token";
	// Upload to post videos; youtube (manage) to keep the channel's description, which the profile page can set.
	public const string Scopes = "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly https://www.googleapis.com/auth/youtube";


	/// <summary>Where to send the owner to consent. <c>prompt=consent</c>, because Google gives a refresh token on first consent only.</summary>
	public static string Consent(YouTubeClient client, string redirect, string state) =>
		$"{AuthorizeUrl}?client_id={Uri.EscapeDataString(client.ClientId)}&redirect_uri={Uri.EscapeDataString(redirect)}&response_type=code"
		+ $"&scope={Uri.EscapeDataString(Scopes)}&access_type=offline&prompt=consent&include_granted_scopes=true&state={Uri.EscapeDataString(state)}";

	/// <summary>The refresh token for a consent's code.</summary>
	public static async Task<string> ExchangeAsync(HttpClient http, YouTubeClient client, string code, string redirect, CancellationToken ct)
	{
		var answer = await TokenAsync(http, new()
		{
			["code"] = code,
			["client_id"] = client.ClientId,
			["client_secret"] = client.ClientSecret,
			["redirect_uri"] = redirect,
			["grant_type"] = "authorization_code"
		}, ct);
		return answer.RefreshToken ?? throw new PublishException("Google gave no refresh token. Remove Braggarr's access in your Google account and connect again.");
	}

	/// <summary>A fresh access token and how long it lasts.</summary>
	public static async Task<(string Token, TimeSpan Lasts)> RefreshAsync(HttpClient http, YouTubeCredentials credentials, CancellationToken ct)
	{
		var answer = await TokenAsync(http, new()
		{
			["client_id"] = credentials.ClientId,
			["client_secret"] = credentials.ClientSecret,
			["refresh_token"] = credentials.RefreshToken,
			["grant_type"] = "refresh_token"
		}, ct);
		return (answer.AccessToken ?? throw new PublishException("Google answered without an access token."), TimeSpan.FromSeconds(answer.ExpiresIn is > 0 ? answer.ExpiresIn.Value : 3600));
	}

	/// <summary>The name of the channel an access token belongs to.</summary>
	public static async Task<string> ChannelAsync(HttpClient http, string accessToken, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
		using var response = await http.SendAsync(request, ct);
		var body = await response.Content.ReadAsStringAsync(ct);
		if (!response.IsSuccessStatusCode)
		{
			throw new PublishException($"YouTube refused to name the channel ({(int)response.StatusCode}): {body}");
		}
		var items = JsonDocument.Parse(body).RootElement.TryGetProperty("items", out var list) ? list : default;
		return items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0
			? items[0].GetProperty("snippet").GetProperty("title").GetString() ?? "your channel"
			: throw new PublishException("This Google account has no YouTube channel yet. Create one, then connect again.");
	}

	private static async Task<TokenAnswer> TokenAsync(HttpClient http, Dictionary<string, string> form, CancellationToken ct)
	{
		using var response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
		var body = await response.Content.ReadAsStringAsync(ct);
		if (!response.IsSuccessStatusCode)
		{
			throw new PublishException(body.Contains("invalid_grant", StringComparison.Ordinal)
				? "Google no longer accepts Braggarr's access to YouTube (revoked, or the app is still in Testing, where it lasts 7 days). Connect YouTube again in Settings."
				: $"Google refused the token request ({(int)response.StatusCode}): {body}");
		}
		return JsonSerializer.Deserialize(body, WebJson.Default.TokenAnswer)!;
	}

	internal sealed record TokenAnswer(
		[property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string? AccessToken,
		[property: System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string? RefreshToken,
		[property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int? ExpiresIn
	);
}

/// <summary>
/// Uploads a post's video to YouTube, public, through the resumable protocol. The first line of the variant is the title, the rest
/// the description. Nothing exists on YouTube until the bytes are all in, so any refusal up to then is "not posted"; an upload
/// that stops is resumed from what YouTube says it has, and only an answer lost after the last byte leaves the delivery unknown.
/// </summary>
public sealed class YouTubePublisher(HttpClient http, YouTubeCredentials credentials, TimeProvider time) : IPublisher
{
	public const string UploadUrl = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status";

	/// <summary>Science &amp; Technology, where a developer's tools belong.</summary>
	public const string Category = "28";

	public const int Resumes = 3;

	private (string Token, DateTimeOffset Until)? _access;

	public Network Network => Network.YouTube;

	public async Task<Receipt> PublishAsync(PublishRequest request, CancellationToken ct)
	{
		var video = request.Media.SingleOrDefault(m => m.IsVideo) ?? throw new PublishException("A YouTube post needs its video, and this one has none.");
		var token = await AccessAsync(ct);

		using var start = new HttpRequestMessage(HttpMethod.Post, UploadUrl)
		{
			Content = WebJson.Body(new JsonObject
			{
				["snippet"] = new JsonObject
				{
					["title"] = Lengths.Title(request.Variant.Text),
					["description"] = Lengths.Description(request.Variant.Text),
					["categoryId"] = Category
				},
				["status"] = new JsonObject { ["privacyStatus"] = "public", ["selfDeclaredMadeForKids"] = false }
			})
		};
		start.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
		start.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		start.Headers.Add("X-Upload-Content-Length", video.Bytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
		start.Headers.Add("X-Upload-Content-Type", "video/mp4");
		using var started = await http.SendAsync(start, ct);
		if (!started.IsSuccessStatusCode || started.Headers.Location is not { } session)
		{
			// No session, no video: whatever went wrong, nothing is on YouTube.
			throw new PublishException($"YouTube refused to start the upload ({(int)started.StatusCode}): {await started.Content.ReadAsStringAsync(ct)}");
		}

		long from = 0;
		for (var resume = 0; ; resume++)
		{
			HttpResponseMessage? sent = null;
			try
			{
				sent = await SendBytesAsync(session, token, video.Bytes, from, ct);
				if (sent.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
				{
					return await ReceiptAsync(sent, ct);
				}
				if ((int)sent.StatusCode is >= 400 and < 500)
				{
					throw new PublishException($"YouTube refused the video ({(int)sent.StatusCode}): {await sent.Content.ReadAsStringAsync(ct)}");
				}
			}
			catch (HttpRequestException)
			{
				// The connection dropped: ask YouTube how far it got. If that question gets no answer either, the delivery is unknown.
			}
			finally
			{
				sent?.Dispose();
			}
			await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, resume + 1)), time, ct);

			using var status = await StatusAsync(session, token, video.Bytes.LongLength, ct);
			if (status.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
			{
				// It arrived after all; only the answer was lost.
				return await ReceiptAsync(status, ct);
			}
			if ((int)status.StatusCode != 308)
			{
				throw new PublishException($"YouTube ended the upload session ({(int)status.StatusCode}); nothing was published.");
			}
			if (resume >= Resumes)
			{
				// YouTube says the upload is incomplete, so no video exists: safe to try again later from the start.
				throw new PublishException($"The upload to YouTube was still incomplete after {Resumes} resumes; nothing was published.");
			}
			from = status.Headers.TryGetValues("Range", out var range) && range.FirstOrDefault() is { } r && long.TryParse(r[(r.IndexOf('-') + 1)..], out var last)
				? last + 1
				: 0;
		}
	}

	/// <summary>The bytes from <paramref name="from"/> on, with their range when resuming.</summary>
	private async Task<HttpResponseMessage> SendBytesAsync(Uri session, string token, byte[] bytes, long from, CancellationToken ct)
	{
		var put = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent(bytes, (int)from, (int)(bytes.LongLength - from)) };
		put.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
		if (from > 0)
		{
			put.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, bytes.LongLength - 1, bytes.LongLength);
		}
		put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		using (put)
		{
			return await http.SendAsync(put, ct);
		}
	}

	private async Task<HttpResponseMessage> StatusAsync(Uri session, string token, long length, CancellationToken ct)
	{
		using var check = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent([]) };
		check.Content.Headers.ContentRange = new ContentRangeHeaderValue(length);
		check.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		return await http.SendAsync(check, ct);
	}

	private static async Task<Receipt> ReceiptAsync(HttpResponseMessage response, CancellationToken ct)
	{
		var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("id").GetString()!;
		return new Receipt(id, $"https://www.youtube.com/watch?v={id}");
	}

	/// <summary>An access token, refreshed when the one held has less than a minute left by the app's clock.</summary>
	private async Task<string> AccessAsync(CancellationToken ct)
	{
		if (_access is { } held && held.Until > time.GetUtcNow().AddMinutes(1))
		{
			return held.Token;
		}
		var (token, lasts) = await GoogleOAuth.RefreshAsync(http, credentials, ct);
		_access = (token, time.GetUtcNow() + lasts);
		return token;
	}

	/// <summary>Checks the connection: the channel's name.</summary>
	public async Task<string> TestAsync(CancellationToken ct) => await GoogleOAuth.ChannelAsync(http, await AccessAsync(ct), ct);
}
