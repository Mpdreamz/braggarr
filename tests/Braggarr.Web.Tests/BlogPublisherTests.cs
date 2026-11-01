using System.IO.Abstractions.TestingHelpers;
using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Braggarr.Adapters.Blog;
using Braggarr.Adapters.Git;
using Braggarr.Adapters.GitHub;
using Braggarr.Blog;
using Braggarr.Domain;
using Braggarr.Services;
using CsCheck;
using Microsoft.Extensions.Options;

namespace Braggarr.Web.Tests;

/// <summary>A blog post committed to its site's repository: once, at the address the site will give it.</summary>
public class BlogPublisherTests
{
	private const string Config = "title: localhost blog\nurl: https://blog.example.com/\nauthor: { name: Owner }\npermalink: /articles/{slug}/\n";

	private static readonly DateTimeOffset At = new(2030, 11, 2, 10, 0, 0, TimeSpan.FromHours(1));

	private static Variant Article(string title = "Curb 1.0, from the start", string? slug = null) =>
		new(Network.Blog, "# Curb 1.0\n\nIt keeps diffs small.\n\n![The diff](media/diff.png)", [new MediaRef("media/diff.png", "The diff")])
		{
			Article = new Article(title, "What curb does to a diff.", ["dotnet"]) { Slug = slug, Cover = new MediaRef("media/card.png", "Curb's logo") }
		};

	/// <summary>A site repository on GitHub: its files on main, and every commit that moves main.</summary>
	private sealed class Site
	{
		public Dictionary<string, string> Files { get; } = new() { ["braggarr.yml"] = Config };

		public List<string> Commits { get; } = [];

		public Dictionary<string, string> Written { get; } = [];

		public StubHttp Http { get; }

		public Site() => Http = new StubHttp((request, body) => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
		{
			("GET", var path) when path.StartsWith("/repos/owner/blog/contents/", StringComparison.Ordinal) =>
				Files.TryGetValue(Uri.UnescapeDataString(path["/repos/owner/blog/contents/".Length..]), out var text)
					? StubHttp.Json(JsonSerializer.Serialize(new Dictionary<string, string> { ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) }))
					: new HttpResponseMessage(HttpStatusCode.NotFound),
			("GET", "/repos/owner/blog/git/ref/heads/main") => StubHttp.Json("""{"object":{"sha":"head"}}"""),
			("GET", "/repos/owner/blog/git/commits/head") => StubHttp.Json("""{"tree":{"sha":"head-tree"}}"""),
			("POST", "/repos/owner/blog/git/blobs") => StubHttp.Json("""{"sha":"blob"}"""),
			("POST", "/repos/owner/blog/git/trees") => Tree(body),
			("POST", "/repos/owner/blog/git/commits") => StubHttp.Json("""{"sha":"new-commit"}"""),
			("PATCH", "/repos/owner/blog/git/refs/heads/main") => Moved(),
			_ => new HttpResponseMessage(HttpStatusCode.NotFound)
		});

		private HttpResponseMessage Tree(string body)
		{
			foreach (var entry in JsonDocument.Parse(body).RootElement.GetProperty("tree").EnumerateArray())
			{
				Written[entry.GetProperty("path").GetString()!] = entry.TryGetProperty("content", out var content) ? content.GetString()! : "(blob)";
			}
			return StubHttp.Json("""{"sha":"new-tree"}""");
		}

		private HttpResponseMessage Moved()
		{
			Commits.Add("new-commit");
			foreach (var (path, text) in Written.Where(w => w.Value != "(blob)"))
			{
				Files[path] = text;
			}
			return StubHttp.Json("""{"object":{"sha":"new-commit"}}""");
		}
	}

	private static BlogPublisher Publisher(Site site, string? baseUrl = null) =>
		new(
			new GitHubClient(new HttpClient(site.Http), new Token(), new Monitor<GitHubAppOptions>(new()), new Monitor<PostsRepositoryOptions>(new() { Repository = "owner/posts" })),
			new BlogSite("owner/blog", BaseUrl: baseUrl)
		);

	private static PublishRequest Request(Variant article) =>
		new(new DeliveryKey("curb-1-0", "00-blog", Network.Blog), At, article)
		{
			Media = [new MediaFile("media/diff.png", "image/png", [1, 2], "The diff"), new MediaFile("media/card.png", "image/png", [3, 4], "Curb's logo")]
		};

	[Test]
	public async Task A_post_and_its_images_go_up_in_one_commit_at_the_address_the_site_gives_it()
	{
		var site = new Site();

		var receipt = await Publisher(site).PublishAsync(Request(Article()), CancellationToken.None);

		receipt.Should().Be(new Receipt("posts/2030-11-02-curb-1-0-from-the-start", "https://blog.example.com/articles/curb-1-0-from-the-start/"));
		site.Commits.Should().ContainSingle();
		site.Written.Keys.Should().BeEquivalentTo(
			"posts/2030-11-02-curb-1-0-from-the-start/index.md",
			"posts/2030-11-02-curb-1-0-from-the-start/media/diff.png",
			"posts/2030-11-02-curb-1-0-from-the-start/media/card.png");
		site.Written["posts/2030-11-02-curb-1-0-from-the-start/index.md"].Should().Be(
			"---\ntitle: \"Curb 1.0, from the start\"\ndate: \"2030-11-02T10:00:00+01:00\"\ndescription: What curb does to a diff.\ntags:\n  - dotnet\nslug: curb-1-0-from-the-start\ncover: media/card.png\ncover_alt: \"Curb's logo\"\ncampaign: curb-1-0\n---\n\n# Curb 1.0\n\nIt keeps diffs small.\n\n![The diff](media/diff.png)\n");
	}

	[Test]
	public async Task Publishing_again_finds_the_post_there_and_commits_nothing()
	{
		var site = new Site();
		var first = await Publisher(site).PublishAsync(Request(Article()), CancellationToken.None);

		var again = await Publisher(site).PublishAsync(Request(Article()), CancellationToken.None);

		again.Should().Be(first);
		site.Commits.Should().ContainSingle("a retry after a commit that got in must not post twice");
	}

	[Test]
	public async Task A_site_served_elsewhere_gets_its_address_from_the_account()
	{
		var receipt = await Publisher(new Site(), "https://www.example.org/blog/").PublishAsync(Request(Article()), CancellationToken.None);

		receipt.Url.Should().Be("https://www.example.org/blog/articles/curb-1-0-from-the-start/");
	}

	[Test]
	public async Task A_site_without_its_config_refuses_and_nothing_is_committed()
	{
		var site = new Site();
		_ = site.Files.Remove("braggarr.yml");

		var act = () => Publisher(site).PublishAsync(Request(Article()), CancellationToken.None);

		_ = await act.Should().ThrowAsync<PublishException>().WithMessage("*has no braggarr.yml*");
		site.Commits.Should().BeEmpty();
	}

	/// <summary>What the publisher writes, read back the way the site's builder reads a post, is at the receipt's address.</summary>
	[Test]
	public void The_receipt_is_where_the_site_builder_puts_the_post() =>
		Gen.Select(
			Gen.OneOfConst("/articles/{slug}/", "/{year}/{month}/{day}/{slug}/", "/posts/{slug}/", "/{year}/{slug}/"),
			Gen.OneOfConst("Curb 1.0, from the start", "Why diffs stay small", "Ünïcödé in a title"),
			Gen.OneOfConst<string?>(null, "a-chosen-slug"),
			Gen.Int[-12, 14],
			Gen.Int[0, 365]
		).Sample((permalink, title, slug, offset, day) =>
		{
			var site = new Site();
			site.Files["braggarr.yml"] = Config.Replace("/articles/{slug}/", permalink, StringComparison.Ordinal);
			var at = new DateTimeOffset(2030, 1, 1, 23, 30, 0, TimeSpan.FromHours(offset)).AddDays(day);
			var request = Request(Article(title, slug)) with { At = at };

			var receipt = Publisher(site).PublishAsync(request, CancellationToken.None).GetAwaiter().GetResult();

			var fs = new MockFileSystem();
			foreach (var (path, text) in site.Files)
			{
				fs.AddFile($"/site/{path}", new MockFileData(text));
			}
			var read = SiteReader.ReadAsync(fs, "/site").GetAwaiter().GetResult();
			var post = read.Site!.Posts.Single();
			$"https://blog.example.com{post.Permalink(read.Site.Config)}".Should().Be(receipt.Url);
		});

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
