using Nullean.Entities;

namespace Braggarr.Blog;

/// <summary>A page that is not a post: <c>pages/about.md</c> is <c>/about/</c>. It has no date and is in no feed.</summary>
public sealed record Page(string Slug, string Title, string Markdown)
{
	public string Folder { get; init; } = "";

	public string Source { get; init; } = "";

	public ValueList<string> Assets { get; init; } = [];

	public string? Description { get; init; }

	public ValueList<string> Aliases { get; init; } = [];

	public string Path => $"/{Slug}/";
}

/// <summary>A profile on a network, linked from the author card with <c>rel="me"</c>. Braggarr writes these from the owner's accounts.</summary>
public sealed record Profile(string Network, string Url)
{
	public string? Name { get; init; }
}

/// <summary>An old path that sends readers to where its post or page lives now.</summary>
public sealed record Redirect(string From, string To);

/// <summary>A site as read from its folder: the config, every post and page, the profiles, and the site's own files.</summary>
public sealed record Site(SiteConfig Config, ValueList<BlogPost> Posts, ValueList<Page> Pages)
{
	public ValueList<Profile> Profiles { get; init; } = [];

	/// <summary>The site's own files, relative to its root: <c>images/me.jpg</c>, <c>_static/custom.css</c>.</summary>
	public ValueList<string> Files { get; init; } = [];

	/// <summary>Markup the owner adds to every page's head (<c>head.html</c>), as it is.</summary>
	public string? Head { get; init; }

	/// <summary>Markup the owner adds to every page's footer (<c>footer.html</c>), as it is.</summary>
	public string? Footer { get; init; }

	/// <summary>Paths the theme writes itself, which no post, page or alias may take.</summary>
	public static IReadOnlyList<string> Reserved { get; } = ["/", "/archive/", "/tags/", "/page/", "/_theme/", "/404.html", "/sitemap.xml", "/robots.txt"];

	/// <summary>Every path a post or a page lives at, in reading order.</summary>
	public IEnumerable<string> Paths() => Posts.Select(p => p.Permalink(Config)).Concat(Pages.Select(p => p.Path));

	public IEnumerable<Redirect> Redirects() =>
		Posts.SelectMany(p => p.Aliases.Select(a => new Redirect(a, p.Permalink(Config))))
			.Concat(Pages.SelectMany(p => p.Aliases.Select(a => new Redirect(a, p.Path))));

	/// <summary>
	/// What a build at <paramref name="now"/> publishes. With <paramref name="drafts"/> (a preview), drafts and posts dated later are
	/// in too.
	/// </summary>
	public SitePlan Plan(DateTimeOffset now, bool drafts = false)
	{
		var published = Posts.Where(p => drafts || p.IsOutAt(now)).OrderBy(p => p, SitePlan.NewestFirst).ToValueList();
		var pages = published.Chunk(Config.PostsPerPage).Select(c => c.ToValueList()).DefaultIfEmpty([]).ToValueList();
		var tags = published.SelectMany(p => p.Tags).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
			.Select(t => new TagPage(t, published.Where(p => p.Tags.Contains(t)).ToValueList()))
			.ToValueList();
		return new SitePlan(this, now, drafts, published, pages, tags, published.Take(Config.FeedSize).ToValueList());
	}
}

/// <summary>The posts with one tag, newest first: <c>/tags/&lt;tag&gt;/</c>.</summary>
public sealed record TagPage(string Tag, ValueList<BlogPost> Posts)
{
	public string Path => $"/tags/{Tag}/";
}

/// <summary>
/// What one build of a site publishes: the posts that are out, the index cut into pages, a page per tag, and the feed. Its rules
/// say what a reader may count on.
/// </summary>
public sealed record SitePlan(
	Site Site,
	DateTimeOffset Now,
	bool Drafts,
	ValueList<BlogPost> Published,
	ValueList<ValueList<BlogPost>> Index,
	ValueList<TagPage> Tags,
	ValueList<BlogPost> Feed)
{
	/// <summary>Newest first; on the same moment by slug (unique on a valid site), so the order never depends on how the posts were read.</summary>
	public static readonly IComparer<BlogPost> NewestFirst = Comparer<BlogPost>.Create((a, b) =>
	{
		var byDate = b.Date.CompareTo(a.Date);
		return byDate != 0 ? byDate : string.CompareOrdinal(a.Slug, b.Slug);
	});

	/// <summary>The path of the index's page <paramref name="number"/>, counted from 1.</summary>
	public static string IndexPath(int number) => number == 1 ? "/" : $"/page/{number}/";

	/// <summary>Every page the site has, for the sitemap: the index pages, the posts, the pages, the tags and the archive.</summary>
	public IEnumerable<string> Sitemap() =>
		Index.Select((_, i) => IndexPath(i + 1))
			.Concat(Published.Select(p => p.Permalink(Site.Config)))
			.Concat(Site.Pages.Select(p => p.Path))
			.Concat(Tags.Select(t => t.Path))
			.Append("/archive/");

	/// <summary>The archive: published posts by year, newest year first.</summary>
	public IEnumerable<IGrouping<int, BlogPost>> Archive() => Published.GroupBy(p => p.Date.Year);
}

[Constraints<Site>]
public static partial class SiteRules
{
	public static readonly Constraint<Site> EveryPathIsUnique = Constraints<Site>.Always(
		s => s.Paths().Distinct(StringComparer.Ordinal).Count() == s.Paths().Count(),
		"No two posts or pages share an address."
	);

	/// <summary>A checkout on macOS or Windows folds case: two folders that differ only there would overwrite each other.</summary>
	public static readonly Constraint<Site> PathsDifferBeyondCase = Constraints<Site>.Always(
		s => s.Paths().Distinct(StringComparer.OrdinalIgnoreCase).Count() == s.Paths().Distinct(StringComparer.Ordinal).Count(),
		"No two addresses differ only in case: on a disk that folds case, one would overwrite the other."
	);

	public static readonly Constraint<Site> NothingTakesTheThemesPaths = Constraints<Site>.Always(
		s => !s.Paths().Concat(s.Redirects().Select(r => r.From)).Any(p => Site.Reserved.Contains(p, StringComparer.OrdinalIgnoreCase)
			|| p.StartsWith("/page/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/tags/", StringComparison.OrdinalIgnoreCase)
			|| p.StartsWith("/_theme/", StringComparison.OrdinalIgnoreCase) || p.Equals("/" + s.Config.Feed, StringComparison.OrdinalIgnoreCase)),
		"No post, page or alias takes a path the theme writes: /, /page/…, /tags/…, /archive/, /_theme/…, the feed, the sitemap."
	);

	public static readonly Constraint<Site> AliasesAreUnique = Constraints<Site>.Always(
		s => s.Redirects().Select(r => r.From).Distinct(StringComparer.OrdinalIgnoreCase).Count() == s.Redirects().Count(),
		"Each old path sends readers to one place."
	);

	public static readonly Constraint<Site> AliasesShadowNothing = Constraints<Site>.Always(
		s => !s.Redirects().Any(r => s.Paths().Any(p => p.Equals(r.From, StringComparison.OrdinalIgnoreCase) || p.Equals(r.From + "/", StringComparison.OrdinalIgnoreCase))),
		"An alias never takes the address of a post or a page that lives there."
	);

	public static readonly Constraint<Site> ProfilesAreHttps = Constraints<Site>.Always(
		s => s.Profiles.All(p => !string.IsNullOrWhiteSpace(p.Network) && SiteConfigRules.IsHttps(p.Url)),
		"Each profile names its network and links to it over https."
	);

	public static readonly Constraint<Site> FilesStayInTheSite = Constraints<Site>.Always(
		s => s.Files.All(SiteConfigRules.IsSiteFile),
		"The site's files are below its root: none is hidden or reached through .."
	);

	/// <summary>The order the disk lists posts in is not the order of the site: planning gives the same in any order.</summary>
	public static readonly Constraint<Site> PlanningIgnoresReadingOrder = Constraints<Site>.OrderIndependent(
		s => s.Posts,
		(s, posts) => s with { Posts = [.. posts] },
		s => s.Plan(DateTimeOffset.MaxValue, drafts: true) with { Site = s with { Posts = [] } }
	);
}

[Constraints<SitePlan>]
public static partial class SitePlanRules
{
	public static readonly Constraint<SitePlan> DraftsStayOut = Constraints<SitePlan>.Always(
		p => p.Drafts || p.Published.All(post => !post.Draft),
		"A draft is never published, except in a preview."
	);

	public static readonly Constraint<SitePlan> LaterPostsWait = Constraints<SitePlan>.Always(
		p => p.Drafts || p.Published.All(post => post.Date <= p.Now),
		"A post dated later is not published before its date, except in a preview: that is how a scheduled post waits in the repository."
	);

	public static readonly Constraint<SitePlan> EveryPostThatIsOutIsPublished = Constraints<SitePlan>.Always(
		p => p.Site.Posts.Where(post => p.Drafts || post.IsOutAt(p.Now)).All(post => p.Published.Contains(post)),
		"Every post that is out (not a draft, its date come) is published."
	);

	/// <summary>Said by the dates, not by <see cref="SitePlan.NewestFirst"/>: a rule that used the plan's own order would agree with any order.</summary>
	public static readonly Constraint<SitePlan> PublishedIsNewestFirst = Constraints<SitePlan>.Always(
		p => p.Published.Zip(p.Published.Skip(1)).All(pair => pair.First.Date >= pair.Second.Date),
		"Posts are listed newest first."
	);

	public static readonly Constraint<SitePlan> TheIndexHoldsEachPostOnce = Constraints<SitePlan>.Always(
		p => p.Index.SelectMany(page => page).SequenceEqual(p.Published),
		"The index's pages, read in order, hold every published post once, newest first."
	);

	public static readonly Constraint<SitePlan> IndexPagesAreFull = Constraints<SitePlan>.Always(
		p => p.Index.Count >= 1
			&& p.Index.Take(p.Index.Count - 1).All(page => page.Count == p.Site.Config.PostsPerPage)
			&& p.Index[^1].Count <= p.Site.Config.PostsPerPage
			&& (p.Index.Count == 1 || p.Index[^1].Count > 0),
		"Every page of the index but the last is full, the last is not empty, and there is always a first page."
	);

	public static readonly Constraint<SitePlan> TagPagesHoldTheirPosts = Constraints<SitePlan>.Always(
		p => p.Tags.All(t => t.Posts.SequenceEqual(p.Published.Where(post => post.Tags.Contains(t.Tag))))
			&& p.Tags.Select(t => t.Tag).Order(StringComparer.Ordinal)
				.SequenceEqual(p.Published.SelectMany(post => post.Tags).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
		"Each tag a published post has gets a page holding exactly its posts, newest first; no other tag has one."
	);

	public static readonly Constraint<SitePlan> TheFeedIsTheNewest = Constraints<SitePlan>.Always(
		p => p.Feed.SequenceEqual(p.Published.Take(p.Site.Config.FeedSize)),
		"The feed holds the newest published posts, as many as feed_size says."
	);

	public static readonly Constraint<SitePlan> TheSitemapListsEveryPageOnce = Constraints<SitePlan>.Always(
		p => p.Sitemap().Distinct(StringComparer.Ordinal).Count() == p.Sitemap().Count()
			&& p.Published.All(post => p.Sitemap().Contains(post.Permalink(p.Site.Config)))
			&& !p.Site.Redirects().Any(r => p.Sitemap().Contains(r.From)),
		"The sitemap lists every page once, every published post among them, and no old path."
	);
}
