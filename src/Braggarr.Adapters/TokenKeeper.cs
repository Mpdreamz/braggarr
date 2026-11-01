using Braggarr.Domain;
using Braggarr.Adapters.Social;
using Braggarr.Services;
using Microsoft.Extensions.Logging;

namespace Braggarr.Adapters;

/// <summary>
/// Keeps the network tokens that expire alive, or says so in time. Threads tokens are refreshed once they are a day old and have
/// less than 30 days left. LinkedIn tokens cannot be refreshed: from a week before they run out, the owner hears about it once a
/// day, and after they ran out LinkedIn posts wait.
/// </summary>
public sealed class TokenKeeper(AccountStore accounts, IHttpClientFactory http, INotifier notifier, TimeProvider time, ILogger<TokenKeeper> logger)
{
	public static readonly TimeSpan WarnAhead = TimeSpan.FromDays(7);

	private readonly Dictionary<string, DateOnly> _warned = [];

	public async Task CheckAsync(CancellationToken ct)
	{
		var now = time.GetUtcNow();
		foreach (var (name, threads) in accounts.Each<ThreadsCredentials>(Network.Threads).Where(a => now < a.Credentials.ExpiresAt))
		{
			if (now - threads.IssuedAt >= TimeSpan.FromDays(1) && threads.ExpiresAt - now < TimeSpan.FromDays(30))
			{
				try
				{
					var (token, lasts) = await ThreadsPublisher.RefreshAsync(http.CreateClient("threads"), threads.AccessToken, ct);
					accounts.Set(Network.Threads, name, threads with { AccessToken = token, IssuedAt = now, ExpiresAt = now + lasts });
					if (logger.IsEnabled(LogLevel.Information))
					{
						logger.LogInformation("Refreshed the Threads token for @{Username}.", threads.Username);
					}
					continue;
				}
				catch (Exception e) when (e is PublishException or HttpRequestException)
				{
					logger.LogWarning("Could not refresh the Threads token for @{Username}: {Problem}", threads.Username, e.Message);
				}
			}
			await WarnAsync($"Threads token for @{name}", threads.ExpiresAt, now, "Braggarr could not refresh it. Paste a new long-lived token in Settings.", "Threads", ct);
		}
		foreach (var (name, linkedin) in accounts.Each<LinkedInCredentials>(Network.LinkedIn))
		{
			await WarnAsync($"LinkedIn token for {name}", linkedin.ExpiresAt, now,
				"LinkedIn tokens last 60 days. Make a new one with the token generator and paste it in Settings.", "LinkedIn", ct);
		}
	}

	private async Task WarnAsync(string token, DateTimeOffset expires, DateTimeOffset now, string what, string network, CancellationToken ct)
	{
		if (expires - now > WarnAhead || _warned.GetValueOrDefault(token) == DateOnly.FromDateTime(now.UtcDateTime))
		{
			return;
		}
		_warned[token] = DateOnly.FromDateTime(now.UtcDateTime);
		var title = expires <= now ? $"The {token} ran out" : $"The {token} runs out in {Math.Max(1, (int)Math.Ceiling((expires - now).TotalDays))} days";
		await notifier.NotifyAsync(new Notice(NoticeLevel.Problem, title, $"{what} Until then, {network} posts from that account wait."), ct);
	}
}
