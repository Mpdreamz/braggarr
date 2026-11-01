using Braggarr.Adapters.Claude;
using Braggarr.Adapters.Config;
using Braggarr.Adapters.Social;
using Braggarr.Domain;
using Braggarr.Services;
using Braggarr.Web.Auth;
using Braggarr.Web.Settings;

namespace Braggarr.Web.Ui.Settings;

public sealed partial class IndexModel(SettingsStore settings, SecretStore secrets, IHttpClientFactory clients, TimeProvider time, SchedulingService scheduling, UsageLog usage, INotifier notifier, JudgingService judging, AccountStore accounts, Adapters.GitHub.GitHubClient github) : UiPage
{
	public BraggarrSettings Current => settings.Current;

	public YouTubeClient? YouTubeClient => secrets.Get<YouTubeClient>(Publishers.YouTubeClientSecret);

	/// <summary>The redirect URI to register on the Google OAuth client: this page's own address, which must be https.</summary>
	public string YouTubeRedirect => Braggarr.Web.Auth.YouTubeConnect.RedirectUri(HttpContext);

	public sealed record AccountRow(Network Network, string Name, string? Note, bool Warn, bool IsDefault);

	/// <summary>The accounts connected on a network, with when a token runs out and which one is the default for everything.</summary>
	public IReadOnlyList<AccountRow> AccountsOn(Network network)
	{
		var global = DefaultFor(network, null, null);
		return
		[
			.. accounts.On(network).Select(name =>
			{
				DateTimeOffset? expires = network switch
				{
					Network.LinkedIn => accounts.Get<LinkedInCredentials>(network, name)?.ExpiresAt,
					Network.Threads => accounts.Get<ThreadsCredentials>(network, name)?.ExpiresAt,
					_ => null
				};
				return new AccountRow(network, name, expires is { } e ? Expiry(e) : null, expires is { } soon && ExpiresSoon(soon), name == global);
			})
		];
	}

	/// <summary>The networks that post with an account, which have one connected.</summary>
	public IReadOnlyList<Network> PostingNetworks => [.. Networks.All.Where(n => !n.IsManual() && accounts.On(n).Count > 0)];

	/// <summary>Everything, then each GitHub org of a watched project, then each watched project.</summary>
	public IReadOnlyList<(string Label, string? Org, string? Project)> Scopes =>
	[
		("Everything", null, null),
		.. Current.Projects.Select(p => p.Repository.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
			.Select(org => ($"{org}'s projects", (string?)org, (string?)null)),
		.. Current.Projects.Select(p => p.Name).Distinct().Order(StringComparer.OrdinalIgnoreCase).Select(name => (name, (string?)null, (string?)name))
	];

	public string? DefaultFor(Network network, string? org, string? project) =>
		Current.AccountDefaults.FirstOrDefault(d => d.Network == network.Slug() && d.Org == org && d.Project == project)?.Account;

	public static string DefaultField(Network network, string? org, string? project) =>
		$"default|{(project is not null ? "project" : org is not null ? "org" : "global")}|{project ?? org ?? ""}|{network.Slug()}";

	public IResult OnPostDefaults()
	{
		var chosen = new List<AccountDefault>();
		foreach (var (key, value) in Request.Form)
		{
			var parts = key.Split('|');
			if (parts is not ["default", var kind, var scope, var slug] || !Networks.TryParse(slug, out var network) || string.IsNullOrWhiteSpace(value))
			{
				continue;
			}
			var account = value.ToString().Trim();
			if (!accounts.On(network).Contains(account))
			{
				Problem = $"{account} is not a connected {network} account.";
				return Page();
			}
			chosen.Add(kind switch
			{
				"project" => new AccountDefault(network.Slug(), account, Project: scope),
				"org" => new AccountDefault(network.Slug(), account, Org: scope),
				_ => new AccountDefault(network.Slug(), account)
			});
		}
		_ = settings.Update(s => s with { AccountDefaults = chosen });
		Message = "Saved. New proposals post as these; a proposal already open keeps its accounts until you change them on it.";
		return Page();
	}

	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) => (method, handler) switch
	{
		("GET", null) => Shown(() => OnGet(form.Text("youtube"))),
		("POST", "defaults") => Task.FromResult(OnPostDefaults()),
		("POST", "youtubeclient") => Task.FromResult(OnPostYouTubeClient(form.Text("clientId")!, form.Text("clientSecret")!)),
		("POST", "claude") => Task.FromResult(OnPostClaude(form.Text("apiKey"), form.Text("model")!, form.Text("effort")!, form.Text("judgeModel"), form.Text("judgeEffort"))),
		("POST", "judge") => Task.FromResult(OnPostJudge(form.Bool("autoApprove"), form.Number("delayHours"))),
		("POST", "forgetclaude") => Task.FromResult(OnPostForgetClaude()),
		("POST", "general") => Task.FromResult(OnPostGeneral(form.Text("timeZone")!, form.Text("ntfyServer"), form.Text("ntfyTopic"), form.Text("trustedProxies"))),
		("POST", "createkey") => Task.FromResult(OnPostCreateKey(form.Text("name")!)),
		("POST", "revokekey") => Task.FromResult(OnPostRevokeKey(form.Text("name")!)),
		("POST", "bluesky") => OnPostBlueskyAsync(form.Text("handle"), form.Text("appPassword"), form.Text("service"), ct),
		("POST", "mastodon") => OnPostMastodonAsync(form.Text("instance"), form.Text("accessToken"), ct),
		("POST", "linkedin") => OnPostLinkedInAsync(form.Text("accessToken"), form.OptionalNumber("days"), ct),
		("POST", "threads") => OnPostThreadsAsync(form.Text("accessToken"), ct),
		("POST", "x") => OnPostXAsync(form.Text("consumerKey"), form.Text("consumerSecret"), form.Text("accessToken"), form.Text("accessTokenSecret"), ct),
		("POST", "blog") => OnPostBlogAsync(form.Text("repository"), form.Text("branch"), form.Text("folder"), form.Text("baseUrl"), ct),
		("POST", "disconnect") => Task.FromResult(OnPostDisconnect(form.Text("network")!, form.Text("name")!)),
		("POST", "testnotice") => OnPostTestNoticeAsync(ct),
		("POST", "sync") => Task.FromResult(OnPostSync()),
		_ => null
	};

	protected override RazorSlices.RazorSlice Slice() => Index.Create(this);

	/// <summary>Keeps a connected account; the first one on a network becomes its default for everything.</summary>
	private void Keep<T>(Network network, T credentials) where T : class
	{
		var name = AccountStore.NameOf(credentials);
		accounts.Set(network, name, credentials);
		if (!Current.AccountDefaults.Any(d => d.IsGlobal && d.Network == network.Slug()))
		{
			_ = settings.Update(s => s with { AccountDefaults = [.. s.AccountDefaults, new AccountDefault(network.Slug(), name)] });
		}
	}

	/// <summary>Within the week Braggarr warns about, by the app's clock.</summary>
	public bool ExpiresSoon(DateTimeOffset at) => at <= time.GetUtcNow().AddDays(7);

	public string Expiry(DateTimeOffset at)
	{
		var days = (int)Math.Floor((at - time.GetUtcNow()).TotalDays);
		return days < 0 ? "expired" : days == 0 ? "expires today" : $"expires in {days} days";
	}

	/// <summary>A key just made: shown on this one response, never again.</summary>
	public string? NewKey { get; private set; }

	public string? Message { get; private set; }

	public string? Problem { get; private set; }

	public bool HasClaudeKey => secrets.Get(ClaudeOptions.ApiKeySecret) is not null;

	/// <summary>What Claude cost since the first of this month, in dollars, and how many calls that took.</summary>
	public (decimal Dollars, int Calls) ThisMonth
	{
		get
		{
			var now = time.GetUtcNow();
			var entries = usage.Since(new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero));
			return (entries.Sum(e => e.Dollars), entries.Count);
		}
	}

	/// <summary>Which part of the settings is shown: networks, Claude, the judge, agents or general.</summary>
	public string Section => Request.Query["section"].FirstOrDefault() switch
	{
		"claude" or "drafting" => "claude",
		"judge" => "judge",
		"agents" => "agents",
		"general" => "general",
		_ => "networks"
	};

	public string SectionTitle => Section switch { "claude" => "Claude", "judge" => "The judge", "agents" => "Keys for agents", "general" => "General", _ => "Networks" };

	public void OnGet(string? youtube)
	{
		// The outcome of connecting YouTube, as a code: the text is ours, never taken from the URL.
		(Message, Problem) = youtube switch
		{
			"connected" => ("Connected the YouTube channel. It is listed under YouTube below.", (string?)null),
			"no-client" => (null, "Save the Google OAuth client first."),
			"state" => (null, "That answer from Google did not start in this browser, so it was ignored. Try connecting again."),
			"declined" => (null, "Google did not give access to YouTube. Try again, and allow uploading and reading the channel."),
			"failed" => (null, "Google refused to connect YouTube; the log says why. Check the client id, the secret and the redirect URI."),
			_ => (null, null)
		};
	}

	public IResult OnPostYouTubeClient(string clientId, string clientSecret)
	{
		if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
		{
			Problem = "Both the client id and the client secret are needed.";
			return Page();
		}
		secrets.Set(Publishers.YouTubeClientSecret, new YouTubeClient(clientId.Trim(), clientSecret.Trim()));
		Message = "Saved the Google OAuth client. Now connect your channel.";
		return Page();
	}

	public IResult OnPostClaude(string? apiKey, string model, string effort, string? judgeModel, string? judgeEffort)
	{
		if (!ClaudeOptions.Models.Contains(model) || !IsEffort(effort)
			|| (!string.IsNullOrEmpty(judgeModel) && !ClaudeOptions.Models.Contains(judgeModel)) || !(string.IsNullOrEmpty(judgeEffort) || IsEffort(judgeEffort)))
		{
			Problem = "Choose one of the listed models and efforts.";
			return Page();
		}
		if (!string.IsNullOrWhiteSpace(apiKey))
		{
			secrets.Set(ClaudeOptions.ApiKeySecret, apiKey.Trim());
		}
		_ = settings.Update(s => s with
		{
			ClaudeModel = model,
			ClaudeEffort = effort,
			JudgeModel = string.IsNullOrEmpty(judgeModel) ? null : judgeModel,
			JudgeEffort = string.IsNullOrEmpty(judgeEffort) ? null : judgeEffort
		});
		Message = HasClaudeKey ? $"Writing with {model} at {effort} effort." : "Saved. Claude starts once there is a key.";
		return Page();
	}

	private static bool IsEffort(string? effort) => effort is "low" or "medium" or "high";

	/// <summary>This month's calls and dollars by what they were for, most expensive first.</summary>
	public IReadOnlyList<(string Use, int Calls, decimal Dollars)> ThisMonthByUse
	{
		get
		{
			var now = time.GetUtcNow();
			return
			[
				.. from e in usage.Since(new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero))
				group e by Use(e.Purpose) into g
				orderby g.Sum(e => e.Dollars) descending
				select (g.Key, g.Count(), g.Sum(e => e.Dollars))
			];
		}
	}

	private static string Use(string purpose) => purpose.Split(' ', 2)[0] switch
	{
		"draft" => "writing variants",
		"points" => "talking points",
		"plan" => "introductions",
		"describe" => "alt text",
		"judge" => "judging",
		"brief" => "project briefs",
		"profile" => "profiles",
		var other => other
	};

	/// <summary>The criteria the judge reads from <c>judges/</c> on main, or why they cannot be read.</summary>
	public Task<JudgeState> JudgeAsync() => judging.StateAsync(HttpContext.RequestAborted);

	public IResult OnPostJudge(bool autoApprove, int delayHours)
	{
		if (delayHours is < 0 or > 24 * 14)
		{
			Problem = "The delay is between 0 hours and two weeks.";
			return Page();
		}
		_ = settings.Update(s => s with { JudgeAutoApprove = autoApprove, JudgeDelayHours = delayHours });
		Message = autoApprove
			? $"The judge may approve what passes every criterion; nothing it approves goes out for {delayHours} hours."
			: "Saved. The judge judges, and only you approve.";
		return Page();
	}

	public IResult OnPostForgetClaude()
	{
		secrets.Set(ClaudeOptions.ApiKeySecret, null);
		Message = "The Claude key is gone. Proposals without text stay incomplete until you write it.";
		return Page();
	}

	public IResult OnPostGeneral(string timeZone, string? ntfyServer, string? ntfyTopic, string? trustedProxies)
	{
		if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone.Trim(), out _))
		{
			Problem = $"\"{timeZone}\" is not a time zone this system knows. Use a name like Europe/Amsterdam.";
			return Page();
		}
		_ = settings.Update(s => s with
		{
			TimeZone = timeZone.Trim(),
			NtfyServer = string.IsNullOrWhiteSpace(ntfyServer) ? "https://ntfy.sh" : ntfyServer.Trim(),
			NtfyTopic = string.IsNullOrWhiteSpace(ntfyTopic) ? null : ntfyTopic.Trim(),
			TrustedProxies = [.. (trustedProxies ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
		});
		Message = "Saved. A change to trusted proxies applies after a restart.";
		return Page();
	}

	public IResult OnPostCreateKey(string name)
	{
		name = name?.Trim() ?? "";
		if (name.Length is 0 or > 40 || Current.ApiKeys.Any(k => k.Name == name))
		{
			Problem = "Give the key a name of its own, such as the agent that will use it.";
			return Page();
		}
		var key = ApiKeys.New();
		_ = settings.Update(s => s with { ApiKeys = [.. s.ApiKeys, new ApiKeySettings(name, ApiKeys.Hash(key), time.GetUtcNow())] });
		NewKey = key;
		Message = $"The key for {name}. Copy it now: Braggarr keeps only its hash and cannot show it again.";
		return Page();
	}

	public IResult OnPostRevokeKey(string name)
	{
		_ = settings.Update(s => s with { ApiKeys = [.. s.ApiKeys.Where(k => k.Name != name)] });
		Message = $"The key for {name} no longer works.";
		return Page();
	}

	public async Task<IResult> OnPostBlueskyAsync(string? handle, string? appPassword, string? service, CancellationToken ct)
	{
		if (handle is null || appPassword is null)
		{
			Problem = "Both the handle and the app password are needed.";
			return Page();
		}
		var credentials = new BlueskyCredentials(handle.Trim().TrimStart('@'), appPassword.Trim(), string.IsNullOrWhiteSpace(service) ? "https://bsky.social" : service.Trim());
		try
		{
			var who = await new BlueskyPublisher(clients.CreateClient("bluesky"), credentials, time).TestAsync(ct);
			Keep(Network.Bluesky, credentials);
			Message = $"Connected to Bluesky as {who}.";
		}
		catch (Exception e) when (e is PublishException or HttpRequestException)
		{
			Problem = e.Message;
		}
		return Page();
	}

	public async Task<IResult> OnPostMastodonAsync(string? instance, string? accessToken, CancellationToken ct)
	{
		if (instance is null || accessToken is null)
		{
			Problem = "Both the server and the access token are needed.";
			return Page();
		}
		var url = instance.Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase) ? instance.Trim() : "https://" + instance.Trim();
		var credentials = new MastodonCredentials(url, accessToken.Trim());
		try
		{
			var who = await new MastodonPublisher(clients.CreateClient("mastodon"), credentials, time).TestAsync(ct);
			Keep(Network.Mastodon, credentials);
			Message = $"Connected to Mastodon as {who}.";
		}
		catch (Exception e) when (e is PublishException or HttpRequestException or UriFormatException)
		{
			Problem = e.Message;
		}
		return Page();
	}

	public async Task<IResult> OnPostLinkedInAsync(string? accessToken, int? days, CancellationToken ct)
	{
		if (accessToken is null)
		{
			Problem = "The access token is needed.";
			return Page();
		}
		try
		{
			var (urn, name) = await LinkedInPublisher.WhoAsync(clients.CreateClient("linkedin"), accessToken.Trim(), ct);
			var expires = time.GetUtcNow().AddDays(days is > 0 and <= 365 ? days.Value : 60);
			Keep(Network.LinkedIn, new LinkedInCredentials(accessToken.Trim(), urn, name, expires));
			Message = $"Connected to LinkedIn as {name}. The token {Expiry(expires)}; Braggarr warns you a week before.";
		}
		catch (Exception e) when (e is PublishException or HttpRequestException)
		{
			Problem = e.Message;
		}
		return Page();
	}

	public async Task<IResult> OnPostThreadsAsync(string? accessToken, CancellationToken ct)
	{
		if (accessToken is null)
		{
			Problem = "The access token is needed.";
			return Page();
		}
		try
		{
			var (id, username) = await ThreadsPublisher.WhoAsync(clients.CreateClient("threads"), accessToken.Trim(), ct);
			var now = time.GetUtcNow();
			Keep(Network.Threads, new ThreadsCredentials(accessToken.Trim(), id, username, now.AddDays(60), now));
			Message = $"Connected to Threads as @{username}. Braggarr refreshes the token before it runs out.";
		}
		catch (Exception e) when (e is PublishException or HttpRequestException)
		{
			Problem = e.Message;
		}
		return Page();
	}

	public async Task<IResult> OnPostXAsync(string? consumerKey, string? consumerSecret, string? accessToken, string? accessTokenSecret, CancellationToken ct)
	{
		if (consumerKey is null || consumerSecret is null || accessToken is null || accessTokenSecret is null)
		{
			Problem = "All four keys are needed: the API key and its secret, and the access token and its secret.";
			return Page();
		}
		var keys = new XCredentials(consumerKey.Trim(), consumerSecret.Trim(), accessToken.Trim(), accessTokenSecret.Trim());
		try
		{
			var username = await new XPublisher(clients.CreateClient("x"), keys, time).WhoAsync(ct);
			Keep(Network.X, keys with { Username = username });
			Message = $"Connected to X as @{username}.";
		}
		catch (Exception e) when (e is PublishException or HttpRequestException)
		{
			Problem = e.Message;
		}
		return Page();
	}

	/// <summary>A blog: the site's repository, tested by reading its braggarr.yml through Braggarr's GitHub App.</summary>
	public async Task<IResult> OnPostBlogAsync(string? repository, string? branch, string? folder, string? baseUrl, CancellationToken ct)
	{
		var repo = (repository ?? "").Trim().Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
		if (!SiteRepository().IsMatch(repo) || string.IsNullOrWhiteSpace(branch) || string.IsNullOrWhiteSpace(folder))
		{
			Problem = "Name the site's repository as owner/name, with its branch and posts folder.";
			return Page();
		}
		if (baseUrl is not null && !(Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var root) && root.Scheme is "https" or "http"))
		{
			Problem = "The address the site is served from is a full web address, such as https://www.example.com/.";
			return Page();
		}
		var site = new Adapters.Blog.BlogSite(repo, branch.Trim(), folder.Trim().Trim('/'), baseUrl?.Trim());
		try
		{
			var title = await new Adapters.Blog.BlogPublisher(github, site).TestAsync(ct);
			Keep(Network.Blog, site);
			Message = $"Connected the blog \"{title}\" ({repo}). Its posts go to {site.Folder}/ on {site.Branch}.";
		}
		catch (Exception e) when (e is PublishException or HttpRequestException)
		{
			Problem = e.Message;
		}
		return Page();
	}

	[System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
	private static partial System.Text.RegularExpressions.Regex SiteRepository();

	public IResult OnPostDisconnect(string network, string name)
	{
		if (!Networks.TryParse(network, out var which) || !accounts.On(which).Contains(name))
		{
			Problem = "That account is not connected.";
			return Page();
		}
		secrets.Set(AccountStore.Key(which, name), null);
		_ = settings.Update(s => s with { AccountDefaults = [.. s.AccountDefaults.Where(d => !(d.Network == which.Slug() && d.Account == name))] });
		Message = $"Disconnected {name} from {which}. Posts for it wait until it is connected again, or until you choose another account on them.";
		return Page();
	}

	public async Task<IResult> OnPostTestNoticeAsync(CancellationToken ct)
	{
		await notifier.NotifyAsync(
			new Notice(NoticeLevel.Action, "Braggarr says hello", "This is what a notice looks like. Tap to open Braggarr.", $"{Request.Scheme}://{Request.Host}/"),
			ct
		);
		Message = Current.NtfyTopic is null ? "No ntfy topic is set: the notice only went to the log." : $"Sent to ntfy topic {Current.NtfyTopic}.";
		return Page();
	}

	public IResult OnPostSync()
	{
		scheduling.RequestSync();
		Message = "The posts repo is fetched on the next run, within 30 seconds.";
		return Page();
	}

}
