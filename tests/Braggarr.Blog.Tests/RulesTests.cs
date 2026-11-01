using AwesomeAssertions;
using Nullean.Entities.CsCheck;

namespace Braggarr.Blog.Tests;

/// <summary>Every rule of a site's config, its posts, the site as a whole and what a build publishes, attacked on every axis it implies.</summary>
public class RulesTests
{
	public static IEnumerable<Func<AxisCase>> ConfigAxes() =>
		AutoTest.For(SiteConfigRules.Instance, Gens.Configs)
			.CounterExample(SiteConfigRules.HasATitle, c => c with { Title = " " })
			.CounterExample(SiteConfigRules.UrlIsTheSitesRoot, c => c with { Url = new Uri("https://example.com/blog?draft=1") })
			.CounterExample(SiteConfigRules.AuthorHasAName, c => c with { Author = c.Author with { Name = "" } })
			.CounterExample(SiteConfigRules.AvatarIsInTheSiteOrHttps, c => c with { Author = c.Author with { Avatar = "../secrets/me.jpg" } })
			// A pattern that names no slug but stays a folder with known tokens: only the slug rule breaks.
			.CounterExample(SiteConfigRules.PermalinkNamesTheSlug, c => c with { Permalink = new Permalink("/{year}/{month}/") })
			.CounterExample(SiteConfigRules.PermalinkNamesOnlyKnownTokens, c => c with { Permalink = new Permalink("/{category}/{slug}/") })
			.CounterExample(SiteConfigRules.PermalinkIsAFolder, c => c with { Permalink = new Permalink("/posts/{slug}.html") })
			.CounterExample(SiteConfigRules.NavGoesSomewhere, c => c with { Nav = [.. c.Nav, new NavLink("Home", "javascript:alert(1)")] })
			.CounterExample(SiteConfigRules.AccentIsAColor, c => c with { Accent = "red; } body { display: none" })
			.CounterExample(SiteConfigRules.CommentsAreComplete, c => c with { Comments = new Giscus("owner/site", "", "Comments", "DIC_1") })
			.CounterExample(SiteConfigRules.FeedIsAFileInTheRoot, c => c with { Feed = "../feed.xml" })
			.CounterExample(SiteConfigRules.PagesHoldPosts, c => c with { PostsPerPage = 0 })
			.CounterExample(SiteConfigRules.NoUnknownKeys, c => c with { UnknownKeys = ["permalinks"] })
			.AsDataSource();

	[Test]
	[MethodDataSource(nameof(ConfigAxes))]
	public Task Every_config_axis_holds(AxisCase axis) => axis.RunAsync();

	public static IEnumerable<Func<AxisCase>> PostAxes() =>
		AutoTest.For(BlogPostRules.Instance, Gens.Posts)
			.CounterExample(BlogPostRules.HasATitle, p => p with { Title = "\t" })
			.CounterExample(BlogPostRules.TitleFitsACard, p => p with { Title = new string('a', BlogPostRules.TitleLength + 1) })
			.CounterExample(BlogPostRules.DescriptionFitsASnippet, p => p with { Description = new string('a', BlogPostRules.DescriptionLength + 1) })
			.CounterExample(BlogPostRules.SlugIsASafeSegment, p => p with { Slug = "../../etc" })
			.CounterExample(BlogPostRules.TagsAreSlugs, p => p with { Tags = [.. p.Tags.Where(t => t != "Release"), "Release"] })
			.CounterExample(BlogPostRules.TagsAreDistinct, p => p with { Tags = [.. p.Tags, "oss", "oss"] })
			.CounterExample(BlogPostRules.CoverIsDescribed, p => p with { Cover = "cover.png", CoverAlt = null })
			.CounterExample(BlogPostRules.AliasesArePathsOnTheSite, p => p with { Aliases = [.. p.Aliases, "/old/../../etc/passwd"] })
			.CounterExample(BlogPostRules.CanonicalIsAFullAddress, p => p with { Canonical = "example.com/elsewhere" })
			.CounterExample(BlogPostRules.AssetsStayBesideThePost, p => p with { Assets = [.. p.Assets, ".git/config"] })
			.CounterExample(BlogPostRules.NoUnknownKeys, p => p with { UnknownKeys = ["tag"] })
			.AsDataSource();

	[Test]
	[MethodDataSource(nameof(PostAxes))]
	public Task Every_post_axis_holds(AxisCase axis) => axis.RunAsync();

	/// <summary>A post every generated site can take: its slug is in no generated pool.</summary>
	private static BlogPost Extra(string slug) => new(slug, "Extra", Gens.Start, "Body.") { Folder = $"posts/{slug}", Source = $"posts/{slug}/index.md" };

	public static IEnumerable<Func<AxisCase>> SiteAxes() =>
		AutoTest.For(SiteRules.Instance, Gens.Sites)
			// The same post twice under two folders: one address, two posts.
			.CounterExample(SiteRules.EveryPathIsUnique, s => s with { Posts = [.. s.Posts, Extra("twice"), Extra("twice") with { Folder = "posts/twice-again" }] })
			.CounterExample(SiteRules.PathsDifferBeyondCase, s => s with { Posts = [.. s.Posts, Extra("case"), Extra("CASE")] })
			.CounterExample(SiteRules.NothingTakesTheThemesPaths, s => s with { Pages = [.. s.Pages.Where(p => p.Slug != "archive"), new Page("archive", "Archive", "Mine.")] })
			.CounterExample(SiteRules.AliasesAreUnique, s => s with
			{
				Posts = [.. s.Posts, Extra("one") with { Aliases = ["/moved.html"] }, Extra("two") with { Aliases = ["/moved.html"] }]
			})
			// An alias that takes the address of a page that lives there.
			.CounterExample(SiteRules.AliasesShadowNothing, s => s with
			{
				Pages = [.. s.Pages.Where(p => p.Slug != "here"), new Page("here", "Here", "Here.")],
				Posts = [.. s.Posts, Extra("shadow") with { Aliases = ["/here/"] }]
			})
			.CounterExample(SiteRules.ProfilesAreHttps, s => s with { Profiles = [.. s.Profiles, new Profile("mastodon", "http://hachyderm.io/@me")] })
			.CounterExample(SiteRules.FilesStayInTheSite, s => s with { Files = [.. s.Files, "../outside.txt"] })
			.AsDataSource();

	[Test]
	[MethodDataSource(nameof(SiteAxes))]
	public Task Every_site_axis_holds(AxisCase axis) => axis.RunAsync();

	public static IEnumerable<Func<AxisCase>> PlanAxes() =>
		AutoTest.For(SitePlanRules.Instance, Gens.Plans)
			// A draft let into a build that is not a preview, in its place: only the draft rule breaks. A plan with no draft to
			// let in, or a preview, is left as it is.
			.CounterExample(SitePlanRules.DraftsStayOut, p => p.Drafts || !p.Site.Posts.Any(x => x.Draft && x.Date <= p.Now) ? p : Rebuilt(p, p.Published.Concat(p.Site.Posts.Where(x => x.Draft && x.Date <= p.Now))))
			.CounterExample(SitePlanRules.LaterPostsWait, p => p.Drafts || !p.Site.Posts.Any(x => !x.Draft && x.Date > p.Now) ? p : Rebuilt(p, p.Published.Concat(p.Site.Posts.Where(x => !x.Draft && x.Date > p.Now))))
			// A post that is out, left out everywhere at once: index, tags and feed agree, only completeness notices.
			.CounterExample(SitePlanRules.EveryPostThatIsOutIsPublished, p => p.Published.Count == 0 ? p : Rebuilt(p, p.Published.Skip(1)))
			// Oldest first, with the index, tags and feed laid out in that order: they agree with it, only the order is wrong.
			.CounterExample(SitePlanRules.PublishedIsNewestFirst, p => p.Published.Count < 2 ? p : Laid(p, p.Published.Reverse()))
			// The first page read backwards: every page as full as it was, every post there once, in the wrong order.
			.CounterExample(SitePlanRules.TheIndexHoldsEachPostOnce, p => p.Index[0].Count < 2 ? p : p with { Index = [[.. p.Index[0].Reverse()], .. p.Index.Skip(1)] })
			// The index cut into pages of one when a page holds more: every post still there, in order, once.
			.CounterExample(SitePlanRules.IndexPagesAreFull, p => p.Published.Count < 2 || p.Site.Config.PostsPerPage == 1 ? p : p with { Index = [.. p.Published.Select(x => (ValueList<BlogPost>)[x])] })
			.CounterExample(SitePlanRules.TagPagesHoldTheirPosts, p => p with { Tags = [.. p.Tags, new TagPage("never-used", [])] })
			.CounterExample(SitePlanRules.TheFeedIsTheNewest, p => p.Feed.Count == 0 ? p : p with { Feed = [.. p.Feed.Skip(1)] })
			.CounterExample(SitePlanRules.TheSitemapListsEveryPageOnce, p => p with { Site = p.Site with { Pages = [.. p.Site.Pages, .. p.Site.Pages] } })
			.AsDataSource();

	[Test]
	[MethodDataSource(nameof(PlanAxes))]
	public Task Every_plan_axis_holds(AxisCase axis) => axis.RunAsync();

	/// <summary>A plan that publishes <paramref name="published"/>, with the index, tags and feed made from it the way a build makes them.</summary>
	private static SitePlan Rebuilt(SitePlan plan, IEnumerable<BlogPost> published)
	{
		var everything = plan.Site with { Posts = [.. published] };
		var fromThem = everything.Plan(DateTimeOffset.MaxValue, drafts: true);
		return plan with { Published = fromThem.Published, Index = fromThem.Index, Tags = fromThem.Tags, Feed = fromThem.Feed };
	}

	/// <summary>A plan that publishes <paramref name="published"/> in exactly that order, the index, tags and feed laid out from it.</summary>
	private static SitePlan Laid(SitePlan plan, IEnumerable<BlogPost> published)
	{
		ValueList<BlogPost> posts = [.. published];
		return plan with
		{
			Published = posts,
			Index = [.. posts.Chunk(plan.Site.Config.PostsPerPage).Select(c => (ValueList<BlogPost>)[.. c])],
			Tags = [.. plan.Tags.Select(t => t with { Posts = [.. posts.Where(x => x.Tags.Contains(t.Tag))] })],
			Feed = [.. posts.Take(plan.Site.Config.FeedSize)]
		};
	}

	[Test]
	public void A_preview_publishes_drafts_and_later_posts_and_a_build_does_not()
	{
		var site = new Site(new SiteConfig("s", "", new Uri("https://example.com/"), new Author("me")),
		[
			new BlogPost("out", "Out", Gens.Start, ""),
			new BlogPost("draft", "Draft", Gens.Start, "") { Draft = true },
			new BlogPost("later", "Later", Gens.Start.AddDays(1), "")
		], []);

		site.Plan(Gens.Start).Published.Select(p => p.Slug).Should().Equal("out");
		site.Plan(Gens.Start, drafts: true).Published.Select(p => p.Slug).Should().Equal("later", "draft", "out");
		site.Plan(Gens.Start.AddDays(1)).Published.Select(p => p.Slug).Should().Equal("later", "out");
	}

	[Test]
	public void A_site_with_no_posts_still_has_a_first_page()
	{
		var plan = new Site(new SiteConfig("s", "", new Uri("https://example.com/"), new Author("me")), [], []).Plan(Gens.Start);

		plan.Index.Should().ContainSingle().Which.Should().BeEmpty();
		SitePlanRules.All.Validate(plan).Should().BeEmpty();
	}
}
