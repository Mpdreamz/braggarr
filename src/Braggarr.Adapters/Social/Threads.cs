using System.Net.Http.Json;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Adapters.Social;

/// <summary>A long-lived Threads token (60 days, refreshable once a day old) and the account it belongs to.</summary>
public sealed record ThreadsCredentials(string AccessToken, string UserId, string Username, DateTimeOffset ExpiresAt, DateTimeOffset IssuedAt);

/// <summary>
/// Posts text to Threads: a container first, then publishing it once Threads has processed it. A refusal to create the container
/// means nothing was posted; once publishing has been asked for, an unclear answer leaves the delivery unknown.
/// </summary>
public sealed class ThreadsPublisher(HttpClient http, ThreadsCredentials credentials, TimeProvider time) : IPublisher
{
	public const string Graph = "https://graph.threads.com";


	public Network Network => Network.Threads;

	/// <summary>How long to wait between checks on a container, and how many checks before giving up.</summary>
	public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(3);

	public int Checks { get; init; } = 20;

	public async Task<Receipt> PublishAsync(PublishRequest request, CancellationToken ct)
	{
		if (request.Media.Count > 0)
		{
			throw new PublishException("Threads takes images only from a public URL, which Braggarr does not have. Post the text alone.");
		}
		var container = await PostAsync<Created>($"v1.0/{credentials.UserId}/threads", new() { ["media_type"] = "TEXT", ["text"] = request.Variant.Text }, ct)
			?? throw new PublishException("Threads did not create the post.");

		for (var check = 0; check < Checks; check++)
		{
			var status = await GetAsync<ContainerStatus>($"v1.0/{container.Id}?fields=status,error_message", ct);
			if (status?.Status == "FINISHED")
			{
				break;
			}
			if (status?.Status is "ERROR" or "EXPIRED")
			{
				throw new PublishException($"Threads could not process the post: {status.ErrorMessage ?? status.Status}");
			}
			await Task.Delay(Poll, time, ct);
		}

		// From here a lost answer may mean it is up.
		var published = await PostAsync<Created>($"v1.0/{credentials.UserId}/threads_publish", new() { ["creation_id"] = container.Id }, ct, uncertain: true);
		var link = await GetAsync<Published>($"v1.0/{published!.Id}?fields=permalink", ct);
		return new Receipt(published.Id, link?.Permalink ?? $"https://www.threads.com/@{credentials.Username}");
	}

	/// <summary>Who a token belongs to.</summary>
	public static async Task<(string Id, string Username)> WhoAsync(HttpClient http, string accessToken, CancellationToken ct)
	{
		using var response = await http.GetAsync(new Uri($"{Graph}/v1.0/me?fields=id,username&access_token={Uri.EscapeDataString(accessToken)}"), ct);
		if (!response.IsSuccessStatusCode)
		{
			throw new PublishException($"Threads refused the token ({(int)response.StatusCode}). Use a long-lived token with threads_basic and threads_content_publish.");
		}
		var me = (await response.Content.ReadFromJsonAsync(WebJson.Default.Me, ct))!;
		return (me.Id, me.Username);
	}

	/// <summary>Swaps a long-lived token (at least a day old) for a fresh one that lasts 60 days from now.</summary>
	public static async Task<(string Token, TimeSpan Lasts)> RefreshAsync(HttpClient http, string accessToken, CancellationToken ct)
	{
		using var response = await http.GetAsync(new Uri($"{Graph}/refresh_access_token?grant_type=th_refresh_token&access_token={Uri.EscapeDataString(accessToken)}"), ct);
		if (!response.IsSuccessStatusCode)
		{
			throw new PublishException($"Threads did not refresh the token ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
		}
		var token = (await response.Content.ReadFromJsonAsync(SnakeJson.Default.Token, ct))!;
		return (token.AccessToken, TimeSpan.FromSeconds(token.ExpiresIn));
	}

	private async Task<T?> PostAsync<T>(string path, Dictionary<string, string> form, CancellationToken ct, bool uncertain = false)
	{
		form["access_token"] = credentials.AccessToken;
		using var response = await http.PostAsync(new Uri($"{Graph}/{path}"), new FormUrlEncodedContent(form), ct);
		if (!response.IsSuccessStatusCode)
		{
			var body = await response.Content.ReadAsStringAsync(ct);
			if (uncertain && (int)response.StatusCode >= 500)
			{
				throw new HttpRequestException($"Threads answered {(int)response.StatusCode} while publishing: {body}");
			}
			throw new PublishException($"Threads refused ({(int)response.StatusCode}): {body}");
		}
		return await response.Content.ReadFromJsonAsync(WebJson.Info<T>(), ct);
	}

	private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
	{
		var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
		using var response = await http.GetAsync(new Uri($"{Graph}/{path}{separator}access_token={Uri.EscapeDataString(credentials.AccessToken)}"), ct);
		return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(SnakeJson.Info<T>(), ct) : default;
	}


	internal sealed record Created(string Id);

	internal sealed record ContainerStatus(string? Status, string? ErrorMessage);

	internal sealed record Published(string? Id, string? Permalink);

	internal sealed record Me(string Id, string Username);

	internal sealed record Token(string AccessToken, long ExpiresIn);
}
