using Braggarr.Domain;

namespace Braggarr.Services;

/// <summary>What the posts repo holds on <c>main</c>: the approved and held campaigns, and every delivery ever recorded.</summary>
public sealed record RepositoryState(IReadOnlyList<Campaign> Campaigns, IReadOnlyList<Delivery> Deliveries);

/// <summary>The posts repo: campaigns on <c>main</c> and the delivery log next to them.</summary>
public interface ICampaignRepository
{
	/// <summary>Fetches <c>main</c> and reads it.</summary>
	Task<RepositoryState> PullAsync(CancellationToken ct);

	/// <summary>Records one delivery durably (a local commit) before returning. It is pushed later by <see cref="PushAsync"/>.</summary>
	Task RecordAsync(Delivery delivery, CancellationToken ct);

	Task PushAsync(CancellationToken ct);

	Task SetHeldAsync(string slug, bool held, Actor by, CancellationToken ct);

	/// <summary>Records an approval in <c>campaign.md</c> on main: who, what (the digest), when, and from when it may go out.</summary>
	Task StampAsync(string slug, Approval approval, CancellationToken ct);

	/// <summary>
	/// The owner's judge criteria, from <c>judges/</c> on main only: a proposal's branch never decides how it is judged, so an
	/// agent cannot add a lenient criterion to its own pull request.
	/// </summary>
	Task<IReadOnlyList<Criterion>> CriteriaAsync(CancellationToken ct);

	/// <summary>Records the judge's verdict on a campaign on main, in its <c>judgement.yml</c>.</summary>
	Task SetJudgementAsync(string slug, Judgement judgement, CancellationToken ct);

	/// <summary>The projects' briefs, from <c>projects/</c> on main: what drafting writes from and the judge reads.</summary>
	Task<IReadOnlyList<ProjectBrief>> BriefsAsync(CancellationToken ct);

	/// <summary>Keeps a project's brief in <c>projects/&lt;slug&gt;.md</c> on main, as a commit by <paramref name="by"/>, and pushes.</summary>
	Task SetBriefAsync(ProjectBrief brief, Actor by, CancellationToken ct);

	/// <summary>Sets how often an approved campaign repeats (0: never), as a commit on main.</summary>
	Task SetRepeatAsync(string slug, int months, Actor by, CancellationToken ct);

	/// <summary>Records on an approved campaign that it was proposed again at <paramref name="at"/>.</summary>
	Task MarkRepeatedAsync(string slug, DateTimeOffset at, CancellationToken ct);

	/// <summary>Rewrites one post of an approved campaign on <c>main</c> (its file only), as a commit by <paramref name="by"/>, and pushes.</summary>
	Task UpdatePostAsync(string slug, Post post, Actor by, CancellationToken ct);

	/// <summary>An image of an approved campaign, by its path in the campaign folder (<c>media/card.png</c>). Null when it is not there.</summary>
	Task<byte[]?> ReadMediaAsync(string slug, string path, CancellationToken ct);

	/// <summary><c>voice.md</c> on <c>main</c>: how the owner wants posts to sound. Null when there is none.</summary>
	Task<string?> VoiceAsync(CancellationToken ct);
}

/// <summary>A proposed campaign: an open pull request on the posts repo.</summary>
public sealed record Proposal(int Number, string Url, Campaign Campaign);

/// <summary>Proposals as pull requests. Merging one approves it.</summary>
public interface IReviews
{
	Task<Proposal> ProposeAsync(Campaign campaign, Actor by, CancellationToken ct);

	Task<IReadOnlyList<Proposal>> OpenAsync(CancellationToken ct);

	/// <summary>
	/// Replaces what the proposal says with <paramref name="campaign"/>, as a new commit on its branch, adding <paramref name="media"/>
	/// (paths in the campaign folder, such as <c>media/card.png</c>) in the same commit.
	/// </summary>
	Task<Proposal> UpdateAsync(Proposal proposal, Campaign campaign, Actor by, CancellationToken ct, IReadOnlyDictionary<string, byte[]>? media = null);

	/// <summary>An image on the proposal's branch.</summary>
	Task<byte[]?> ReadMediaAsync(Proposal proposal, string path, CancellationToken ct);

	Task MergeAsync(Proposal proposal, Actor by, CancellationToken ct);

	Task CloseAsync(Proposal proposal, Actor by, CancellationToken ct);
}

/// <summary>A link that opens a network's submit form already filled in.</summary>
public sealed record SubmitLink(string Label, string Url);

/// <summary>The issue that asks the owner to post, and the links that do it.</summary>
public sealed record ManualPost(int Issue, string IssueUrl, IReadOnlyList<SubmitLink> Submit);

/// <summary>Reddit and Hacker News: an issue per delivery, which the owner closes once they posted it.</summary>
public interface IManualPosts
{
	Task<ManualPost> OpenAsync(Campaign campaign, Post post, Variant variant, CancellationToken ct);

	Task<IReadOnlySet<int>> ClosedAsync(IReadOnlyCollection<int> issues, CancellationToken ct);

	/// <summary>Closes the issue for the owner, who said in Braggarr that it is posted.</summary>
	Task CloseAsync(int issue, CancellationToken ct);
}

/// <summary>
/// One post to send. <see cref="Key"/> and <see cref="At"/> are the same on every attempt, so a publisher can make a retry harmless:
/// Bluesky derives the record key from them, Mastodon sends them as its <c>Idempotency-Key</c>.
/// </summary>
public sealed record PublishRequest(DeliveryKey Key, DateTimeOffset At, Variant Variant)
{
	/// <summary>The variant's images, read from the posts repo, in the order the variant lists them.</summary>
	public IReadOnlyList<MediaFile> Media { get; init; } = [];
}

/// <summary>An image or a video, with what it is and what it shows. A video carries its shape, read from the file.</summary>
public sealed record MediaFile(string Path, string ContentType, byte[] Bytes, string Alt)
{
	public VideoInfo? Video { get; init; }

	public bool IsVideo => ContentType == "video/mp4";

	public static string? ContentTypeOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
	{
		".png" => "image/png",
		".jpg" or ".jpeg" => "image/jpeg",
		".webp" => "image/webp",
		".gif" => "image/gif",
		".mp4" => "video/mp4",
		_ => null
	};
}

/// <summary>
/// Videos, kept on the server rather than in git: too big for a repository, and git LFS is not wanted. Named by the SHA-256 of
/// their bytes, so the same file stored twice is one file, and a name always means the same video.
/// </summary>
public interface IVideoStore
{
	/// <summary>Stores the bytes; returns the reference a variant carries, <c>video:sha256-….mp4</c>.</summary>
	Task<MediaRef> SaveAsync(byte[] bytes, string alt, CancellationToken ct);

	/// <summary>The bytes of a stored video, or null when the server does not have it.</summary>
	Task<byte[]?> ReadAsync(string path, CancellationToken ct);
}

/// <summary>
/// A network Braggarr posts to. Throw <see cref="PublishException"/> only when the post is certainly not up (the network refused
/// it, or a retry is idempotent); any other exception marks the delivery unknown, for the owner to check.
/// </summary>
public interface IPublisher
{
	Network Network { get; }

	Task<Receipt> PublishAsync(PublishRequest request, CancellationToken ct);
}

/// <summary>Whether a blog post answers at its address yet: its site has built and is serving it.</summary>
public interface ISites
{
	Task<bool> AnswersAsync(string url, CancellationToken ct);
}

/// <summary>Whether a campaign's project can be seen by anyone, as GitHub says: only such a project gets a blog post.</summary>
public interface IProjectVisibility
{
	Task<ProjectVisibility> OfAsync(Campaign campaign, CancellationToken ct);
}

/// <summary>The publishers that have working credentials, one per connected account. A post for an account that is not connected waits.</summary>
public interface IPublishers
{
	IPublisher? Find(Network network, string account);
}

/// <summary>What changes on a profile: only the fields that are set.</summary>
public sealed record ProfileChange(string? DisplayName = null, string? Bio = null, string? Link = null);

/// <summary>Reads and changes the owner's profiles on the networks, per connected account.</summary>
public interface IProfiles
{
	/// <summary>The account's profile as it is now. Throws <see cref="PublishException"/> when the network will not say.</summary>
	Task<AccountProfile> ReadAsync(Network network, string account, CancellationToken ct);

	/// <summary>
	/// Sets what <paramref name="change"/> names, leaving everything else as it is. Only what
	/// <see cref="Profiles.CanApply"/> allows; throws <see cref="PublishException"/> when the network refuses.
	/// </summary>
	Task ApplyAsync(Network network, string account, ProfileChange change, CancellationToken ct);
}

/// <summary>What Claude is asked to write a profile from: the profile now, the briefs of the projects it posts about, the voice.</summary>
public sealed record ProfileRequest(AccountProfile Current, IReadOnlyList<ProjectBrief> About, string? Voice);

/// <summary>A display name, a bio within the network's limit, the link to use, and in a sentence why.</summary>
public sealed record ProfileSuggestion(string DisplayName, string Bio, string? Link, string Why);

/// <summary>Which account a campaign posts as on a network, and why: the campaign's own choice, its project's, its org's, or the global default.</summary>
public sealed record AccountChoice(Network Network, string Account, string Because);

/// <summary>The owner's connected accounts, and the defaults that pick one for a project.</summary>
public interface IAccounts
{
	/// <summary>The accounts connected on a network, by the name Braggarr shows for each.</summary>
	IReadOnlyList<string> Connected(Network network);

	/// <summary>
	/// The account a campaign for <paramref name="project"/> posts as on <paramref name="network"/>: the project's default, else
	/// its GitHub org's, else the global one (the only account, when there is one and no default). Null when none is connected.
	/// </summary>
	AccountChoice? Choose(Network network, string project);
}

public sealed class PublishException(string message, Exception? inner = null) : Exception(message, inner);

public enum NoticeLevel
{
	Info,
	Action,
	Problem
}

/// <summary>Something the owner should hear about, with a link to act on it, and up to three buttons.</summary>
public sealed record Notice(NoticeLevel Level, string Title, string Message, string? Url = null)
{
	public IReadOnlyList<SubmitLink> Actions { get; init; } = [];
}

public interface INotifier
{
	Task NotifyAsync(Notice notice, CancellationToken ct);
}

/// <summary>Every delivery event, with the time it happened. Read by the history page and by the tests' rules.</summary>
public interface IDeliveryJournal
{
	void Record(DeliveryEvent change, DateTimeOffset at);
}

/// <summary>One variant to write: which post, which network, and why again if it is a second try.</summary>
public sealed record DraftTarget(string Post, Network Network, string? Problem = null, string? Previous = null);

/// <summary>
/// What a drafter gets: the campaign as context, the posts (with any variants already written, to stay consistent with), the
/// variants to write, the owner's voice, and a note from whoever asked.
/// </summary>
public sealed record DraftRequest(
	string Title,
	string Project,
	string Brief,
	IReadOnlyList<string> Links,
	IReadOnlyList<Post> Posts,
	IReadOnlyList<DraftTarget> Targets,
	string? Voice,
	string? Note = null
)
{
	/// <summary>The campaign's talking points, in order; each post names the ones it covers.</summary>
	public IReadOnlyList<TalkingPoint> Points { get; init; } = [];

	/// <summary>The owner's brief for the project, when there is one.</summary>
	public ProjectBrief? About { get; init; }
}

/// <summary>What one call cost, in tokens, on which model.</summary>
public sealed record Usage(string Model, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens);

/// <summary>A variant written for one post.</summary>
public sealed record DraftedVariant(string Post, Variant Variant);

public sealed record DraftResult(IReadOnlyList<DraftedVariant> Variants, Usage Usage);

public sealed class DraftException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Writes variants. Null from <see cref="IDrafters.Current"/> when no model is configured: then nothing is drafted.</summary>
public interface IDrafter
{
	Task<DraftResult> DraftAsync(DraftRequest request, CancellationToken ct);

	/// <summary>Alt text for an image that goes with a post about <paramref name="context"/>.</summary>
	Task<(string Alt, Usage Usage)> DescribeAsync(MediaFile image, string context, CancellationToken ct);

	/// <summary>An introduction campaign for a project: a launch, then posts that each zoom in on one feature.</summary>
	Task<(CampaignPlan Plan, Usage Usage)> PlanAsync(RepositoryInfo repository, CancellationToken ct);

	/// <summary>Talking points for a campaign that only has a brief, most important first, each with the media it wants.</summary>
	Task<(IReadOnlyList<TalkingPoint> Points, Usage Usage)> PointsAsync(PointsRequest request, CancellationToken ct);

	/// <summary>A display name, bio and link for one of the owner's accounts, from the briefs of what it posts about.</summary>
	Task<(ProfileSuggestion Suggestion, Usage Usage)> ProfileAsync(ProfileRequest request, CancellationToken ct);

	/// <summary>A brief for a project from its repository: what it is in a line, what sets it apart, for whom, and its links.</summary>
	Task<(ProjectBrief Brief, Usage Usage)> BriefAsync(RepositoryInfo repository, CancellationToken ct);

	/// <summary>Whether a release is worth announcing, why, and from what angle.</summary>
	Task<(ReleaseVerdict Verdict, Usage Usage)> JudgeAsync(string project, Release release, CancellationToken ct);
}

/// <summary>A model's call on a release: worth a post or not, the reason in a sentence, and the angle to take if it is.</summary>
public sealed record ReleaseVerdict(bool Worthy, string Reason, string? Angle);

public interface IDrafters
{
	IDrafter? Current { get; }
}

/// <summary>A campaign to judge, the owner's criteria, and their voice for context.</summary>
public sealed record JudgeRequest(Campaign Campaign, IReadOnlyList<Criterion> Criteria, string? Voice)
{
	/// <summary>The owner's brief for the campaign's project, when there is one: what it is, and what never to say about it.</summary>
	public ProjectBrief? About { get; init; }
}

/// <summary>Judges a campaign against the owner's criteria: a finding per criterion and variant it applies to.</summary>
public interface IJudge
{
	string Model { get; }

	Task<(IReadOnlyList<Finding> Findings, Usage Usage)> JudgeAsync(JudgeRequest request, CancellationToken ct);
}

public interface IJudges
{
	/// <summary>The judge for the current settings; null without a Claude key.</summary>
	IJudge? Current { get; }
}

/// <summary>Whether the judge may approve by itself, and how long what it approves waits before anything goes out.</summary>
public sealed class JudgingOptions
{
	public bool AutoApprove { get; set; }

	public TimeSpan Delay { get; set; } = TimeSpan.FromHours(12);
}

/// <summary>Every model call and what it cost, for the settings page.</summary>
public interface IUsageLog
{
	void Record(Usage usage, string purpose, DateTimeOffset at);
}

/// <summary>A published release of a watched project.</summary>
public sealed record Release(string Tag, string Name, string Url, string Notes, DateTimeOffset PublishedAt, bool Prerelease);

/// <summary>Releases of a GitHub repository, newest first. Drafts never show up here.</summary>
public interface IReleases
{
	Task<IReadOnlyList<Release>> LatestAsync(string repository, CancellationToken ct);
}

/// <summary>A project Braggarr watches: its repository, what to call it, where to announce it, and the last release it saw.</summary>
public sealed record WatchedProject(
	string Repository,
	string Name,
	IReadOnlyList<Network> Networks,
	string? LastSeenTag,
	bool IncludePrereleases = false,
	bool AskClaude = true
);

/// <summary>The watched projects and where Braggarr keeps what it has seen.</summary>
public interface IWatchedProjects
{
	IReadOnlyList<WatchedProject> All { get; }

	/// <summary>The owner's time zone, so a release is announced at a sensible hour.</summary>
	TimeZoneInfo Zone { get; }

	void MarkSeen(string repository, string tag);

	/// <summary>Remembers that a release was not announced, and why, so the owner can see it and announce it anyway.</summary>
	void RecordSkipped(string repository, string tag, string reason);
}

/// <summary>The owner's time zone, for posts proposed at a sensible hour.</summary>
public interface IOwnerTime
{
	TimeZoneInfo Zone { get; }
}

/// <summary>What a repository says about itself: enough to introduce it.</summary>
public sealed record RepositoryInfo(
	string Repository,
	string Name,
	string Description,
	string? Homepage,
	IReadOnlyList<string> Topics,
	string Readme,
	IReadOnlyList<Release> Releases
);

public interface IRepositories
{
	Task<RepositoryInfo> ReadAsync(string repository, string name, CancellationToken ct);
}

/// <summary>
/// A campaign as planned by a model: its title, the facts for whoever writes the posts, and its talking points in order of
/// importance, each with the media it wants. The points decide the posts (see <see cref="Layout"/>).
/// </summary>
public sealed record CampaignPlan(string Title, string Brief, IReadOnlyList<TalkingPoint> Points);

/// <summary>A campaign with a brief and no points yet: what a model turns into talking points.</summary>
public sealed record PointsRequest(string Title, string Project, string Brief, IReadOnlyList<string> Links)
{
	/// <summary>The owner's brief for the project, when there is one.</summary>
	public ProjectBrief? About { get; init; }
}
