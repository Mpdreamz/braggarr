namespace Braggarr.Domain;

public enum CampaignStatus
{
	/// <summary>An open pull request on the posts repo.</summary>
	Proposed,

	/// <summary>On <c>main</c>: merged by the owner, and scheduled.</summary>
	Approved,

	/// <summary>On <c>main</c> with <c>held: true</c>: nothing goes out until it is resumed.</summary>
	Held,

	/// <summary>The pull request was closed without merging.</summary>
	Rejected
}

public enum MediaKind
{
	Image,
	Video
}

/// <summary>
/// Media attached to a variant: an image as a path relative to the campaign folder (<c>media/card.png</c>); a video kept on the
/// server by its content hash (<c>video:sha256-….mp4</c>), since videos are too big for git; or a slot still wanted
/// (<c>wanted:image</c>, <c>wanted:video</c>) with what it should show as its text. A wanted slot is skipped when posting.
/// </summary>
public sealed partial record MediaRef(string Path, string Alt)
{
	public const string WantedPrefix = "wanted:";

	public const string VideoPrefix = "video:";

	public bool IsWanted => Path.StartsWith(WantedPrefix, StringComparison.Ordinal);

	/// <summary>A video, stored or still wanted.</summary>
	public bool IsVideo => Path == $"{WantedPrefix}video" || Path.StartsWith(VideoPrefix, StringComparison.Ordinal);

	/// <summary>A video kept on the server, rather than an image in the posts repo.</summary>
	public bool IsStoredVideo => Path.StartsWith(VideoPrefix, StringComparison.Ordinal);

	/// <summary>The reference to a stored MP4, by the SHA-256 of its bytes in lowercase hex.</summary>
	public static MediaRef Video(string sha256, string alt) => new($"{VideoPrefix}sha256-{sha256}.mp4", alt);

	/// <summary>The path is one a variant may carry: an image in the media folder, a stored video, or a wanted slot.</summary>
	public bool IsKnownPath =>
		IsWanted ? Path is "wanted:image" or "wanted:video"
		: IsStoredVideo ? StoredVideo().IsMatch(Path)
		: Path.StartsWith("media/", StringComparison.Ordinal) && !Path.Contains("..", StringComparison.Ordinal);

	[System.Text.RegularExpressions.GeneratedRegex("^video:sha256-[0-9a-f]{64}\\.mp4$")]
	private static partial System.Text.RegularExpressions.Regex StoredVideo();

	public static MediaRef Wanted(WantedMedia wanted)
	{
		var kind = wanted.Kind.ToString().ToLowerInvariant();
		var description = wanted.Description.Trim();
		description = description.Length == 0 ? $"An {kind} for this post" : description.Length > Lengths.AltLimit ? description[..Lengths.AltLimit] : description;
		return new($"{WantedPrefix}{kind}", description);
	}
}

/// <summary>Media a talking point asks for, described, not made yet: "a before/after diff where one line changes".</summary>
public sealed record WantedMedia(MediaKind Kind, string Description);

/// <summary>One thing worth saying, in a sentence. A campaign's points are in order of importance.</summary>
public sealed record TalkingPoint(string Text, ValueList<WantedMedia> Wanted)
{
	public TalkingPoint(string text) : this(text, []) { }
}

/// <summary>
/// A blog post's own fields, beside its markdown: a title, a description for feeds and previews, tags, a cover, and the slug its
/// address is made from (from the title when not given).
/// </summary>
public sealed record Article(string Title, string Description, ValueList<string> Tags)
{
	public MediaRef? Cover { get; init; }

	public string? Slug { get; init; }

	/// <summary>The slug its address is made from.</summary>
	public string Path => Slug ?? Slugs.From(Title);

	/// <summary>
	/// A title of 1 to 100 characters and a description of 1 to 160, tags as distinct slugs, a slug that names a folder, and a
	/// cover, if any, in the campaign's media folder with alt text.
	/// </summary>
	public bool Fits =>
		Title.Trim().Length is > 0 and <= Blogging.TitleLimit
		&& Description.Trim().Length is > 0 and <= Blogging.DescriptionLimit
		&& Tags.All(Slugs.IsValid) && Tags.Distinct(StringComparer.Ordinal).Count() == Tags.Count
		&& Slugs.IsValid(Path)
		&& (Cover is not { } cover || (cover.Path.StartsWith("media/", StringComparison.Ordinal) && cover.IsKnownPath && cover.Alt.Trim().Length is > 0 and <= Lengths.AltLimit));
}

/// <summary>
/// What one post says on one network. For Reddit and Hacker News the first line is the title. On the blog the text is the
/// article's markdown, and <see cref="Article"/> holds its title and the rest.
/// </summary>
public sealed record Variant(Network Network, string Text, ValueList<MediaRef> Media)
{
	public Variant(Network network, string text) : this(network, text, []) { }

	/// <summary>The blog post's title, description, tags and cover. Only a blog variant has one.</summary>
	public Article? Article { get; init; }

	/// <summary>
	/// The text fits the network, and a blog variant has its article, which fits too. Images are checked on their own, by
	/// <see cref="MediaFits"/>.
	/// </summary>
	public bool Fits => Lengths.Fits(Network, Text) && (Network == Network.Blog ? Article is { Fits: true } : Article is null);

	/// <summary>
	/// No more images than the network takes, or one video alone on a network that takes video; each image in the campaign's
	/// media folder, each video stored, either still wanted; each with alt text (or, when wanted, a description).
	/// </summary>
	public bool MediaFits =>
		Media.Count <= Lengths.MediaLimit(Network)
		&& (!Media.Any(m => m.IsVideo) || (Media.Count == 1 && Lengths.TakesVideo(Network)))
		&& (!Lengths.NeedsVideo(Network) || Media.All(m => m.IsVideo))
		&& Media.All(m => m.IsKnownPath && m.Alt.Trim().Length is > 0 and <= Lengths.AltLimit);
}

/// <summary>
/// One moment in a campaign: the launch, a follow-up. It goes out at <see cref="At"/> on its networks: the campaign's, unless it
/// names some of them.
/// </summary>
public sealed record Post(string Id, DateTimeOffset At, ValueList<Variant> Variants)
{
	/// <summary>What this post is about, when a campaign spreads over several: "the CLI", "how fast it is". Drafting writes to it.</summary>
	public string? Angle { get; init; }

	/// <summary>The networks this post goes to, when not all of the campaign's: a long post for LinkedIn, a short one per point.</summary>
	public ValueList<Network>? Networks { get; init; }

	/// <summary>Which of the campaign's talking points this post covers, by position.</summary>
	public ValueList<int> Points { get; init; } = [];

	public IReadOnlyList<Network> NetworksIn(Campaign campaign) => Networks ?? campaign.Networks;

	public Variant? For(Network network) => Variants.FirstOrDefault(v => v.Network == network);
}

/// <summary>
/// One push for one thing: a release, a project, an article. Every method returns the next campaign and leaves it unchanged when
/// the change is not allowed, so a caller can always compare before and after.
/// </summary>
/// <summary>
/// Who approved a campaign, what they approved (the <see cref="Campaign.Digest"/> of its content then), when, and, for the
/// judge, the first moment anything may go out. Content that no longer matches the digest is no longer approved.
/// </summary>
public sealed record Approval(string By, string Digest, DateTimeOffset At)
{
	public DateTimeOffset? NotBefore { get; init; }
}

/// <summary>Someone approving, at a time; the judge also says from when what it approves may go out.</summary>
public sealed record Approving(Actor By, DateTimeOffset At)
{
	public DateTimeOffset? NotBefore { get; init; }

	/// <summary>Someone who may approve, and a first moment to go out that is not before the approval.</summary>
	public bool IsValid => By.MayApprove && (NotBefore is not { } notBefore || notBefore >= At);
}

/// <summary>One of the owner's accounts on a network, by the name Braggarr shows for it (a handle, a channel).</summary>
public sealed record CampaignAccount(Network Network, string Account);

public sealed record Campaign(
	string Slug,
	string Title,
	string Project,
	CampaignStatus Status,
	ValueList<Network> Networks,
	ValueList<string> Links,
	string Brief,
	string CreatedBy,
	ValueList<Post> Posts
)
{
	/// <summary>What the campaign says, in order of importance. Empty for a campaign written post by post.</summary>
	public ValueList<TalkingPoint> Points { get; init; } = [];

	/// <summary>The approval it carries, if any. It holds only while <see cref="Digest"/> is still what was approved.</summary>
	public Approval? Approval { get; init; }

	/// <summary>
	/// Which of the owner's accounts posts on each network: chosen when the campaign is proposed, from the campaign's own choice,
	/// its project's, its GitHub org's or the global default, and part of what is approved. A network with none here posts as
	/// the global default.
	/// </summary>
	public ValueList<CampaignAccount> Accounts { get; init; } = [];

	/// <summary>The account named for a network, if any.</summary>
	public string? AccountFor(Network network) => Accounts.FirstOrDefault(a => a.Network == network)?.Account;

	/// <summary>The judge's last verdict, if any. It is about this campaign only while its digests are this one's and the criteria's.</summary>
	public Judgement? Judgement { get; init; }

	/// <summary>Judged as it is now, against these criteria.</summary>
	public bool JudgedAsIs(string criteria) => Judgement is { } j && j.Digest == Digest && j.Criteria == criteria;

	/// <summary>
	/// A fingerprint of everything that goes out: the networks and links, the points, and each post's id, time, networks, points,
	/// and every variant's text and media. Not the title, the brief or how it repeats, which never reach a network.
	/// </summary>
	public string Digest
	{
		get
		{
			var text = new System.Text.StringBuilder();
			_ = text.Append("networks:").AppendJoin(',', Networks).Append('\n')
				.Append("links:").AppendJoin('\u001f', Links).Append('\n');
			foreach (var point in Points)
			{
				_ = text.Append("point:").Append(point.Text).Append('\u001f').AppendJoin('\u001f', point.Wanted.Select(w => $"{w.Kind}={w.Description}")).Append('\n');
			}
			foreach (var post in Posts.OrderBy(p => p.Id, StringComparer.Ordinal))
			{
				_ = text.Append("post:").Append(post.Id).Append('\u001f').Append(post.At.UtcTicks)
					.Append('\u001f').AppendJoin(',', post.NetworksIn(this)).Append('\u001f').AppendJoin(',', post.Points).Append('\n');
				foreach (var variant in post.Variants.OrderBy(v => v.Network))
				{
					_ = text.Append("variant:").Append(variant.Network).Append('\u001f').Append(variant.Text)
						.Append('\u001f').AppendJoin('\u001f', variant.Media.Select(m => $"{m.Path}={m.Alt}")).Append('\n');
					// Only for a blog post, so every other variant keeps the fingerprint it was approved with.
					if (variant.Article is { } article)
					{
						_ = text.Append("article:").Append(article.Title).Append('\u001f').Append(article.Description)
							.Append('\u001f').AppendJoin(',', article.Tags).Append('\u001f').Append(article.Path)
							.Append('\u001f').Append(article.Cover is { } cover ? $"{cover.Path}={cover.Alt}" : "").Append('\n');
					}
				}
			}
			// Only when there are any, so a campaign from before accounts keeps the fingerprint it was approved with.
			if (Accounts.Count > 0)
			{
				_ = text.Append("accounts:").AppendJoin(',', Accounts.OrderBy(a => a.Network).Select(a => $"{a.Network}={a.Account}")).Append('\n');
			}
			return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
		}
	}

	/// <summary>On main, and what is on main is what was approved.</summary>
	public bool IsApproved => Status is CampaignStatus.Approved or CampaignStatus.Held && Approval is { } a && a.Digest == Digest;

	/// <summary>On main, but changed since it was approved (or never stamped): nothing more goes out until it is approved again.</summary>
	public bool NeedsApproval => Status is CampaignStatus.Approved or CampaignStatus.Held && !IsApproved;

	/// <summary>
	/// Every talking point is in some post, and reaches every short network (Bluesky, Mastodon, Threads, X) in a post of its own
	/// or shared: the networks where people scroll past, so each point gets its own chance. Long networks cover the top points
	/// in one post.
	/// </summary>
	public bool IsCovered =>
		Enumerable.Range(0, Points.Count).All(i => Posts.Any(p => p.Points.Contains(i)))
		&& Networks.Where(n => n.Shape() == NetworkShape.Stream).All(n =>
			Enumerable.Range(0, Points.Count).All(i => Posts.Any(p => p.Points.Contains(i) && p.NetworksIn(this).Contains(n))));

	/// <summary>Months after its last post when the campaign is proposed again, written fresh. Zero: never.</summary>
	public int RepeatMonths { get; init; }

	/// <summary>When it was last proposed again; the next repeat counts from the later of this and its last post.</summary>
	public DateTimeOffset? LastRepeat { get; init; }

	/// <summary>
	/// Whether it is time to propose this campaign again: it repeats, everything went out (and something was posted), and the
	/// months have passed since the last post or the last repeat, whichever is later.
	/// </summary>
	public bool RepeatIsDue(IEnumerable<Delivery> deliveries, DateTimeOffset now)
	{
		if (RepeatMonths <= 0 || Status != CampaignStatus.Approved || Posts.Count == 0)
		{
			return false;
		}
		var own = deliveries.Where(d => d.Key.Campaign == Slug).ToList();
		if (own.Count == 0 || !own.All(d => d.State is DeliveryState.Published or DeliveryState.ManualDone))
		{
			return false;
		}
		var lastPost = Posts.Max(p => p.At);
		var anchor = LastRepeat is { } last && last > lastPost ? last : lastPost;
		return now >= anchor.AddMonths(RepeatMonths);
	}

	/// <summary>Every post has a variant for every network of the campaign, and every variant fits its network.</summary>
	public bool IsComplete =>
		Networks.Count > 0 && Posts.Count > 0 && Posts.All(p => p.NetworksIn(this).Count > 0 && p.NetworksIn(this).All(n => p.For(n) is { Fits: true }));

	/// <summary>Complete, and every image described and within its network's limit: what approving and posting ask for.</summary>
	public bool IsReady => IsComplete && IsCovered && HasItsVideos && Posts.All(p => p.Variants.All(v => v.MediaFits));

	/// <summary>Every variant on a network whose post is the video (YouTube) has its video, uploaded rather than still wanted.</summary>
	public bool HasItsVideos =>
		Posts.All(p => p.Variants.Where(v => Lengths.NeedsVideo(v.Network)).All(v => v.Media.Any(m => m.IsStoredVideo)));

	/// <summary>
	/// The owner or the judge approves, never an agent, and only a campaign that is ready. The approval records what was
	/// approved, so an edit afterwards undoes it.
	/// </summary>
	public Campaign Approve(Approving approving) =>
		Status == CampaignStatus.Proposed && approving.IsValid && IsReady
			? this with { Status = CampaignStatus.Approved, Approval = Stamp(approving) }
			: this;

	/// <summary>Approves again what changed since it was approved: a new stamp for what it says now. Held stays held.</summary>
	public Campaign Reapprove(Approving approving) =>
		NeedsApproval && approving.IsValid && IsReady ? this with { Approval = Stamp(approving) } : this;

	private Approval Stamp(Approving approving) => new(approving.By.ToString(), Digest, approving.At) { NotBefore = approving.NotBefore };

	public Campaign Reject(Actor by) =>
		Status == CampaignStatus.Proposed && by.Kind == ActorKind.Owner ? this with { Status = CampaignStatus.Rejected } : this;

	public Campaign Hold(Actor by) =>
		Status == CampaignStatus.Approved && by.Kind == ActorKind.Owner ? this with { Status = CampaignStatus.Held } : this;

	public Campaign Resume(Actor by) =>
		Status == CampaignStatus.Held && by.Kind == ActorKind.Owner ? this with { Status = CampaignStatus.Approved } : this;

	/// <summary>Whether a delivery of this campaign may go out now. Held and incomplete campaigns wait.</summary>
	public bool IsLive => Status == CampaignStatus.Approved && IsReady && IsApproved;

	/// <summary>Live, and past the moment the approval lets anything go out.</summary>
	public bool MayGoOut(DateTimeOffset now) => IsLive && (Approval!.NotBefore is not { } notBefore || now >= notBefore);
}
