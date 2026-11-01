using System.Net;
using System.Threading.RateLimiting;
using Braggarr.Adapters;
using Braggarr.Adapters.Claude;
using Braggarr.Adapters.Config;
using Braggarr.Adapters.Git;
using Braggarr.Adapters.GitHub;
using Braggarr.Adapters.Social;
using Braggarr.Services;
using Braggarr.Web.Api;
using Braggarr.Web.Auth;
using Braggarr.Web.Hosting;
using Braggarr.Web.Settings;
using Braggarr.Web.Ui;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Braggarr.Web;

public static class Composition
{
	public static IServiceCollection AddBraggarr(this IServiceCollection services, string configDirectory)
	{
		// Everything Braggarr touches on disk goes through one file system, scoped to /config.
		_ = new System.IO.Abstractions.FileSystem().Directory.CreateDirectory(configDirectory);
		var fs = ConfigFileSystem.Create(configDirectory);
		_ = services.AddSingleton(fs);
		var settings = new SettingsStore(fs, configDirectory);
		_ = services.AddSingleton(settings);
		services.AddSingleton(TimeProvider.System);
		_ = services.AddDataProtection().SetApplicationName("Braggarr");
		_ = services.Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(
			o => o.XmlRepository = new KeyRepository(fs, Path.Combine(configDirectory, "keys"))
		);
		var redactions = new RedactionList();
		_ = services.AddSingleton(redactions);
		_ = services.AddLogging(logging => logging.AddRedactingConsole(redactions));
		_ = services.AddSingleton(sp =>
		{
			var store = new SecretStore(sp.GetRequiredService<IDataProtectionProvider>(), fs, Path.Combine(configDirectory, "secrets.json"));
			redactions.Update(store.Values());
			store.Changed += () => redactions.Update(store.Values());
			return store;
		});

		// Options that follow config.json and the secrets: rebuilt whenever either changes.
		_ = services.AddOptions<PostsRepositoryOptions>().Configure<SettingsStore>((o, s) =>
		{
			o.Repository = s.Current.PostsRepository ?? "";
			o.Directory = Path.Combine(s.ConfigDirectory, "posts");
		});
		_ = services.AddOptions<GitHubAppOptions>().Configure<SettingsStore, SecretStore>((o, s, secrets) =>
		{
			o.AppId = s.Current.GitHubApp.AppId;
			o.Slug = s.Current.GitHubApp.Slug;
			o.ClientId = s.Current.GitHubApp.ClientId;
			o.ClientSecret = secrets.Get(GitHubSignIn.GitHubSecretClient);
			o.PrivateKeyPem = secrets.Get(GitHubSignIn.GitHubSecretKey);
		});
		_ = services.AddOptions<NtfyOptions>().Configure<SettingsStore>((o, s) =>
		{
			o.Server = s.Current.NtfyServer;
			o.Topic = s.Current.NtfyTopic;
		});
		_ = services.AddOptions<ClaudeOptions>().Configure<SettingsStore, SecretStore>((o, s, secrets) =>
		{
			o.ApiKey = secrets.Get(ClaudeOptions.ApiKeySecret);
			o.Model = s.Current.ClaudeModel;
			o.Effort = s.Current.ClaudeEffort;
		});
		_ = services.AddOptions<JudgeOptions>().Configure<SettingsStore, SecretStore>((o, s, secrets) =>
		{
			o.ApiKey = secrets.Get(ClaudeOptions.ApiKeySecret);
			o.Model = s.Current.JudgeModel ?? s.Current.ClaudeModel;
			o.Effort = s.Current.JudgeEffort ?? s.Current.ClaudeEffort;
		});
		_ = services.AddOptions<JudgingOptions>().Configure<SettingsStore>((o, s) =>
		{
			o.AutoApprove = s.Current.JudgeAutoApprove;
			o.Delay = TimeSpan.FromHours(s.Current.JudgeDelayHours);
		});
		_ = services.AddOptions<SchedulingOptions>();
		_ = services.AddSingleton<IOptionsChangeTokenSource<PostsRepositoryOptions>, SettingsStore.ChangeTokenSource<PostsRepositoryOptions>>();
		_ = services.AddSingleton<IOptionsChangeTokenSource<GitHubAppOptions>, SettingsStore.ChangeTokenSource<GitHubAppOptions>>();
		_ = services.AddSingleton<IOptionsChangeTokenSource<NtfyOptions>, SettingsStore.ChangeTokenSource<NtfyOptions>>();
		_ = services.AddSingleton<IOptionsChangeTokenSource<ClaudeOptions>, SettingsStore.ChangeTokenSource<ClaudeOptions>>();
		_ = services.AddSingleton<IOptionsChangeTokenSource<JudgeOptions>, SettingsStore.ChangeTokenSource<JudgeOptions>>();
		_ = services.AddSingleton<IOptionsChangeTokenSource<JudgingOptions>, SettingsStore.ChangeTokenSource<JudgingOptions>>();

		_ = services.AddHttpClient("github", c => c.Timeout = TimeSpan.FromSeconds(30));
		_ = services.AddHttpClient("bluesky");
		_ = services.AddHttpClient("mastodon");
		_ = services.AddHttpClient("linkedin", c => c.Timeout = TimeSpan.FromSeconds(60));
		_ = services.AddHttpClient("threads", c => c.Timeout = TimeSpan.FromSeconds(30));
		_ = services.AddHttpClient("x", c => c.Timeout = TimeSpan.FromSeconds(60));
		// One request carries a whole video: as long as the scheduler gives a post with a video.
		_ = services.AddHttpClient("youtube", c => c.Timeout = TimeSpan.FromMinutes(15));
		_ = services.AddHttpClient("ntfy", c => c.Timeout = TimeSpan.FromSeconds(10));
		// Whether a committed blog post answers at its address yet.
		_ = services.AddHttpClient("sites", c => c.Timeout = TimeSpan.FromSeconds(30));
		_ = services.AddSingleton<ISites>(sp => new Adapters.Blog.SiteProbe(Client(sp, "sites")));
		// Only a public project gets a blog post: its watched repository, else the first GitHub repository it links.
		_ = services.AddSingleton<IProjectVisibility>(sp => new GitHubProjectVisibility(
			sp.GetRequiredService<GitHubClient>(),
			c => settings.Current.Projects.FirstOrDefault(p => Domain.ProjectBrief.Slug(p.Name) == Domain.ProjectBrief.Slug(c.Project))?.Repository ?? GitHubProjectVisibility.FromLinks(c.Links)
		));
		// Drafting thinks before it writes: allow it minutes, not seconds.
		_ = services.AddHttpClient("anthropic", c => c.Timeout = TimeSpan.FromMinutes(5));

		// The adapters behind the ports. Singletons: they hold caches, locks and the clone.
		_ = services.AddSingleton(sp => new GitHubAppToken(
			Client(sp, "github"), sp.GetRequiredService<IOptionsMonitor<GitHubAppOptions>>(), sp.GetRequiredService<IOptionsMonitor<PostsRepositoryOptions>>(),
			sp.GetRequiredService<TimeProvider>()));
		_ = services.AddSingleton<IGitHubToken>(sp => sp.GetRequiredService<GitHubAppToken>());
		_ = services.AddSingleton(sp => new GitHubClient(
			Client(sp, "github"), sp.GetRequiredService<IGitHubToken>(), sp.GetRequiredService<IOptionsMonitor<GitHubAppOptions>>(),
			sp.GetRequiredService<IOptionsMonitor<PostsRepositoryOptions>>()));
		_ = services.AddSingleton<ICampaignRepository, GitCampaignRepository>();
		_ = services.AddSingleton<IReviews, GitHubReviews>();
		_ = services.AddSingleton<IManualPosts, GitHubManualPosts>();
		_ = services.AddSingleton<AccountStore>();
		_ = services.AddSingleton<SettingsAccounts>();
		_ = services.AddSingleton<IAccounts>(sp => sp.GetRequiredService<SettingsAccounts>());
		_ = services.AddSingleton<IPublishers, Publishers>();
		_ = services.AddSingleton<IProfiles, NetworkProfiles>();
		_ = services.AddSingleton<ProfileService>();
		_ = services.AddSingleton<INotifier>(sp => new NtfyNotifier(Client(sp, "ntfy"), sp.GetRequiredService<IOptionsMonitor<NtfyOptions>>(), sp.GetRequiredService<ILogger<NtfyNotifier>>()));
		_ = services.AddSingleton<InMemoryJournal>();
		_ = services.AddSingleton<IDeliveryJournal>(sp => sp.GetRequiredService<InMemoryJournal>());

		_ = services.AddSingleton<IDrafters, ClaudeDrafters>();
		_ = services.AddSingleton<IJudges, ClaudeJudges>();
		_ = services.AddSingleton(new UsageLog(fs, Path.Combine(configDirectory, "usage.jsonl")));
		_ = services.AddSingleton(new FileVideoStore(fs, Path.Combine(configDirectory, "media")));
		_ = services.AddSingleton<IVideoStore>(sp => sp.GetRequiredService<FileVideoStore>());
		_ = services.AddSingleton<IUsageLog>(sp => sp.GetRequiredService<UsageLog>());
		_ = services.AddSingleton<DraftingService>();
		_ = services.AddSingleton<SchedulingService>();
		_ = services.AddSingleton<CampaignService>();
		_ = services.AddSingleton<JudgingService>();
		_ = services.AddSingleton<BriefService>();
		_ = services.AddHostedService<JudgeHost>();
		_ = services.AddSingleton<SetupToken>();
		_ = services.AddSingleton<Clock>();
		_ = services.AddSingleton<IOwnerTime>(sp => sp.GetRequiredService<Clock>());
		_ = services.AddSingleton<Repeater>();
		_ = services.AddHostedService<RepeatHost>();
		_ = services.AddSingleton<IReleases, GitHubReleases>();
		_ = services.AddSingleton<IRepositories, GitHubRepositories>();
		_ = services.AddSingleton<Introducer>();
		_ = services.AddSingleton<BackgroundWork>();
		_ = services.AddHostedService(sp => sp.GetRequiredService<BackgroundWork>());
		_ = services.AddSingleton<IWatchedProjects, SettingsProjects>();
		_ = services.AddSingleton<ReleaseWatcher>();
		_ = services.AddHostedService<ReleaseHost>();
		_ = services.AddSingleton<TokenKeeper>();
		_ = services.AddHostedService<TokenHost>();
		_ = services.AddSingleton<SchedulerHost>();
		_ = services.AddHostedService(sp => sp.GetRequiredService<SchedulerHost>());

		_ = services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
			.AddCookie(o =>
			{
				o.Cookie.Name = "braggarr";
				o.Cookie.HttpOnly = true;
				o.Cookie.SameSite = SameSiteMode.Lax; // the GitHub sign-in comes back as a top-level navigation
				o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
				o.ExpireTimeSpan = TimeSpan.FromDays(14);
				o.SlidingExpiration = true;
				o.LoginPath = "/login";
				o.AccessDeniedPath = "/denied";
				o.Events.OnValidatePrincipal = context =>
				{
					// A session ends when the owner it was for is no longer the owner, and setup sessions end once there is one.
					var owner = context.HttpContext.RequestServices.GetRequiredService<SettingsStore>().Current.Owner;
					var principal = context.Principal!;
					var stale = principal.HasClaim(Roles.ClaimType, Roles.Owner)
						? principal.FindFirst(Roles.OwnerIdClaim)?.Value != owner?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
						: owner is not null;
					if (stale)
					{
						context.RejectPrincipal();
						return context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
					}
					return Task.CompletedTask;
				};
			})
			.AddScheme<AuthenticationSchemeOptions, ApiKeyHandler>(ApiKeys.Scheme, null);

		_ = services.AddAuthorizationBuilder()
			.AddPolicy(Roles.OwnerPolicy, p => p.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme).RequireClaim(Roles.ClaimType, Roles.Owner))
			.AddPolicy(Roles.SetupPolicy, p => p.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme).RequireClaim(Roles.ClaimType, Roles.Owner, Roles.Setup))
			.AddPolicy(Roles.AgentPolicy, p => p.AddAuthenticationSchemes(ApiKeys.Scheme).RequireAuthenticatedUser());

		_ = services.AddRateLimiter(o =>
		{
			o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
			o.AddPolicy("per-key", http => RateLimitPartition.GetFixedWindowLimiter(
				http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
				_ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
		});

		_ = services.Configure<ForwardedHeadersOptions>(o =>
		{
			// ASP.NET believes forwarded headers from anyone when both lists are empty, so with no trusted proxies none are read.
			o.KnownProxies.Clear();
			o.KnownIPNetworks.Clear();
			foreach (var proxy in settings.Current.TrustedProxies)
			{
				if (System.Net.IPNetwork.TryParse(proxy, out var network))
				{
					o.KnownIPNetworks.Add(network);
				}
				else if (IPAddress.TryParse(proxy, out var address))
				{
					o.KnownProxies.Add(address);
				}
			}
			o.ForwardedHeaders = o.KnownProxies.Count + o.KnownIPNetworks.Count == 0
				? ForwardedHeaders.None
				: ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
		});

		_ = services.AddOpenApi();
		_ = services.AddHttpContextAccessor();
		_ = services.AddMcpServer(o => o.ServerInfo = new() { Name = "braggarr", Version = "1.0" })
			.WithHttpTransport(o => o.Stateless = true)
			.WithTools<Braggarr.Web.Mcp.BraggarrTools>(Braggarr.Web.Mcp.BraggarrTools.Json);
		_ = services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJson.Default));
		_ = services.AddAntiforgery();
		_ = services.AddUi();
		return services;
	}

	public static WebApplication UseBraggarr(this WebApplication app)
	{
		_ = app.UseForwardedHeaders();
		_ = app.UseSecurityHeaders();
		_ = app.UseStatusCodePages();
		// The stylesheet and script are embedded, so the AOT binary serves them with no wwwroot beside it.
		app.Environment.WebRootFileProvider = new Microsoft.Extensions.FileProviders.ManifestEmbeddedFileProvider(typeof(Composition).Assembly, "wwwroot");
		_ = app.UseStaticFiles();
		_ = app.UseRouting();
		_ = app.UseAuthentication();
		_ = app.UseAuthorization();
		_ = app.UseRateLimiter();

		_ = app.MapGet("/healthz", () => Results.Ok("ok")).AllowAnonymous();
		_ = app.MapOpenApi().RequireAuthorization(Roles.AgentPolicy);
		app.MapGitHubSignIn();
		app.MapYouTubeConnect();
		app.MapCampaignApi();
		app.MapMedia();
		// The same keys, the same rate limit and the same powers as /api/v1: nothing here approves or publishes.
		_ = app.MapMcp("/mcp").RequireAuthorization(Roles.AgentPolicy).RequireRateLimiting("per-key").DisableAntiforgery();
		app.MapUi();

		// Read the secrets now, so the log redacts them from the first line on.
		_ = app.Services.GetRequiredService<SecretStore>();
		// Before anything publishes: the one connection per network from before accounts becomes an account and its default.
		app.Services.GetRequiredService<SettingsAccounts>().MoveSingleConnections();
		var settings = app.Services.GetRequiredService<SettingsStore>();
		if (settings.Current.Owner is null)
		{
			var token = app.Services.GetRequiredService<SetupToken>().Value;
			app.Logger.LogWarning("Braggarr is not set up yet. Open /setup/start?token={Token} on this host to begin. The token works until the owner signs in.", token);
		}
		return app;
	}

	private static HttpClient Client(IServiceProvider sp, string name) => sp.GetRequiredService<IHttpClientFactory>().CreateClient(name);
}
