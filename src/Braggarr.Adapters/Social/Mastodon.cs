using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Adapters.Social;

public sealed record MastodonCredentials(string Instance, string AccessToken);

/// <summary>
/// Posts a status with an access token. Every attempt of one delivery sends the same <c>Idempotency-Key</c>, so the server returns
/// the first status instead of posting a second one, which makes any refusal safe to retry.
/// </summary>
public sealed class MastodonPublisher(HttpClient http, MastodonCredentials credentials, TimeProvider time) : IPublisher
{

	public Network Network => Network.Mastodon;

	public async Task<Receipt> PublishAsync(PublishRequest request, CancellationToken ct)
	{
		var mediaIds = new List<string>();
		foreach (var image in request.Media)
		{
			mediaIds.Add(await UploadAsync(image, ct));
		}
		using var message = new HttpRequestMessage(HttpMethod.Post, Api("api/v1/statuses"))
		{
			Content = WebJson.Body(new JsonObject { ["status"] = request.Variant.Text, ["visibility"] = "public", ["media_ids"] = new JsonArray([.. mediaIds.Select(id => JsonValue.Create(id))]) })
		};
		message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
		message.Headers.Add("Idempotency-Key", $"braggarr:{request.Key}:{request.At.ToUnixTimeSeconds()}");
		using var response = await http.SendAsync(message, ct);
		if (!response.IsSuccessStatusCode)
		{
			throw new PublishException($"Mastodon answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
		}
		var status = (await response.Content.ReadFromJsonAsync(WebJson.Default.Status, ct))!;
		return new Receipt(status.Id, status.Url);
	}

	/// <summary>
	/// Uploads an image or a video with its description. The server may answer 202 and process it in the background; the status can
	/// only carry it once it is processed, so this waits for that: half a minute for an image, ten for a video.
	/// </summary>
	private async Task<string> UploadAsync(MediaFile image, CancellationToken ct)
	{
		using var form = new MultipartFormDataContent();
		var file = new ByteArrayContent(image.Bytes);
		file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
		form.Add(file, "file", Path.GetFileName(image.Path));
		form.Add(new StringContent(image.Alt), "description");
		using var upload = new HttpRequestMessage(HttpMethod.Post, Api("api/v2/media")) { Content = form };
		upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
		using var response = await http.SendAsync(upload, ct);
		if (!response.IsSuccessStatusCode)
		{
			throw new PublishException($"Mastodon refused the image {image.Path} ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
		}
		var media = (await response.Content.ReadFromJsonAsync(WebJson.Default.Attachment, ct))!;
		var patience = image.IsVideo ? 300 : 30;
		for (var waited = 0; media.Url is null && waited < patience; waited++)
		{
			await Task.Delay(TimeSpan.FromSeconds(image.IsVideo ? 2 : 1), time, ct);
			using var check = new HttpRequestMessage(HttpMethod.Get, Api($"api/v1/media/{media.Id}"));
			check.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
			using var status = await http.SendAsync(check, ct);
			if (status.StatusCode == System.Net.HttpStatusCode.OK)
			{
				media = (await status.Content.ReadFromJsonAsync(WebJson.Default.Attachment, ct))!;
			}
		}
		return media.Url is null ? throw new PublishException($"Mastodon did not finish processing {image.Path}.") : media.Id;
	}

	/// <summary>Checks the token: the account it belongs to.</summary>
	public async Task<string> TestAsync(CancellationToken ct)
	{
		using var message = new HttpRequestMessage(HttpMethod.Get, Api("api/v1/accounts/verify_credentials"));
		message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
		using var response = await http.SendAsync(message, ct);
		if (!response.IsSuccessStatusCode)
		{
			throw new PublishException($"Mastodon refused the token ({(int)response.StatusCode}).");
		}
		return "@" + (await response.Content.ReadFromJsonAsync(WebJson.Default.Account, ct))!.Acct;
	}

	private Uri Api(string path) => new($"{credentials.Instance.TrimEnd('/')}/{path}");

	internal sealed record Status(string Id, string Url);

	internal sealed record Account(string Acct);

	internal sealed record Attachment(string Id, string? Url);
}
