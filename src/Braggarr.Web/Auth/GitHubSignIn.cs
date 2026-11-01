using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Braggarr.Adapters.Config;
using Braggarr.Adapters.GitHub;
using Braggarr.Web.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Braggarr.Web.Auth;

/// <summary>
/// Sign in with GitHub through Braggarr's own GitHub App, and the setup that creates that app from a manifest. Only the GitHub
/// account whose numeric id is the owner's gets in; the first one to sign in during setup, holding the setup token, becomes it.
/// </summary>
public static class GitHubSignIn
{
	public const string GitHubSecretClient = "github:client-secret";
	public const string GitHubSecretKey = "github:private-key";
	private const string StateCookie = "braggarr.oauth-state";

	public static void MapGitHubSignIn(this WebApplication app)
	{
		_ = app.MapGet("/login", (HttpContext http, IOptionsMonitor<GitHubAppOptions> github) =>
		{
			if (github.CurrentValue.ClientId is not { } clientId)
			{
				return Results.Redirect("/setup");
			}
			var state = NewState(http);
			var url = $"https://github.com/login/oauth/authorize?client_id={Uri.EscapeDataString(clientId)}"
				+ $"&redirect_uri={Uri.EscapeDataString(BaseUrl(http) + "/login/callback")}&state={state}&allow_signup=false";
			return Results.Redirect(url);
		}).AllowAnonymous();

		_ = app.MapGet("/login/callback", async (HttpContext http, string? code, string? state, IHttpClientFactory clients, IOptionsMonitor<GitHubAppOptions> github,
			SettingsStore settings, SetupToken setup, ILoggerFactory logs, CancellationToken ct) =>
		{
			if (code is null || !StateMatches(http, state))
			{
				return Results.BadRequest("The sign-in expired or did not start here. Try again.");
			}
			var user = await UserAsync(clients.CreateClient("github"), github.CurrentValue, code, BaseUrl(http) + "/login/callback", ct);
			if (user is null)
			{
				return Results.BadRequest("GitHub did not confirm the sign-in. Try again.");
			}

			var current = settings.Current;
			var settingUp = current.Owner is null && http.User.HasClaim(Roles.ClaimType, Roles.Setup);
			if (settingUp)
			{
				current = settings.Update(s => s with { Owner = new OwnerSettings(user.Id, user.Login) });
				setup.Spend();
				logs.CreateLogger("Braggarr.Setup").LogWarning("{Login} is now Braggarr's owner. The setup token is spent.", user.Login);
			}
			if (current.Owner is not { } owner || owner.Id != user.Id)
			{
				return Results.Redirect("/denied");
			}

			var identity = new ClaimsIdentity(
				[
					new Claim(ClaimTypes.Name, user.Login),
					new Claim(Roles.ClaimType, Roles.Owner),
					new Claim(Roles.OwnerIdClaim, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
				],
				CookieAuthenticationDefaults.AuthenticationScheme
			);
			await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
			return Results.Redirect(settingUp ? "/setup" : "/");
		}).AllowAnonymous();

		_ = app.MapPost("/logout", async (HttpContext http) =>
		{
			await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
			return Results.Redirect("/");
		}).RequireAuthorization(Roles.SetupPolicy);

		// The link printed to the log. A valid token gives a setup session; the token stays valid until the owner signs in.
		_ = app.MapGet("/setup/start", async (HttpContext http, string? token, SetupToken setup) =>
		{
			if (!setup.Matches(token))
			{
				return Results.Redirect("/denied");
			}
			var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "setup"), new Claim(Roles.ClaimType, Roles.Setup)], CookieAuthenticationDefaults.AuthenticationScheme);
			await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
			return Results.Redirect("/setup");
		}).AllowAnonymous();

		// GitHub sends the browser back here after the app was created from the manifest.
		_ = app.MapGet("/setup/app", async (HttpContext http, string? code, string? state, IHttpClientFactory clients, SettingsStore settings, SecretStore secrets,
			GitHubAppToken tokens, CancellationToken ct) =>
		{
			if (code is null || !StateMatches(http, state))
			{
				return Results.BadRequest("The app creation expired or did not start here. Start again from the setup page.");
			}
			var client = clients.CreateClient("github");
			using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/app-manifests/{Uri.EscapeDataString(code)}/conversions");
			GitHubClientHeaders(request);
			using var response = await client.SendAsync(request, ct);
			if (!response.IsSuccessStatusCode)
			{
				return Results.Problem($"GitHub did not hand over the new app ({(int)response.StatusCode}). Start again from the setup page.");
			}
			var created = (await response.Content.ReadFromJsonAsync(SignInJson.Default.CreatedApp, ct))!;
			secrets.Set(GitHubSecretClient, created.ClientSecret);
			secrets.Set(GitHubSecretKey, created.Pem);
			_ = settings.Update(s => s with { GitHubApp = new GitHubAppSettings { AppId = created.Id, Slug = created.Slug, ClientId = created.ClientId } });
			tokens.Forget();
			return Results.Redirect("/setup");
		}).RequireAuthorization(Roles.SetupPolicy);
	}

	/// <summary>The manifest GitHub creates the app from: private, no webhooks, and exactly the permissions the posts repo needs.</summary>
	public static string Manifest(HttpContext http)
	{
		var baseUrl = BaseUrl(http);
		return new JsonObject
		{
			["name"] = $"Braggarr {http.Request.Host.Host}".Trim(),
			["url"] = baseUrl,
			["hook_attributes"] = new JsonObject { ["url"] = baseUrl + "/github/hooks", ["active"] = false },
			["redirect_url"] = baseUrl + "/setup/app",
			["callback_urls"] = new JsonArray(baseUrl + "/login/callback"),
			["setup_url"] = baseUrl + "/setup",
			["public"] = false,
			["default_permissions"] = new JsonObject { ["contents"] = "write", ["pull_requests"] = "write", ["issues"] = "write", ["metadata"] = "read" },
			["default_events"] = new JsonArray()
		}.ToJsonString();
	}

	public static string NewState(HttpContext http)
	{
		var state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
		http.Response.Cookies.Append(StateCookie, state, new CookieOptions
		{
			HttpOnly = true,
			Secure = http.Request.IsHttps,
			SameSite = SameSiteMode.Lax,
			MaxAge = TimeSpan.FromMinutes(15)
		});
		return state;
	}

	public static bool StateMatches(HttpContext http, string? state)
	{
		var expected = http.Request.Cookies[StateCookie];
		http.Response.Cookies.Delete(StateCookie);
		return expected is not null && state is not null
			&& CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(expected), System.Text.Encoding.ASCII.GetBytes(state));
	}

	public static string BaseUrl(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";

	private static async Task<GitHubUser?> UserAsync(HttpClient client, GitHubAppOptions app, string code, string redirect, CancellationToken ct)
	{
		using var exchange = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
		{
			Content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["client_id"] = app.ClientId ?? "",
				["client_secret"] = app.ClientSecret ?? "",
				["code"] = code,
				["redirect_uri"] = redirect
			})
		};
		exchange.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		using var tokenResponse = await client.SendAsync(exchange, ct);
		var token = await tokenResponse.Content.ReadFromJsonAsync(SignInJson.Default.OAuthToken, ct);
		if (token?.AccessToken is not { Length: > 0 } accessToken)
		{
			return null;
		}
		using var me = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
		GitHubClientHeaders(me);
		me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
		using var response = await client.SendAsync(me, ct);
		return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(SignInJson.Default.GitHubUser, ct) : null;
	}

	private static void GitHubClientHeaders(HttpRequestMessage request)
	{
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Braggarr", "1.0"));
	}

	internal sealed record OAuthToken(string? AccessToken);

	internal sealed record GitHubUser(long Id, string Login);

	internal sealed record CreatedApp(long Id, string Slug, string ClientId, string ClientSecret, string Pem);
}
