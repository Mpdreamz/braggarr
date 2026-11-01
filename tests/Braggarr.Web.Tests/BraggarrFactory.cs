using System.Net;
using System.Text.Json;
using Braggarr.Services;
using Braggarr.Web.Auth;
using Braggarr.Web.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Braggarr.Web.Tests;

/// <summary>
/// Braggarr in-process, set up as if the owner (GitHub id 42) had finished setup, with an agent key, and with GitHub and the posts
/// repo replaced by fakes. GitHub's sign-in endpoints answer from <see cref="GitHub"/>: the user they report is <see cref="SignInAs"/>.
/// It starts on first use, on a test server, in the Development environment, as WebApplicationFactory started it.
/// </summary>
public sealed class BraggarrFactory : IDisposable
{
	private WebApplication? _app;
	public const string AgentKey = "brag_test-key-for-the-release-bot";
	public const long OwnerId = 42;

	public string ConfigDirectory { get; } = Directory.CreateTempSubdirectory("braggarr-web-").FullName;

	public MemoryRepository Repository { get; } = new();

	public MemoryReviews Reviews { get; } = new();

	public long SignInAs { get; set; } = OwnerId;

	public EchoDrafter Drafter { get; } = new();

	public StubJudges Judges { get; } = new();

	public MemoryProfiles Profiles { get; } = new();

	public StubHttp GitHub { get; }

	/// <summary>More of GitHub, for a test: asked first, and the stub's own answers apply when it says null.</summary>
	public Func<HttpRequestMessage, HttpResponseMessage?> GitHubAnswers { get; set; } = _ => null;

	/// <summary>Google, for YouTube: set before the first request that needs it.</summary>
	public StubHttp YouTube { get; set; } = new((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));

	/// <summary>
	/// The app's clock, years away from the real one: anything in the app that reads the real clock instead of this shows up as
	/// wrong in a test, rather than happening to agree with it.
	/// </summary>
	public Nullean.Entities.ManualTimeProvider Time { get; } = new(new DateTimeOffset(2030, 1, 7, 9, 0, 0, TimeSpan.Zero));

	public BraggarrFactory()
	{
		GitHub = new StubHttp((request, _) => GitHubAnswers(request) ?? request.RequestUri!.AbsoluteUri switch
		{
			"https://github.com/login/oauth/access_token" => StubHttp.Json("""{"access_token":"gho_user_token"}"""),
			"https://api.github.com/user" => StubHttp.Json(JsonSerializer.Serialize(new { id = SignInAs, login = SignInAs == OwnerId ? "mpdreamz" : "someone-else" })),
			_ => new HttpResponseMessage(HttpStatusCode.NotFound)
		});
		var settings = new SettingsStore(Braggarr.Adapters.ConfigFileSystem.Create(ConfigDirectory), ConfigDirectory);
		_ = settings.Update(_ => new BraggarrSettings
		{
			PostsRepository = "owner/braggarr-posts",
			GitHubApp = new GitHubAppSettings { AppId = 1, Slug = "braggarr-test", ClientId = "client-id" },
			Owner = new OwnerSettings(OwnerId, "mpdreamz"),
			ApiKeys = [new ApiKeySettings("release-bot", ApiKeys.Hash(AgentKey), DateTimeOffset.UnixEpoch)]
		});
		settings.Dispose();
	}

	public SettingsStore Settings => Services.GetRequiredService<SettingsStore>();

	public IServiceProvider Services => App.Services;

	private WebApplication App => _app ??= Start();

	private WebApplication Start()
	{
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development, ApplicationName = Server.ApplicationName });
		_ = builder.WebHost.UseTestServer();
		builder.Configuration["BRAGGARR_CONFIG"] = ConfigDirectory;
		_ = builder.Services.AddBraggarr(ConfigDirectory);
		var services = builder.Services;
		{
			// No scheduler or pollers in tests; the background worker stays, since requests hand it work.
			_ = services.RemoveAll<IHostedService>();
			_ = services.AddHostedService(sp => sp.GetRequiredService<Braggarr.Web.Hosting.BackgroundWork>());
			_ = services.AddSingleton<TimeProvider>(Time);
			_ = services.AddSingleton<ICampaignRepository>(Repository);
			_ = services.AddSingleton<IReviews>(Reviews);
			_ = services.AddSingleton<IManualPosts, NoManualPosts>();
			_ = services.AddSingleton<IDrafters>(Drafter);
			_ = services.AddSingleton<IJudges>(Judges);
			_ = services.AddSingleton<IProfiles>(Profiles);
			_ = services.AddSingleton<IRepositories>(new FixedRepositories());
			_ = services.AddSingleton<ISites>(new Braggarr.Services.Tests.FakeSites());
			_ = services.AddSingleton<IProjectVisibility>(new Braggarr.Services.Tests.FakeVisibility());
			// Nothing reaches the internet: a network a test does not answer for answers 404.
			_ = services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => new StubHttp((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound))));
			_ = services.AddHttpClient("github").ConfigurePrimaryHttpMessageHandler(() => GitHub);
			_ = services.AddHttpClient("youtube").ConfigurePrimaryHttpMessageHandler(() => YouTube);
		}
		var app = builder.Build();
		app.UseBraggarr();
		app.Start();
		return app;
	}

	/// <summary>A client of the test server that keeps cookies and does not follow redirects.</summary>
	private HttpClient CreateClient() =>
		new(new CookieContainerHandler { InnerHandler = App.GetTestServer().CreateHandler() }) { BaseAddress = new Uri("http://localhost") };

	private sealed class FixedRepositories : IRepositories
	{
		public Task<RepositoryInfo> ReadAsync(string repository, string name, CancellationToken ct) =>
			Task.FromResult(new RepositoryInfo(repository, name, "A formatter.", null, [], "# Curb", []));
	}

	public HttpClient Browser() => CreateClient();

	public HttpClient Agent(string key = AgentKey)
	{
		var client = CreateClient();
		client.DefaultRequestHeaders.Add(ApiKeys.Header, key);
		return client;
	}

	/// <summary>Goes through "Sign in with GitHub" as <see cref="SignInAs"/>, and returns where the callback sent the browser.</summary>
	public static async Task<string> SignInAsync(HttpClient browser)
	{
		var login = await browser.GetAsync("/login");
		var state = System.Web.HttpUtility.ParseQueryString(login.Headers.Location!.Query)["state"];
		var callback = await browser.GetAsync($"/login/callback?code=one-time-code&state={state}");
		return callback.Headers.Location!.OriginalString;
	}

	/// <summary>The anti-forgery token from a page's form, to post it back as a browser would.</summary>
	public static async Task<string> FormTokenAsync(HttpClient browser, string page)
	{
		var html = await browser.GetStringAsync(page);
		var marker = "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"";
		var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
		return html[start..html.IndexOf('"', start)];
	}

	public void Dispose()
	{
		if (_app is not null)
		{
			_app.StopAsync().GetAwaiter().GetResult();
			((IDisposable)_app).Dispose();
		}
		try
		{
			Directory.Delete(ConfigDirectory, recursive: true);
		}
		catch (IOException)
		{
		}
	}
}
