using CsCheck;

namespace Braggarr.Blog.Tests;

/// <summary>Generated sites and their parts, all keeping every rule, so each rule has something to attack.</summary>
public static class Gens
{
	public static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1));

	/// <summary>Slugs as sites have them: Braggarr's own, and ones moved from elsewhere with dots, commas and dates in them.</summary>
	public static readonly string[] Slugs =
	[
		"curb-1-0", "mermaider-renders-without-a-browser", "heed-review-a-branch", "the-joys-of-.net-oss-development-2013-04-03",
		"oss-development,-a-continuous-lesson-in-humility-2013-03-07", "raygun.io-first-thoughts-2013-03-07", "hello", "nullean-entities",
		"ünïcode-is-fine", "a"
	];

	private static readonly string[] Titles =
	[
		"Curb 1.0 is out", "Mermaider renders diagrams without a browser", "Review a branch without leaving the keyboard", "Hello",
		"The joys of .NET OSS development", "日本語のタイトル", "A title: with a colon, and \"quotes\""
	];

	private static readonly string[] Bodies =
	[
		"Curb keeps your diffs small.\n\n## Why\n\nBecause reviews are about change.",
		"First paragraph.\n\n```csharp\nvar x = 1;\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |",
		"A [link](https://example.org) and a footnote.[^1]\n\n[^1]: The note.",
		"---\nNot front matter: a rule.\n\n## bluesky\n\nNot a variant either.",
		""
	];

	public static readonly string[] Tags = ["dotnet", "oss", "release", "testing", "c-sharp"];

	public static readonly Gen<Permalink> Permalinks =
		Gen.OneOfConst(Permalink.Default, new Permalink("/articles/{slug}/"), new Permalink("/{year}/{month}/{slug}/"), new Permalink("/{year}/{month}/{day}/{slug}/"));

	public static readonly Gen<SiteConfig> Configs =
		from permalink in Permalinks
		from root in Gen.OneOfConst("https://example.com/", "https://example.com/blog/", "http://localhost:3000/")
		from perPage in Gen.Int[1, 4]
		from feedSize in Gen.Int[1, 5]
		from avatar in Gen.OneOfConst<string?>(null, "images/me.jpg", "https://example.com/me.png")
		from accent in Gen.OneOfConst<string?>(null, "#d9480f", "#abc")
		from comments in Gen.Bool
		from nav in Gen.Bool
		select new SiteConfig("Example blog", "Notes on building things.", new Uri(root), new Author("Sam") { Avatar = avatar, Bio = "Writes code." })
		{
			Permalink = permalink,
			PostsPerPage = perPage,
			FeedSize = feedSize,
			Accent = accent,
			Comments = comments ? new Giscus("owner/site", "R_1", "Comments", "DIC_1") : null,
			Nav = nav ? [new NavLink("About", "/about/"), new NavLink("Source", "https://github.com/owner/site")] : []
		};

	/// <summary>A post on one of a few days and hours, so posts on the same moment happen and the order between them is attacked.</summary>
	public static Gen<BlogPost> Post(string slug) =>
		from title in Gen.OneOfConst(Titles)
		from days in Gen.Int[-10, 10]
		from hour in Gen.OneOfConst(0, 9, 9, 17)
		from body in Gen.OneOfConst(Bodies)
		from tags in Gen.OneOfConst(Tags).HashSet[0, 3]
		from draft in Gen.Int[0, 4].Select(i => i == 0)
		from described in Gen.Bool
		from covered in Gen.Bool
		from aliased in Gen.Bool
		select new BlogPost(slug, title, Start.AddDays(days).AddHours(hour), body)
		{
			Folder = $"posts/{slug}",
			Source = $"posts/{slug}/index.md",
			Tags = [.. tags.Order(StringComparer.Ordinal)],
			Draft = draft,
			Description = described ? "What the post is about." : null,
			Cover = covered ? "cover.png" : null,
			CoverAlt = covered ? "A terminal where one line changes" : null,
			Assets = covered ? ["cover.png"] : [],
			Aliases = aliased ? [$"/old/{Uri.EscapeDataString(slug)}.html"] : []
		};

	public static readonly Gen<BlogPost> Posts = Gen.OneOfConst(Slugs).SelectMany(Post);

	private static readonly Project[] Projects =
	[
		new("curb", "nullean/curb") { Tldr = "A formatter that keeps diffs small.", Stars = 12, Language = "C#", Icon = "media/curb.png", Image = "https://curb.example.com/card.png" },
		new("heed", "heed-build/heed") { Url = "https://heed.example.com/", Links = [new ProjectLink("Docs", "https://heed.example.com/docs/")] },
		new("Nullean.Entities", "nullean/entities") { Release = new Release("v0.3.0", new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(2)), "https://github.com/nullean/entities/releases/tag/v0.3.0") },
		new("site.tools", "owner/site.tools_2")
	];

	/// <summary>None, or the projects dealt into one to three groups, each in at most one.</summary>
	public static readonly Gen<ValueList<ProjectGroup>> Groups =
		from count in Gen.Int[0, 3]
		from picks in Gen.Int[0, 3].Array[Projects.Length]
		select Enumerable.Range(0, count)
			.Select(g => new ProjectGroup(g == 0 ? "Developer tools" : $"Group {g}", [.. Projects.Where((_, i) => picks[i] == g)]) { Description = g == 0 ? "Small tools." : null })
			.ToValueList();

	public static readonly Gen<Site> Sites =
		from config in Configs
		from posts in Posts.Array[0, 7]
		from about in Gen.Bool
		from groups in Groups
		select new Site(config, [.. posts.DistinctBy(p => p.Slug)], about ? [new Page("about", "About", "Who writes here.\n\n{{ projects }}") { Aliases = ["/about-me.html"] }] : [])
		{
			Profiles = [new Profile("bluesky", "https://bsky.app/profile/example.com")],
			Groups = groups,
			Media = ["media/curb.png"],
			Files = config.Author.Avatar is "images/me.jpg" ? ["images/me.jpg"] : []
		};

	public static readonly Gen<SitePlan> Plans =
		from site in Sites
		from days in Gen.Int[-40, 40]
		from drafts in Gen.Int[0, 3].Select(i => i == 0)
		select site.Plan(Start.AddDays(days), drafts);
}
