namespace Braggarr.Web.Settings;

/// <summary>
/// Everything in <c>/config/config.json</c>. Nothing secret: credentials live encrypted in <c>secrets.json</c>.
/// </summary>
/// <remarks>
/// Setters, not <c>init</c>, here and on the settings below: the generated JSON reader sets an init-only property even when
/// the file leaves it out, to the type's default (null, 0), not to its initializer. A file written before a setting existed
/// must read it as the default.
/// </remarks>
public sealed record BraggarrSettings
{
	/// <summary>The posts repo, <c>owner/name</c>.</summary>
	public string? PostsRepository { get; set; }

	public GitHubAppSettings GitHubApp { get; set; } = new();

	/// <summary>The one GitHub account that may sign in, by numeric id: a login can be renamed and taken, an id cannot.</summary>
	public OwnerSettings? Owner { get; set; }

	public string TimeZone { get; set; } = "Europe/Amsterdam";

	/// <summary>The model that drafts variants. The key is a secret, kept encrypted.</summary>
	public string ClaudeModel { get; set; } = "claude-opus-5-5";

	public string ClaudeEffort { get; set; } = "medium";

	/// <summary>The model that judges proposals against the criteria in <c>judges/</c>; null: the drafting model.</summary>
	public string? JudgeModel { get; set; }

	public string? JudgeEffort { get; set; }

	/// <summary>Whether the judge may approve a proposal every criterion passes. Off unless the owner turns it on.</summary>
	public bool JudgeAutoApprove { get; set; }

	/// <summary>How long after the judge approves before its first post may go out, so the owner can look first.</summary>
	public int JudgeDelayHours { get; set; } = 12;

	public string NtfyServer { get; set; } = "https://ntfy.sh";

	public string? NtfyTopic { get; set; }

	public IReadOnlyList<ApiKeySettings> ApiKeys { get; set; } = [];

	/// <summary>Which account posts for whom, per network: everything, a GitHub org, or a project. The most specific wins.</summary>
	public IReadOnlyList<AccountDefault> AccountDefaults { get; set; } = [];

	/// <summary>The calendar's rebalance options, as last used.</summary>
	public RebalanceSettings Rebalance { get; set; } = new();

	/// <summary>Projects whose GitHub releases become proposed campaigns.</summary>
	public IReadOnlyList<ProjectSettings> Projects { get; set; } = [];

	/// <summary>The groups projects are sorted into on the blog's about page, in the order the page shows them.</summary>
	public IReadOnlyList<ProjectGroupSettings> ProjectGroups { get; set; } = [];

	/// <summary>
	/// Proxies whose X-Forwarded-* headers are believed: addresses, or networks such as the Docker network Braggarr shares with
	/// a reverse proxy (<c>172.19.0.0/16</c>). Empty: no forwarded header is read.
	/// </summary>
	public IReadOnlyList<string> TrustedProxies { get; set; } = [];

	public bool IsSetUp => Owner is not null && GitHubApp.AppId > 0 && !string.IsNullOrEmpty(PostsRepository);
}

public sealed record GitHubAppSettings
{
	public long AppId { get; set; }

	public string? Slug { get; set; }

	public string? ClientId { get; set; }
}

public sealed record OwnerSettings(long Id, string Login);

/// <summary>An agent's key: only its SHA-256 hash is kept. The key itself is shown once, when it is made.</summary>
public sealed record ApiKeySettings(string Name, string Hash, DateTimeOffset Created);

/// <summary>A watched project. <see cref="LastSeenTag"/> is the newest release already handled; null until the first check.</summary>
public sealed record ProjectSettings(
	string Repository,
	string Name,
	IReadOnlyList<string> Networks,
	string? LastSeenTag = null,
	bool IncludePrereleases = false,
	bool AskClaude = true,
	SkippedRelease? LastSkipped = null,
	string? Group = null
);

/// <summary>A group on the blog's about page: its title, and a line on what the group is.</summary>
public sealed record ProjectGroupSettings(string Title, string? Description = null);

/// <summary>
/// The account a network posts as for everything (neither <see cref="Org"/> nor <see cref="Project"/>), for a GitHub org's
/// projects, or for one project.
/// </summary>
public sealed record AccountDefault(string Network, string Account, string? Org = null, string? Project = null)
{
	public bool IsGlobal => Org is null && Project is null;
}

/// <summary>Rest days by name (<c>Saturday</c>), posts per day per network, and the posting hours as <c>HH:mm</c>.</summary>
public sealed record RebalanceSettings
{
	public IReadOnlyList<string> RestDays { get; set; } = ["Saturday", "Sunday"];

	public int PerDay { get; set; } = 1;

	public string From { get; set; } = "09:00";

	public string To { get; set; } = "17:00";

	public Domain.RebalanceOptions ToOptions() =>
		new(
			[.. RestDays.Select(d => Enum.TryParse<DayOfWeek>(d, out var day) ? (DayOfWeek?)day : null).OfType<DayOfWeek>()],
			PerDay,
			TimeOnly.TryParse(From, System.Globalization.CultureInfo.InvariantCulture, out var from) ? from : new TimeOnly(9, 0),
			TimeOnly.TryParse(To, System.Globalization.CultureInfo.InvariantCulture, out var to) ? to : new TimeOnly(17, 0)
		);
}

/// <summary>A release Claude judged not worth a post, and why.</summary>
public sealed record SkippedRelease(string Tag, string Reason, DateTimeOffset At);
