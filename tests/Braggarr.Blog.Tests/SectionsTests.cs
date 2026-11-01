using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;

namespace Braggarr.Blog.Tests;

/// <summary>A page shows what Braggarr wrote where it says so: <c>{{ intro }}</c>, <c>{{ projects }}</c>, <c>{{ profiles }}</c>.</summary>
public class SectionsTests
{
	[Test]
	public async Task The_projects_are_read_in_their_groups_with_the_intro()
	{
		var (site, _, read) = await Sample.BuildAsync();

		read.Should().BeEmpty();
		site.Groups.Select(g => g.Title).Should().Equal("Developer tools", "Libraries");
		site.Groups[0].Description.Should().Be("Small tools that stay out of the way.");
		site.Groups[0].Projects.Select(p => p.Repository).Should().Equal("nullean/curb", "heed-build/heed");
		var curb = site.Groups[0].Projects[0];
		curb.Should().BeEquivalentTo(new Project("curb", "nullean/curb")
		{
			Tldr = "A C# formatter that keeps diffs small.",
			Language = "C#",
			Stars = 1234,
			Release = new Release("v1.0.0", new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)), "https://github.com/nullean/curb/releases/tag/v1.0.0"),
			Links = [new ProjectLink("Docs", "https://curb.example.com/docs/")]
		});
		site.Intro.Should().StartWith("Sam builds small tools for .NET.");
	}

	[Test]
	public async Task The_about_page_shows_each_section_where_it_says_so()
	{
		var (_, built, _) = await Sample.BuildAsync();
		var html = built.Text("about/index.html");

		html.Should().NotContain("\0").And.NotContain("{{");
		html.IndexOf("Who writes here.", StringComparison.Ordinal).Should()
			.BeLessThan(html.IndexOf("Sam builds small tools", StringComparison.Ordinal)).And
			.BeLessThan(html.IndexOf("What I make", StringComparison.Ordinal));
		html.IndexOf("What I make", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("Developer tools", StringComparison.Ordinal));
		html.IndexOf("Developer tools", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("Libraries", StringComparison.Ordinal));
		html.IndexOf("Libraries", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("Elsewhere", StringComparison.Ordinal));
		html.Should().Contain("<a href=\"/archive/\">the archive</a>");
		html.Should().Contain("<h2 id=\"developer-tools\">Developer tools</h2>");
		html.Should().Contain("<p class=\"group-description\">Small tools that stay out of the way.</p>");
		html.Should().Contain("<li class=\"project\" data-project-repo=\"nullean/curb\">");
		html.Should().Contain("<h3><a href=\"https://github.com/nullean/curb\">curb</a></h3>");
		html.Should().Contain("<h3><a href=\"https://heed.example.com/\">heed</a></h3>");
		html.Should().Contain("<p class=\"tldr\">A C# formatter that keeps diffs small.</p>");
		html.Should().Contain("<span class=\"language\">C#</span>");
		html.Should().Contain("<span data-count>1,234</span>");
		html.Should().Contain("<a class=\"release\" data-release href=\"https://github.com/nullean/curb/releases/tag/v1.0.0\"><span data-tag>v1.0.0</span> <time datetime=\"2026-11-02T09:00:00&#x2B;01:00\">2 November 2026</time></a>");
		html.Should().Contain("<li><a href=\"https://curb.example.com/docs/\">Docs</a></li>");
		html.Should().Contain("<a rel=\"me\" href=\"https://bsky.app/profile/example.com\">@example.com</a>");
		html.Should().Contain("<meta name=\"description\" content=\"Who writes here.\">");
	}

	[Test]
	public async Task A_project_links_the_published_posts_about_it_and_nothing_else()
	{
		var (_, built, _) = await Sample.BuildAsync();
		var html = built.Text("about/index.html");
		var curb = Project(html, "nullean/curb");
		var heed = Project(html, "heed-build/heed");

		curb.Should().Contain("Written about here:").And.Contain("<a href=\"/articles/curb-1-0/\">Curb 1.0 is out</a>");
		heed.Should().NotContain("Written about here:");
	}

	[Test]
	public async Task Stars_and_releases_are_refreshed_from_GitHub_by_a_script_only_the_projects_page_loads()
	{
		var (_, built, _) = await Sample.BuildAsync();

		var script = built.Files.Keys.Should().ContainSingle(k => k.StartsWith("_theme/projects.", StringComparison.Ordinal) && k.EndsWith(".js", StringComparison.Ordinal)).Subject;
		built.Text("about/index.html").Should().Contain($"<script src=\"/{script}\" defer></script>");
		built.Text("articles/curb-1-0/index.html").Should().NotContain("projects.");
		built.Text(script).Should().Contain("https://api.github.com/repos/").And.Contain("dataset.projectRepo");
	}

	[Test]
	public async Task A_project_without_stars_or_a_release_keeps_a_hidden_place_for_them()
	{
		var (_, built, _) = await Sample.BuildAsync();
		var heed = Project(built.Text("about/index.html"), "heed-build/heed");

		heed.Should().Contain("<span class=\"stars\" data-stars hidden=\"hidden\">");
		Project(built.Text("about/index.html"), "nullean/curb").Should().Contain("<span class=\"stars\" data-stars>");
		heed.Should().Contain("<a class=\"release\" data-release hidden>");
	}

	[Test]
	public async Task A_marker_that_is_not_alone_in_its_paragraph_or_is_in_a_post_stays_text()
	{
		var fs = Sample.Disk();
		fs.AddFile($"{Sample.Root}/pages/notes.md", new MockFileData("---\ntitle: Notes\n---\n\nWrite {{ projects }} on a line of its own.\n\n```\n{{ projects }}\n```\n\n`{{ intro }}`\n"));
		fs.AddFile($"{Sample.Root}/posts/2026-11-09-markers.md", new MockFileData("---\ntitle: Markers\n---\n\n{{ projects }}\n"));

		var (_, built, _) = await Sample.BuildAsync(fs);

		var notes = built.Text("notes/index.html");
		notes.Should().Contain("Write {{ projects }} on a line of its own.").And.Contain("<code>{{ projects }}\n</code>").And.Contain("<code>{{ intro }}</code>");
		notes.Should().NotContain("project-group").And.NotContain("projects.");
		built.Text("articles/markers/index.html").Should().Contain("<p>{{ projects }}</p>").And.NotContain("project-group");
	}

	[Test]
	public async Task A_page_that_opens_with_a_section_is_described_by_its_first_paragraph_of_text()
	{
		var fs = Sample.Disk();
		fs.AddFile($"{Sample.Root}/pages/now.md", new MockFileData("---\ntitle: Now\n---\n\n{{ intro }}\n\nWhat I work on now.\n"));

		var (_, built, _) = await Sample.BuildAsync(fs);

		built.Text("now/index.html").Should().Contain("<meta name=\"description\" content=\"What I work on now.\">");
	}

	[Test]
	public async Task A_section_the_site_has_no_file_for_or_the_theme_does_not_know_is_a_warning()
	{
		var fs = Sample.Disk();
		fs.RemoveFile($"{Sample.Root}/_braggarr/projects.yml");
		fs.RemoveFile($"{Sample.Root}/_braggarr/intro.md");
		fs.AddFile($"{Sample.Root}/pages/more.md", new MockFileData("---\ntitle: More\n---\n\n{{ project }}\n"));

		var (_, built, _) = await Sample.BuildAsync(fs);

		built.Diagnostics.Where(d => d.File.StartsWith("pages/", StringComparison.Ordinal)).Should().BeEquivalentTo(
		[
			Diagnostic.Warning("pages/about.md", "{{ intro }} shows _braggarr/intro.md, which this site does not have: Braggarr writes it."),
			Diagnostic.Warning("pages/about.md", "{{ projects }} shows _braggarr/projects.yml, which this site does not have: Braggarr writes it."),
			Diagnostic.Warning("pages/more.md", "{{ project }} is not a section: a page can show {{ intro }}, {{ projects }}, {{ profiles }}.")
		]);
		built.Text("about/index.html").Should().NotContain("\0").And.NotContain("project-group");
		built.Files.Keys.Should().NotContain(k => k.StartsWith("_theme/projects.", StringComparison.Ordinal));
	}

	[Test]
	public async Task A_link_in_the_intro_that_leads_nowhere_is_a_warning_on_the_intro()
	{
		var fs = Sample.Disk();
		fs.AddFile($"{Sample.Root}/_braggarr/intro.md", new MockFileData("See [the old page](/gone/)."));

		var (_, built, _) = await Sample.BuildAsync(fs);

		built.Diagnostics.Should().ContainSingle(d => d.File == "_braggarr/intro.md" && d.Message == "/gone/ leads nowhere on this site.");
	}

	[Test]
	public async Task A_release_without_a_date_is_an_error_in_the_projects_file()
	{
		var fs = Sample.Disk();
		fs.AddFile($"{Sample.Root}/_braggarr/projects.yml", new MockFileData("groups:\n  - title: Tools\n    projects:\n      - name: curb\n        repo: nullean/curb\n        release: { tag: v1, date: soon, url: https://github.com/nullean/curb/releases/tag/v1 }\n"));

		var read = await SiteReader.ReadAsync(fs, Sample.Root);

		read.Diagnostics.Should().ContainSingle().Which.Should().Be(
			Diagnostic.Error("_braggarr/projects.yml", "curb's release date \"soon\" is not a date: write it as 2026-11-02T09:00:00+01:00."));
	}

	[Test]
	[Arguments("../etc")]
	[Arguments("owner/..")]
	[Arguments("owner/name/more")]
	[Arguments("owner/name\" onmouseover=\"x")]
	[Arguments("curb")]
	public async Task A_repository_is_owner_and_name(string repository)
	{
		var fs = Sample.Disk();
		fs.AddFile($"{Sample.Root}/_braggarr/projects.yml", new MockFileData($"groups:\n  - title: Tools\n    projects:\n      - name: curb\n        repo: '{repository}'\n"));

		var read = await SiteReader.ReadAsync(fs, Sample.Root);

		read.Diagnostics.Select(d => d.Message).Should().Equal("Each project has a name and a repository (owner/name), and is in one group once.");
	}

	[Test]
	public async Task The_rules_of_the_projects_file_are_the_sites()
	{
		var fs = Sample.Disk();
		fs.AddFile($"{Sample.Root}/_braggarr/projects.yml", new MockFileData("groups:\n  - title: Tools\n    projects:\n      - { name: curb, repo: nullean/curb }\n  - title: tools\n    projects:\n      - { name: curb, repo: nullean/curb, url: \"javascript:alert(1)\" }\n"));

		var read = await SiteReader.ReadAsync(fs, Sample.Root);

		read.Diagnostics.Select(d => d.Message).Should().BeEquivalentTo(
		[
			"Each group of projects has a title of its own.",
			"Each project has a name and a repository (owner/name), and is in one group once.",
			"A project's site, its links and its release link over https, and each link and release says what it is."
		]);
	}

	/// <summary>A project's item on the page: from its own start to the next project's, or the end of its group.</summary>
	private static string Project(string html, string repository)
	{
		var from = html.IndexOf($"data-project-repo=\"{repository}\"", StringComparison.Ordinal);
		from.Should().BeGreaterThanOrEqualTo(0, $"the page shows {repository}");
		var next = html.IndexOf("data-project-repo=", from + 1, StringComparison.Ordinal);
		var section = html.IndexOf("</section>", from, StringComparison.Ordinal);
		return html[from..(next < 0 ? section : Math.Min(next, section))];
	}
}
