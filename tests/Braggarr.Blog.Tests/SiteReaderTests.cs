using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;

namespace Braggarr.Blog.Tests;

public class SiteReaderTests
{
	[Test]
	public async Task A_site_is_read_with_its_posts_pages_profiles_and_files()
	{
		var read = await SiteReader.ReadAsync(Sample.Disk(), Sample.Root);

		read.Diagnostics.Should().BeEmpty();
		var site = read.Site!;
		site.Posts.Select(p => p.Slug).Should().BeEquivalentTo(["curb-1-0", "heed-review", "unfinished", "scheduled"]);
		site.Pages.Should().ContainSingle(p => p.Slug == "about" && p.Aliases.Contains("/about-me.html"));
		site.Profiles.Should().ContainSingle(p => p.Network == "bluesky" && p.Name == "@example.com");
		site.Files.Should().Equal("custom.css", "images/me.jpg");
		site.Head.Should().Contain("fediverse:creator");
		site.Config.Comments!.Repo.Should().Be("owner/site");
		site.Config.Nav.Should().ContainSingle(n => n.Href == "/about/");
	}

	[Test]
	public async Task A_post_in_a_folder_keeps_its_files_and_a_single_file_post_takes_its_date_from_its_name()
	{
		var site = (await SiteReader.ReadAsync(Sample.Disk(), Sample.Root)).Site!;

		var curb = site.Posts.Single(p => p.Slug == "curb-1-0");
		curb.Assets.Should().BeEquivalentTo(["card.png", "diff.png"]);
		curb.Source.Should().Be("posts/2026-11-02-curb-1-0/index.md");
		curb.Date.Should().Be(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)));
		curb.Tags.Should().Equal("dotnet", "release");

		var heed = site.Posts.Single(p => p.Slug == "heed-review");
		heed.Date.Should().Be(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero));
		heed.Assets.Should().BeEmpty();
	}

	[Test]
	public async Task Hidden_folders_and_node_modules_are_never_read() =>
		(await SiteReader.ReadAsync(Sample.Disk(), Sample.Root)).Site!.Posts.Should().NotContain(p => p.Source.Contains("node_modules"));

	[Test]
	public async Task A_misspelt_key_is_reported_not_ignored()
	{
		var fs = Sample.Disk(Sample.Config + "\npermalinks: /x/{slug}/\n");
		fs.AddFile($"{Sample.Root}/posts/2026-11-07-typo.md", new MockFileData("---\ntitle: Typo\ntag: [dotnet]\n---\nBody."));

		var read = await SiteReader.ReadAsync(fs, Sample.Root);

		read.Diagnostics.Should().Contain(d => d.File == "braggarr.yml" && d.Message.Contains("only keys the site knows"));
		read.Diagnostics.Should().Contain(d => d.File == "posts/2026-11-07-typo.md" && d.Message.Contains("only keys a post knows"));
	}

	[Test]
	public async Task A_date_without_an_offset_is_a_warning_and_a_date_that_is_not_one_an_error()
	{
		var fs = Sample.Disk();
		fs.AddFile($"{Sample.Root}/posts/local.md", new MockFileData("---\ntitle: Local\ndate: 2020-03-06 22:44\n---\nBody."));
		fs.AddFile($"{Sample.Root}/posts/nonsense.md", new MockFileData("---\ntitle: Nonsense\ndate: soon\n---\nBody."));
		fs.AddFile($"{Sample.Root}/posts/undated.md", new MockFileData("---\ntitle: Undated\n---\nBody."));

		var read = await SiteReader.ReadAsync(fs, Sample.Root);

		read.Diagnostics.Should().ContainSingle(d => d.File == "posts/local.md" && d.Level == DiagnosticLevel.Warning);
		read.Site!.Posts.Single(p => p.Slug == "local").Date.Should().Be(new DateTimeOffset(2020, 3, 6, 22, 44, 0, TimeSpan.Zero));
		read.Diagnostics.Should().ContainSingle(d => d.File == "posts/nonsense.md" && d.Level == DiagnosticLevel.Error);
		read.Diagnostics.Should().ContainSingle(d => d.File == "posts/undated.md" && d.Level == DiagnosticLevel.Error);
	}

	[Test]
	public async Task Broken_yaml_is_a_diagnostic_never_a_crash()
	{
		var fs = Sample.Disk();
		fs.AddFile($"{Sample.Root}/posts/2026-11-08-broken.md", new MockFileData("---\ntitle: [unclosed\n---\nBody."));
		fs.AddFile($"{Sample.Root}/posts/2026-11-08-unclosed.md", new MockFileData("---\ntitle: No end\n"));

		var read = await SiteReader.ReadAsync(fs, Sample.Root);

		read.Diagnostics.Should().Contain(d => d.File == "posts/2026-11-08-broken.md" && d.Level == DiagnosticLevel.Error);
		read.Diagnostics.Should().Contain(d => d.File == "posts/2026-11-08-unclosed.md" && d.Level == DiagnosticLevel.Error);
	}

	[Test]
	public async Task A_folder_without_braggarr_yml_is_not_a_site()
	{
		var fs = new MockFileSystem();
		fs.AddDirectory("/empty");

		var read = await SiteReader.ReadAsync(fs, "/empty");

		read.Site.Should().BeNull();
		read.Diagnostics.Should().ContainSingle(d => d.Message.Contains("braggarr blog init"));
	}

	[Test]
	public async Task What_init_writes_is_a_site_that_builds_cleanly_and_a_new_post_is_a_draft()
	{
		var fs = new MockFileSystem();
		foreach (var (path, text) in Scaffold.Site(Sample.Now))
		{
			fs.AddFile($"/new/{path}", new MockFileData(text));
		}
		var (newPath, newText) = Scaffold.Post("Second: with a colon", Sample.Now);
		fs.AddFile($"/new/{newPath}", new MockFileData(newText));

		var read = await SiteReader.ReadAsync(fs, "/new");
		var built = await SiteBuilder.RenderAsync(fs, "/new", read.Site!, new BuildOptions(Sample.Now));

		read.Diagnostics.Should().BeEmpty();
		built.Diagnostics.Should().BeEmpty();
		read.Site!.Posts.Should().ContainSingle(p => p.Title == "Second: with a colon" && p.Draft && p.Slug == "second-with-a-colon");
		built.Files.Keys.Should().Contain("posts/hello/index.html").And.NotContain("posts/second-with-a-colon/index.html");
	}

	/// <summary>The example in the repository is what CI builds with the action, and what people copy: it builds strictly.</summary>
	[Test]
	public async Task The_example_site_builds_without_a_warning()
	{
		var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", "site"));
		var fs = new System.IO.Abstractions.FileSystem();

		var read = await SiteReader.ReadAsync(fs, root);
		// Today's build, as CI's: a post dated later than today would wait, and CI would find it missing.
		var built = await SiteBuilder.RenderAsync(fs, root, read.Site!, new BuildOptions(TimeProvider.System.GetUtcNow()));

		read.Diagnostics.Should().BeEmpty();
		built.Diagnostics.Should().BeEmpty();
		built.Files.Keys.Should().Contain(["posts/welcome/index.html", "posts/second/index.html", "about/index.html"]);
	}
}
