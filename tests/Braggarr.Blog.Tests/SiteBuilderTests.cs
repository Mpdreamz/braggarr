using System.IO.Abstractions.TestingHelpers;
using System.Xml.Linq;
using AwesomeAssertions;
using CsCheck;

namespace Braggarr.Blog.Tests;

public class SiteBuilderTests
{
	[Test]
	public async Task A_build_writes_every_page_the_feed_the_sitemap_and_the_redirects()
	{
		var (_, built, _) = await Sample.BuildAsync();

		built.Files.Keys.Where(k => !k.StartsWith("_theme/", StringComparison.Ordinal)).Should().BeEquivalentTo(
		[
			"index.html", "page/2/index.html",
			"articles/curb-1-0/index.html", "articles/curb-1-0/card.png", "articles/curb-1-0/diff.png",
			"articles/heed-review/index.html",
			"about/index.html", "about-me.html", "articles/curb-2026-11-02/index.html",
			"tags/dotnet/index.html", "tags/release/index.html", "archive/index.html",
			"404.html", "feed.xml", "sitemap.xml", "robots.txt",
			"custom.css", "images/me.jpg"
		]);
		built.Files.Keys.Should().ContainSingle(k => k.StartsWith("_theme/style.", StringComparison.Ordinal) && k.EndsWith(".css", StringComparison.Ordinal));
	}

	[Test]
	public async Task Drafts_and_later_posts_wait_but_a_preview_shows_them()
	{
		var (_, built, _) = await Sample.BuildAsync();
		var (_, preview, _) = await Sample.BuildAsync(options: new BuildOptions(Sample.Now) { Drafts = true });

		built.Files.Keys.Should().NotContain(k => k.Contains("unfinished") || k.Contains("scheduled"));
		preview.Files.Keys.Should().Contain(["articles/unfinished/index.html", "articles/scheduled/index.html"]);
		preview.Text("articles/unfinished/index.html").Should().Contain("class=\"draft\"");
	}

	[Test]
	public async Task A_post_page_has_its_cards_its_author_its_profiles_and_its_comments()
	{
		var (_, built, _) = await Sample.BuildAsync();
		var html = built.Text("articles/curb-1-0/index.html");

		html.Should().Contain("<title>Curb 1.0 is out · Example blog</title>");
		html.Should().Contain("<link rel=\"canonical\" href=\"https://www.example.com/articles/curb-1-0/\">");
		html.Should().Contain("<meta property=\"og:image\" content=\"https://www.example.com/articles/curb-1-0/card.png\">");
		html.Should().Contain("<meta property=\"og:image:alt\" content=\"A diff with one changed line\">");
		html.Should().Contain("<meta name=\"description\" content=\"A C# formatter that keeps diffs small.\">");
		html.Should().Contain("\"@type\":\"BlogPosting\"");
		html.Should().Contain("<a rel=\"me\" href=\"https://bsky.app/profile/example.com\">@example.com</a>");
		html.Should().Contain("<img class=\"avatar\" src=\"/images/me.jpg\"");
		html.Should().Contain("data-repo=\"owner/site\"");
		html.Should().Contain("<link rel=\"stylesheet\" href=\"/custom.css\">");
		html.Should().Contain("<style>:root { --accent: #d9480f; }</style>");
		html.Should().Contain("fediverse:creator");
		html.Should().Contain("<a href=\"/tags/release/\">#release</a>");
		html.Should().Contain("id=\"why\"");
	}

	[Test]
	public async Task Text_is_escaped_where_it_goes_into_markup()
	{
		var fs = Sample.Disk();
		fs.AddFile($"{Sample.Root}/posts/2026-11-09-xss.md", new MockFileData("---\ntitle: \"</title><script>alert(1)</script>\"\ndescription: \"\\\"><script>alert(2)</script>\"\n---\nBody."));

		var (_, built, _) = await Sample.BuildAsync(fs);
		var html = built.Text("articles/xss/index.html");

		html.Should().NotContain("<script>alert(");
		html.Should().Contain("&lt;/title&gt;&lt;script&gt;");
	}

	[Test]
	public async Task A_site_below_its_domain_links_everything_under_its_own_path()
	{
		var (_, built, _) = await Sample.BuildAsync(Sample.Disk(Sample.Config.Replace("https://www.example.com/", "https://example.com/blog/", StringComparison.Ordinal)));
		var html = built.Text("articles/curb-1-0/index.html");

		html.Should().Contain("href=\"/blog/_theme/style.");
		html.Should().Contain("<a class=\"site-title\" href=\"/blog/\">");
		html.Should().Contain("<a href=\"/blog/about/\">About</a>");
		html.Should().Contain("<link rel=\"canonical\" href=\"https://example.com/blog/articles/curb-1-0/\">");
		built.Text("feed.xml").Should().Contain("https://example.com/blog/articles/curb-1-0/");
	}

	[Test]
	public async Task An_old_path_sends_readers_on_to_where_the_post_lives_now()
	{
		var (_, built, _) = await Sample.BuildAsync();

		var stub = built.Text("articles/curb-2026-11-02/index.html");
		stub.Should().Contain("<meta http-equiv=\"refresh\" content=\"0; url=https://www.example.com/articles/curb-1-0/\">");
		stub.Should().Contain("<link rel=\"canonical\" href=\"https://www.example.com/articles/curb-1-0/\">");
		built.Text("about-me.html").Should().Contain("url=https://www.example.com/about/");
	}

	[Test]
	public async Task The_feed_is_atom_with_the_newest_posts_and_their_full_text()
	{
		var (_, built, _) = await Sample.BuildAsync();
		XNamespace atom = "http://www.w3.org/2005/Atom";

		var feed = XDocument.Parse(built.Text("feed.xml")).Root!;

		feed.Name.Should().Be(atom + "feed");
		feed.Element(atom + "updated")!.Value.Should().Be("2026-11-05T00:00:00+00:00");
		var entries = feed.Elements(atom + "entry").ToList();
		entries.Select(e => e.Element(atom + "id")!.Value).Should().Equal("https://www.example.com/articles/heed-review/", "https://www.example.com/articles/curb-1-0/");
		entries[1].Element(atom + "content")!.Value.Should().Contain("<h2 id=\"why\">");
		entries[1].Attribute(XNamespace.Xml + "base")!.Value.Should().Be("https://www.example.com/articles/curb-1-0/");
		entries[1].Elements(atom + "category").Select(c => c.Attribute("term")!.Value).Should().Equal("dotnet", "release");
	}

	[Test]
	public async Task The_sitemap_lists_pages_and_never_an_old_path()
	{
		var (_, built, _) = await Sample.BuildAsync();
		XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

		var locations = XDocument.Parse(built.Text("sitemap.xml")).Descendants(ns + "loc").Select(l => l.Value).ToList();

		locations.Should().Contain(["https://www.example.com/", "https://www.example.com/page/2/", "https://www.example.com/articles/curb-1-0/", "https://www.example.com/about/"]);
		locations.Should().NotContain(l => l.Contains("about-me") || l.Contains("curb-2026-11-02") || l.Contains("unfinished"));
	}

	[Test]
	public async Task A_link_or_an_image_that_leads_nowhere_and_an_image_without_alt_text_are_warnings()
	{
		var fs = Sample.Disk();
		fs.AddFile($"{Sample.Root}/posts/2026-11-09-sloppy.md", new MockFileData("---\ntitle: Sloppy\n---\n[gone](/nowhere/) ![](missing.png) [fine](https://elsewhere.example/) [anchor](#top)"));

		var (_, built, read) = await Sample.BuildAsync(fs);

		read.Should().BeEmpty();
		built.Diagnostics.Should().OnlyContain(d => d.Level == DiagnosticLevel.Warning && d.File == "posts/2026-11-09-sloppy.md");
		built.Diagnostics.Select(d => d.Message).Should().Contain(m => m.Contains("/nowhere/ leads nowhere"))
			.And.Contain(m => m.Contains("missing.png leads nowhere"))
			.And.Contain(m => m.Contains("no alt text"));
		built.Diagnostics.Should().HaveCount(3);
	}

	[Test]
	public async Task The_sample_site_builds_without_a_warning() =>
		(await Sample.BuildAsync()).Built.Diagnostics.Should().BeEmpty();

	[Test]
	public async Task Writing_removes_what_an_earlier_build_left_behind()
	{
		var fs = new MockFileSystem();
		fs.AddFile("/out/stale/index.html", new MockFileData("old"));
		var built = new BuiltSite(new Dictionary<string, byte[]> { ["index.html"] = "new"u8.ToArray() }, []);

		(await SiteBuilder.WriteAsync(built, fs, "/out")).Should().BeEmpty();

		fs.FileExists("/out/stale/index.html").Should().BeFalse();
		fs.File.ReadAllText("/out/index.html").Should().Be("new");
	}

	/// <summary>A disk holding the files a generated site names: the covers beside its posts, its avatar.</summary>
	private static MockFileSystem WorldFor(Site site)
	{
		var fs = new MockFileSystem();
		fs.AddDirectory(Sample.Root);
		foreach (var post in site.Posts)
		{
			foreach (var asset in post.Assets)
			{
				fs.AddFile($"{Sample.Root}/{post.Folder}/{asset}", new MockFileData(Sample.Png));
			}
		}
		foreach (var file in site.Files)
		{
			fs.AddFile($"{Sample.Root}/static/{file}", new MockFileData(Sample.Png));
		}
		return fs;
	}

	private static readonly Gen<(Site Site, int[] Order, int Days, bool Drafts)> Shuffled =
		from site in Gens.Sites
		from order in Gen.Int.Array[site.Posts.Count]
		from days in Gen.Int[-40, 40]
		from drafts in Gen.Bool
		select (site, order, days, drafts);

	[Test]
	public void Building_twice_or_from_posts_read_in_another_order_gives_the_same_bytes() =>
		Shuffled.Sample(t =>
		{
			var fs = WorldFor(t.Site);
			var options = new BuildOptions(Gens.Start.AddDays(t.Days)) { Drafts = t.Drafts };
			var once = SiteBuilder.RenderAsync(fs, Sample.Root, t.Site, options).GetAwaiter().GetResult();
			var again = SiteBuilder.RenderAsync(fs, Sample.Root, t.Site, options).GetAwaiter().GetResult();
			var reordered = t.Site with { Posts = [.. t.Site.Posts.Zip(t.Order).OrderBy(p => p.Second).Select(p => p.First)] };
			var other = SiteBuilder.RenderAsync(fs, Sample.Root, reordered, options).GetAwaiter().GetResult();

			Same(once, again);
			Same(once, other);
		}, iter: 100);

	private static void Same(BuiltSite a, BuiltSite b)
	{
		a.Files.Keys.Should().Equal(b.Files.Keys);
		foreach (var (path, bytes) in a.Files)
		{
			b.Files[path].Should().Equal(bytes, path);
		}
	}

	[Test]
	public void Every_published_post_is_written_at_its_permalink_and_nothing_unpublished_is() =>
		Gens.Plans.Sample(plan =>
		{
			var built = SiteBuilder.RenderAsync(WorldFor(plan.Site), Sample.Root, plan.Site, new BuildOptions(plan.Now) { Drafts = plan.Drafts }).GetAwaiter().GetResult();

			foreach (var post in plan.Site.Posts)
			{
				var written = built.Find(post.Permalink(plan.Site.Config)) is not null;
				written.Should().Be(plan.Published.Contains(post), post.ToString());
			}
		}, iter: 100);
}
