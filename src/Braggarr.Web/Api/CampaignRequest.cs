using Braggarr.Domain;
using Braggarr.Services;
using Nullean.Entities;

namespace Braggarr.Web.Api;

/// <summary>
/// A post as an agent sends it: when, and the text per network (keys are network names: <c>bluesky</c>, <c>hackernews</c>…).
/// Networks left out are drafted by Claude when it is set up. <c>networks</c> limits it to some of the campaign's, and
/// <c>points</c> names the talking points it covers, counted from 1.
/// </summary>
/// <remarks>A post to the blog carries its article's title and the rest in <c>article</c>; its markdown is its <c>blog</c> variant.</remarks>
public sealed record PostRequest(string? Id, DateTimeOffset At, Dictionary<string, string>? Variants, string[]? Networks = null, int[]? Points = null, ArticleRequest? Article = null)
{
	public PostDraft ToDraft() =>
		new(Id, At, (Variants ?? []).Where(v => Domain.Networks.TryParse(v.Key, out _)).ToDictionary(v => Parse(v.Key), v => v.Value))
		{
			Networks = Networks is null ? null : [.. Networks.Where(n => Domain.Networks.TryParse(n, out _)).Select(Parse)],
			Points = [.. (Points ?? []).Select(p => p - 1)],
			Article = Article?.ToArticle()
		};

	internal static Network Parse(string name) => Domain.Networks.TryParse(name, out var network) ? network : default;
}

/// <summary>
/// A blog post's own fields: its title (at most 100 characters), a description for feeds and previews (at most 160), tags
/// (lowercase slugs), the slug its address is made from (from the title when left out), and a cover in the campaign's media.
/// </summary>
public sealed record ArticleRequest(string Title, string Description, string[]? Tags = null, string? Slug = null, string? Cover = null, string? CoverAlt = null)
{
	public Article ToArticle() =>
		new(Title.Trim(), Description.Trim(), [.. (Tags ?? []).Select(t => t.Trim())])
		{
			Slug = string.IsNullOrWhiteSpace(Slug) ? null : Slug.Trim(),
			Cover = string.IsNullOrWhiteSpace(Cover) ? null : new MediaRef(Cover.Trim(), CoverAlt?.Trim() ?? "")
		};
}

/// <summary>
/// A blog post as an agent proposes it: one article about a public project, which becomes a small campaign of its own
/// (<c>blog-&lt;slug&gt;</c>) the owner reviews like any other. <c>announce</c> names networks whose posts link the article
/// half an hour after it goes up; Claude writes them. <c>at</c> is when it goes up: ten tomorrow morning when left out.
/// </summary>
public sealed record BlogRequest(
	string Project,
	string Title,
	string Description,
	string Markdown,
	string[]? Tags = null,
	DateTimeOffset? At = null,
	string[]? Announce = null,
	string? Slug = null,
	string[]? Links = null
)
{
	public string CampaignSlug => $"blog-{(string.IsNullOrWhiteSpace(Slug) ? Slugs.From(Title) : Slug.Trim())}";

	public CampaignDraft ToDraft(DateTimeOffset nextMorning)
	{
		var at = At ?? nextMorning;
		var announce = (Announce ?? []).Where(n => Domain.Networks.TryParse(n, out var network) && network != Network.Blog).Select(PostRequest.Parse).Distinct().Order().ToList();
		var article = new Article(Title.Trim(), Description.Trim(), [.. (Tags ?? []).Select(t => t.Trim())]) { Slug = string.IsNullOrWhiteSpace(Slug) ? null : Slug.Trim() };
		List<PostDraft> posts = [new("00-blog", at, new Dictionary<Network, string> { [Network.Blog] = Markdown }) { Networks = [Network.Blog], Article = article }];
		if (announce.Count > 0)
		{
			posts.Add(new PostDraft("01-announce", at + Blogging.DeployMargin, new Dictionary<Network, string>())
			{
				Networks = announce,
				Angle = $"The article \"{article.Title}\" is up: what it is about, and the link, written as {Blogging.Placeholder}"
			});
		}
		return new CampaignDraft(CampaignSlug, Title.Trim(), Project.Trim(), [Network.Blog, .. announce], Links ?? [], Description.Trim(), posts);
	}
}

/// <summary>Media a talking point wants made: <c>image</c> or <c>video</c>, and what it should show.</summary>
public sealed record WantedRequest(string Kind, string Description)
{
	internal bool IsKnown => Kind is "image" or "video";

	public WantedMedia ToWanted() => new(Kind == "video" ? MediaKind.Video : MediaKind.Image, Description.Trim());
}

/// <summary>One thing worth saying, in a sentence, and the media that would show it.</summary>
public sealed record PointRequest(string Text, WantedRequest[]? Wanted = null)
{
	public TalkingPoint ToPoint() => new(Text.Trim(), [.. (Wanted ?? []).Select(w => w.ToWanted())]);
}

/// <summary>A campaign as an agent proposes it. Whatever it says, it becomes a proposal the owner reviews.</summary>
public sealed record CampaignRequest(
	string? Slug,
	string Title,
	string Project,
	string[] Networks,
	string[]? Links,
	string? Brief,
	PostRequest[]? Posts = null,
	int? RepeatMonths = null,
	PointRequest[]? Points = null,
	DateTimeOffset? Start = null,
	Dictionary<string, string>? Accounts = null
)
{
	public CampaignDraft ToDraft() =>
		new(Slug, Title, Project, [.. Networks.Where(n => Domain.Networks.TryParse(n, out _)).Select(PostRequest.Parse)], Links ?? [], Brief ?? "", [.. (Posts ?? []).Select(p => p.ToDraft())])
		{
			RepeatMonths = RepeatMonths ?? 0,
			Points = [.. (Points ?? []).Select(p => p.ToPoint())],
			Start = Start,
			Accounts = (Accounts ?? []).Where(a => Domain.Networks.TryParse(a.Key, out _)).ToDictionary(a => PostRequest.Parse(a.Key), a => a.Value)
		};
}

/// <summary>Asks for one variant of a proposal to be written again, with an optional note on what to change.</summary>
public sealed record RedraftRequest(string Network, string? Note);

/// <summary>What a request must say before it reaches the services. Answered as a 400 with each problem by field.</summary>
[Constraints<CampaignRequest>]
public static partial class CampaignRequestRules
{
	public static readonly Constraint<CampaignRequest> HasATitle = Constraints<CampaignRequest>.Always(r => !string.IsNullOrWhiteSpace(r.Title), "A campaign needs a title.");

	public static readonly Constraint<CampaignRequest> NamesNetworks = Constraints<CampaignRequest>.Always(
		r => r.Networks is { Length: > 0 },
		"Name at least one network: bluesky, mastodon, linkedin, threads, x, youtube, reddit, hackernews or blog."
	);

	public static readonly Constraint<CampaignRequest> KnownNetworks = Constraints<CampaignRequest>.Always(
		r => (r.Networks ?? []).All(n => Networks.TryParse(n, out _))
			&& (r.Accounts ?? []).Keys.All(n => Networks.TryParse(n, out _))
			&& (r.Posts ?? []).All(p => (p.Variants ?? []).Keys.All(n => Networks.TryParse(n, out _)) && (p.Networks ?? []).All(n => Networks.TryParse(n, out _))),
		"Every network is one of bluesky, mastodon, linkedin, threads, x, youtube, reddit, hackernews or blog."
	);

	public static readonly Constraint<CampaignRequest> SaysSomething = Constraints<CampaignRequest>.Always(
		r => r.Posts is { Length: > 0 } || r.Points is { Length: > 0 } || !string.IsNullOrWhiteSpace(r.Brief),
		"Give talking points (preferred), a brief to find them in, or posts."
	);

	public static readonly Constraint<CampaignRequest> PointsSaySomething = Constraints<CampaignRequest>.Always(
		r => (r.Points ?? []).All(p => !string.IsNullOrWhiteSpace(p.Text) && (p.Wanted ?? []).All(w => w.IsKnown && !string.IsNullOrWhiteSpace(w.Description))),
		"Every talking point has text, and every wanted media is an image or a video with a description."
	);
}

/// <summary>What a blog request must say before it reaches the services. Answered as a 400 with each problem by field.</summary>
[Constraints<BlogRequest>]
public static partial class BlogRequestRules
{
	public static readonly Constraint<BlogRequest> NamesItsProject = Constraints<BlogRequest>.Always(
		r => !string.IsNullOrWhiteSpace(r.Project),
		"Name the project: only a public project gets a blog post, and Braggarr asks GitHub which one it is."
	);

	public static readonly Constraint<BlogRequest> HasATitle = Constraints<BlogRequest>.Always(
		r => r.Title?.Trim().Length is > 0 and <= Blogging.TitleLimit,
		"A blog post has a title of at most 100 characters."
	);

	public static readonly Constraint<BlogRequest> HasADescription = Constraints<BlogRequest>.Always(
		r => r.Description?.Trim().Length is > 0 and <= Blogging.DescriptionLimit,
		"A blog post has a description of at most 160 characters, for feeds and previews."
	);

	public static readonly Constraint<BlogRequest> SaysSomething = Constraints<BlogRequest>.Always(
		r => !string.IsNullOrWhiteSpace(r.Markdown),
		"A blog post has its article, in markdown."
	);

	public static readonly Constraint<BlogRequest> TagsAreSlugs = Constraints<BlogRequest>.Always(
		r => (r.Tags ?? []).All(t => Slugs.IsValid(t.Trim())),
		"Tags are lowercase letters, digits and dashes."
	);

	public static readonly Constraint<BlogRequest> AnnouncesOnKnownNetworks = Constraints<BlogRequest>.Always(
		r => (r.Announce ?? []).All(n => Networks.TryParse(n, out var network) && network != Network.Blog),
		"Announce on bluesky, mastodon, linkedin, threads, x, youtube, reddit or hackernews."
	);

	public static readonly Constraint<BlogRequest> SlugIsAFolderName = Constraints<BlogRequest>.Always(
		r => string.IsNullOrWhiteSpace(r.Slug) || Slugs.IsValid(r.Slug.Trim()),
		"A slug is lowercase letters, digits and dashes."
	);
}
