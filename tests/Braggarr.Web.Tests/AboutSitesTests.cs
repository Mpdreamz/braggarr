using System.IO.Abstractions.TestingHelpers;
using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Braggarr.Adapters.Blog;
using Braggarr.Adapters.Config;
using Braggarr.Adapters.Git;
using Braggarr.Adapters.GitHub;
using Braggarr.Adapters.Social;
using Braggarr.Blog;
using Braggarr.Domain;
using Braggarr.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Braggarr.Web.Tests;

/// <summary>The about page as a pull request on the blog's repository, in files the blog's builder reads back as they were meant.</summary>
public class AboutSitesTests
{
	private static readonly AboutPage Page = new(
	[
		new AboutSection(new AboutGroup("For the Mac") { Description = "Apps for Apple silicon." },
		[
			new AboutProject("heed", "heed-build/heed", ProjectVisibility.Public)
			{
				Tldr = "A Mac app for running coding agents.", Url = "https://heed.build/", Stars = 4, Language = "Swift",
				Links = [new BriefLink("Getting started", "https://heed.build/getting-started/")],
				Release = new AboutRelease("v0.33.0", new DateTimeOffset(2026, 10, 29, 22, 43, 38, TimeSpan.Zero), "https://github.com/heed-build/heed/releases/tag/v0.33.0"),
				Icon = "media/heed-icon.png", Image = "media/heed-card.png"
			}
		]),
		new AboutSection(new AboutGroup(".NET tools"), [new AboutProject("curb", "nullean/curb", ProjectVisibility.Public) { Tldr = "A C# formatter." }])
	])
	{
		Intro = "I build tooling I need and want.",
		Profiles = [new AboutProfile(Network.Bluesky, "https://bsky.app/profile/example.com") { Name = "@example.com" }]
	};

	private static readonly Dictionary<string, byte[]> Media = new() { ["media/heed-icon.png"] = [1, 2, 3], ["media/heed-card.png"] = [4, 5] };

	/// <summary>A blog's repository: the files on its main branch, Braggarr's branch, and the pull requests opened from it.</summary>
	private sealed class Blog
	{
		public Dictionary<string, byte[]> Main { get; } = new(StringComparer.Ordinal) { ["braggarr.yml"] = "title: Blog\n"u8.ToArray() };
		public string? Branch { get; private set; }
		public List<int> Pulls { get; } = [];
		public List<string> Commits { get; } = [];
		public List<string> Deleted { get; } = [];
		public Dictionary<string, byte[]> Written { get; } = new(StringComparer.Ordinal);
		private readonly Dictionary<string, byte[]> _blobs = [];
		public StubHttp Http { get; }

		public Blog() => Http = new StubHttp((request, body) => (request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query) switch
		{
			("GET", "/repos/owner/blog/contents/_braggarr/intro.md", _) => Main.TryGetValue("_braggarr/intro.md", out var intro)
				? StubHttp.Json(JsonSerializer.Serialize(new Dictionary<string, string> { ["content"] = Convert.ToBase64String(intro) }))
				: new HttpResponseMessage(HttpStatusCode.NotFound),
			("GET", "/repos/owner/blog/git/ref/heads/main", _) => StubHttp.Json("""{"object":{"sha":"main"}}"""),
			("GET", "/repos/owner/blog/git/commits/main", _) => StubHttp.Json("""{"tree":{"sha":"main-tree"}}"""),
			("GET", "/repos/owner/blog/git/trees/main", _) => StubHttp.Json(JsonSerializer.Serialize(new
			{
				tree = Main.Select(f => new { path = f.Key, type = "blob", sha = GitHubAboutSites.BlobSha(f.Value) })
			})),
			("POST", "/repos/owner/blog/git/blobs", _) => Blob(body),
			("POST", "/repos/owner/blog/git/trees", _) => Tree(body),
			("POST", "/repos/owner/blog/git/commits", _) => Commit(),
			("PATCH", "/repos/owner/blog/git/refs/heads/braggarr/about", _) => Branch is null ? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) : Moved(body),
			("POST", "/repos/owner/blog/git/refs", _) => Created(body),
			("GET", "/repos/owner/blog/pulls", var q) when q.Contains("head=owner%3Abraggarr%2Fabout", StringComparison.Ordinal) =>
				StubHttp.Json(JsonSerializer.Serialize(Pulls.Select(n => new { number = n, html_url = $"https://github.com/owner/blog/pull/{n}", head = new { @ref = "braggarr/about", sha = Branch } }))),
			("POST", "/repos/owner/blog/pulls", _) => Opened(body),
			_ => new HttpResponseMessage(HttpStatusCode.NotFound)
		});

		private HttpResponseMessage Blob(string body)
		{
			var bytes = Convert.FromBase64String(JsonDocument.Parse(body).RootElement.GetProperty("content").GetString()!);
			var sha = GitHubAboutSites.BlobSha(bytes);
			_blobs[sha] = bytes;
			return StubHttp.Json($$"""{"sha":"{{sha}}"}""");
		}

		private HttpResponseMessage Tree(string body)
		{
			var root = JsonDocument.Parse(body).RootElement;
			root.GetProperty("base_tree").GetString().Should().Be("main-tree", "the change starts from the blog's branch as it is");
			foreach (var entry in root.GetProperty("tree").EnumerateArray())
			{
				var path = entry.GetProperty("path").GetString()!;
				if (entry.TryGetProperty("content", out var content))
				{
					Written[path] = Encoding.UTF8.GetBytes(content.GetString()!);
				}
				else if (entry.GetProperty("sha").ValueKind == JsonValueKind.Null)
				{
					Deleted.Add(path);
				}
				else
				{
					Written[path] = _blobs[entry.GetProperty("sha").GetString()!];
				}
			}
			return StubHttp.Json("""{"sha":"new-tree"}""");
		}

		private HttpResponseMessage Commit()
		{
			Commits.Add($"commit-{Commits.Count + 1}");
			return StubHttp.Json($$"""{"sha":"{{Commits[^1]}}"}""");
		}

		private HttpResponseMessage Moved(string body)
		{
			var root = JsonDocument.Parse(body).RootElement;
			root.GetProperty("force").GetBoolean().Should().BeTrue("Braggarr's branch starts again from the blog's branch");
			Branch = root.GetProperty("sha").GetString();
			return StubHttp.Json($$$"""{"object":{"sha":"{{{Branch}}}"}}""");
		}

		private HttpResponseMessage Created(string body)
		{
			var root = JsonDocument.Parse(body).RootElement;
			root.GetProperty("ref").GetString().Should().Be("refs/heads/braggarr/about");
			Branch = root.GetProperty("sha").GetString();
			return StubHttp.Json($$$"""{"object":{"sha":"{{{Branch}}}"}}""");
		}

		private HttpResponseMessage Opened(string body)
		{
			var root = JsonDocument.Parse(body).RootElement;
			root.GetProperty("head").GetString().Should().Be("braggarr/about");
			root.GetProperty("base").GetString().Should().Be("main");
			Pulls.Add(Pulls.Count + 12);
			return StubHttp.Json($$$"""{"number":{{{Pulls[^1]}}},"html_url":"https://github.com/owner/blog/pull/{{{Pulls[^1]}}}","head":{"ref":"braggarr/about","sha":"{{{Branch}}}"}}""");
		}

		/// <summary>The pull request merged: main holds what Braggarr's branch wrote, without what it deleted.</summary>
		public void Merge()
		{
			foreach (var (path, bytes) in Written)
			{
				Main[path] = bytes;
			}
			foreach (var path in Deleted)
			{
				_ = Main.Remove(path);
			}
			Written.Clear();
			Deleted.Clear();
			Pulls.Clear();
		}
	}

	private static GitHubAboutSites Sites(Blog blog)
	{
		var accounts = new AccountStore(new SecretStore(new EphemeralDataProtectionProvider(), new MockFileSystem(), "/config/secrets.json"));
		accounts.Set(Network.Blog, "owner/blog", new BlogSite("owner/blog"));
		return new GitHubAboutSites(
			new GitHubClient(new HttpClient(blog.Http), new Token(), new Monitor<GitHubAppOptions>(new()), new Monitor<PostsRepositoryOptions>(new() { Repository = "owner/posts" })),
			accounts);
	}

	[Test]
	public async Task The_page_goes_up_as_one_pull_request_with_its_files_and_pictures()
	{
		var blog = new Blog();

		var proposal = await Sites(blog).ProposeAsync("owner/blog", Page, Media, CancellationToken.None);

		proposal.Should().Be(new AboutProposal(12, "https://github.com/owner/blog/pull/12"));
		blog.Commits.Should().ContainSingle();
		blog.Branch.Should().Be("commit-1");
		blog.Written.Keys.Should().BeEquivalentTo("_braggarr/projects.yml", "_braggarr/intro.md", "_braggarr/profiles.yml", "_braggarr/media/heed-icon.png", "_braggarr/media/heed-card.png");
		blog.Written["_braggarr/media/heed-icon.png"].Should().Equal(1, 2, 3);
		Encoding.UTF8.GetString(blog.Written["_braggarr/intro.md"]).Should().Be("I build tooling I need and want.\n");
	}

	[Test]
	public async Task What_the_blog_reads_back_is_the_page_as_it_was_meant()
	{
		var blog = new Blog();
		_ = await Sites(blog).ProposeAsync("owner/blog", Page, Media, CancellationToken.None);
		blog.Merge();
		var fs = new MockFileSystem();
		fs.AddFile("/site/braggarr.yml", new MockFileData("title: Blog\nurl: https://blog.example.com/\nauthor: { name: Owner }\n"));
		fs.AddFile("/site/pages/about.md", new MockFileData("---\ntitle: About\n---\n\n{{ intro }}\n\n{{ projects }}\n\n{{ profiles }}\n"));
		foreach (var (path, bytes) in blog.Main.Where(f => f.Key.StartsWith("_braggarr/", StringComparison.Ordinal)))
		{
			fs.AddFile($"/site/{path}", new MockFileData(bytes));
		}

		var read = await SiteReader.ReadAsync(fs, "/site");
		var built = await SiteBuilder.RenderAsync(fs, "/site", read.Site!, new BuildOptions(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));

		read.Diagnostics.Should().BeEmpty();
		built.Diagnostics.Should().BeEmpty();
		var site = read.Site!;
		site.Intro.Should().Be("I build tooling I need and want.\n");
		site.Profiles.Should().Equal(new Profile("bluesky", "https://bsky.app/profile/example.com") { Name = "@example.com" });
		site.Groups.Select(g => (g.Title, g.Description)).Should().Equal(("For the Mac", "Apps for Apple silicon."), (".NET tools", null));
		var heed = site.Groups[0].Projects.Single();
		heed.Should().BeEquivalentTo(new Project("heed", "heed-build/heed")
		{
			Tldr = "A Mac app for running coding agents.", Url = "https://heed.build/", Stars = 4, Language = "Swift",
			Links = [new ProjectLink("Getting started", "https://heed.build/getting-started/")],
			Release = new Braggarr.Blog.Release("v0.33.0", new DateTimeOffset(2026, 10, 29, 22, 43, 38, TimeSpan.Zero), "https://github.com/heed-build/heed/releases/tag/v0.33.0"),
			Icon = "media/heed-icon.png", Image = "media/heed-card.png"
		});
		site.Media.Should().BeEquivalentTo("media/heed-icon.png", "media/heed-card.png");
	}

	[Test]
	public async Task A_blog_that_shows_this_already_gets_no_commit_and_no_pull_request()
	{
		var blog = new Blog();
		_ = await Sites(blog).ProposeAsync("owner/blog", Page, Media, CancellationToken.None);
		blog.Merge();
		var commits = blog.Commits.Count;

		var again = await Sites(blog).ProposeAsync("owner/blog", Page, Media, CancellationToken.None);

		again.Should().BeNull();
		blog.Commits.Should().HaveCount(commits);
		blog.Pulls.Should().BeEmpty();
	}

	[Test]
	public async Task While_its_pull_request_is_open_a_change_moves_the_branch_and_opens_no_second_one()
	{
		var blog = new Blog();
		var first = await Sites(blog).ProposeAsync("owner/blog", Page, Media, CancellationToken.None);

		var second = await Sites(blog).ProposeAsync("owner/blog", Page with { Intro = "Something else." }, Media, CancellationToken.None);

		second.Should().Be(first);
		blog.Pulls.Should().ContainSingle();
		blog.Branch.Should().Be("commit-2");
	}

	[Test]
	public async Task A_picture_the_page_no_longer_shows_is_removed_and_one_it_still_shows_is_not_sent_again()
	{
		var blog = new Blog();
		_ = await Sites(blog).ProposeAsync("owner/blog", Page, Media, CancellationToken.None);
		blog.Merge();
		blog.Main["_braggarr/media/gone-icon.png"] = [9];
		var fewer = Page with { Sections = [Page.Sections[0] with { Projects = [Page.Sections[0].Projects[0] with { Image = null }] }, Page.Sections[1]] };

		_ = await Sites(blog).ProposeAsync("owner/blog", fewer, new Dictionary<string, byte[]> { ["media/heed-icon.png"] = [1, 2, 3] }, CancellationToken.None);

		blog.Deleted.Should().BeEquivalentTo("_braggarr/media/heed-card.png", "_braggarr/media/gone-icon.png");
		blog.Written.Keys.Should().Equal("_braggarr/projects.yml");
	}

	[Test]
	public async Task The_blogs_intro_is_read_from_its_branch()
	{
		var blog = new Blog();
		var sites = Sites(blog);
		(await sites.IntroAsync("owner/blog", CancellationToken.None)).Should().BeNull();

		blog.Main["_braggarr/intro.md"] = "My words.\n"u8.ToArray();

		(await sites.IntroAsync("owner/blog", CancellationToken.None)).Should().Be("My words.\n");
	}

	[Test]
	public async Task A_blog_that_is_not_connected_is_refused()
	{
		var act = () => Sites(new Blog()).ProposeAsync("owner/elsewhere", Page, Media, CancellationToken.None);

		_ = await act.Should().ThrowAsync<PublishException>().WithMessage("*not a connected blog*");
	}

	[Test]
	public void A_blob_is_named_as_git_names_it() =>
		// `printf 'hello\n' | git hash-object --stdin`
		GitHubAboutSites.BlobSha("hello\n"u8.ToArray()).Should().Be("ce013625030ba8dba906f756967f9e9ca394464a");

	private sealed class Token : IGitHubToken
	{
		public Task<string?> GetAsync(CancellationToken ct) => Task.FromResult<string?>("installation-token");
	}

	private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
	{
		public T CurrentValue => value;

		public T Get(string? name) => value;

		public IDisposable? OnChange(Action<T, string?> listener) => null;
	}
}
