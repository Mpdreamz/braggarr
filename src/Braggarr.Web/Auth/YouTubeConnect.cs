using Braggarr.Adapters.Config;
using Braggarr.Domain;
using Braggarr.Adapters.Social;
using Braggarr.Services;

namespace Braggarr.Web.Auth;

/// <summary>
/// Connecting YouTube: the owner's browser goes to Google's consent with a state only it holds, and comes back to the callback,
/// where the code becomes a refresh token. Google needs the callback on https (or localhost), so this works through the public
/// hostname, not the Tailscale address. The outcome travels back to the settings page as a code, never as text from the URL.
/// </summary>
public static class YouTubeConnect
{
	public const string Callback = "/settings/youtube/callback";

	public static string RedirectUri(HttpContext http) => GitHubSignIn.BaseUrl(http) + Callback;

	public static void MapYouTubeConnect(this WebApplication app)
	{
		_ = app.MapGet("/settings/youtube/connect", (HttpContext http, SecretStore secrets) =>
			secrets.Get<YouTubeClient>(Publishers.YouTubeClientSecret) is { } client
				? Results.Redirect(GoogleOAuth.Consent(client, RedirectUri(http), GitHubSignIn.NewState(http)))
				: Results.Redirect("/Settings?section=networks&youtube=no-client")
		).RequireAuthorization(Roles.OwnerPolicy);

		_ = app.MapGet(Callback, async (HttpContext http, string? code, string? state, string? error, SecretStore secrets, AccountStore accounts, IHttpClientFactory clients,
			ILoggerFactory logs, CancellationToken ct) =>
		{
			if (!GitHubSignIn.StateMatches(http, state))
			{
				return Results.Redirect("/Settings?section=networks&youtube=state");
			}
			if (error is not null || code is null || secrets.Get<YouTubeClient>(Publishers.YouTubeClientSecret) is not { } client)
			{
				return Results.Redirect("/Settings?section=networks&youtube=declined");
			}
			try
			{
				var google = clients.CreateClient("youtube");
				var refresh = await GoogleOAuth.ExchangeAsync(google, client, code, RedirectUri(http), ct);
				var credentials = new YouTubeCredentials(client.ClientId, client.ClientSecret, refresh, "");
				var channel = await GoogleOAuth.ChannelAsync(google, (await GoogleOAuth.RefreshAsync(google, credentials, ct)).Token, ct);
				accounts.Set(Network.YouTube, channel, credentials with { Channel = channel });
				return Results.Redirect("/Settings?section=networks&youtube=connected");
			}
			catch (Exception e) when (e is PublishException or HttpRequestException)
			{
				logs.CreateLogger("Braggarr.YouTube").LogWarning("Connecting YouTube failed: {Problem}", e.Message);
				return Results.Redirect("/Settings?section=networks&youtube=failed");
			}
		}).RequireAuthorization(Roles.OwnerPolicy);
	}
}
