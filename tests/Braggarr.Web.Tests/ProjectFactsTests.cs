using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Braggarr.Adapters.Blog;
using Braggarr.Adapters.Git;
using Braggarr.Adapters.GitHub;
using Braggarr.Domain;
using Microsoft.Extensions.Options;

namespace Braggarr.Web.Tests;

/// <summary>What GitHub and a project's website say for the about page: whether it is public, its numbers, its pictures.</summary>
public class ProjectFactsTests
{
	private const string Head = """
		<html><head>
		<meta property="og:image" content="/images/social.png"/>
		<link rel="icon" type="image/png" sizes="16x16" href="images/icon-16.png"/>
		<link rel="icon" type="image/png" sizes="32x32" href="images/icon-32.png"/>
		<link rel="icon" type="image/svg+xml" href="images/mark.svg"/>
		<link rel="apple-touch-icon" href="images/icon-180.png"/>
		</head></html>
		""";

	private static (GitHubProjectFacts Facts, StubHttp GitHub, StubHttp Web) Create(
		string repo = """{"private":false,"stargazers_count":37,"language":"C#","homepage":"https://curb.example.com/"}""",
		string page = Head,
		string graph = """{"data":{"repository":{"usesCustomOpenGraphImage":true,"openGraphImageUrl":"https://repository-images.githubusercontent.com/1/2"}}}""")
	{
		var github = new StubHttp((request, _) => request.RequestUri!.AbsolutePath switch
		{
			"/repos/nullean/curb" => StubHttp.Json(repo),
			"/repos/nullean/curb/releases" => StubHttp.Json("""
				[{"tag_name":"0.11.0-beta","html_url":"https://github.com/nullean/curb/releases/tag/0.11.0-beta","published_at":"2026-11-02T10:00:00Z","prerelease":true,"draft":false},
				 {"tag_name":"0.10.1","html_url":"https://github.com/nullean/curb/releases/tag/0.10.1","published_at":"2026-11-01T10:34:25Z","prerelease":false,"draft":false}]
				"""),
			"/graphql" => StubHttp.Json(graph),
			_ => new HttpResponseMessage(HttpStatusCode.NotFound)
		});
		var web = new StubHttp((request, _) => request.RequestUri!.ToString() == "https://curb.example.com/"
			? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page, System.Text.Encoding.UTF8, "text/html") }
			: new HttpResponseMessage(HttpStatusCode.NotFound));
		var client = new GitHubClient(new HttpClient(github), new Token(), new Monitor<GitHubAppOptions>(new()), new Monitor<PostsRepositoryOptions>(new() { Repository = "owner/posts" }));
		return (new GitHubProjectFacts(client, new GitHubReleases(client), new HttpClient(web)), github, web);
	}

	[Test]
	public async Task A_public_project_gets_its_numbers_its_latest_release_and_its_websites_pictures()
	{
		var (facts, _, _) = Create();

		var read = await facts.ReadAsync("nullean/curb", CancellationToken.None);

		read.Should().Be(new Services.ProjectFacts("nullean/curb", ProjectVisibility.Public)
		{
			Stars = 37,
			Language = "C#",
			Homepage = "https://curb.example.com/",
			Release = new AboutRelease("0.10.1", new DateTimeOffset(2026, 11, 1, 10, 34, 25, TimeSpan.Zero), "https://github.com/nullean/curb/releases/tag/0.10.1"),
			IconUrl = "https://curb.example.com/images/icon-180.png",
			ImageUrl = "https://curb.example.com/images/social.png"
		});
	}

	[Test]
	public async Task A_private_project_or_one_GitHub_will_not_show_is_said_so_and_nothing_else_is_asked()
	{
		var (secret, github, web) = Create(repo: """{"private":true,"stargazers_count":3,"language":"C#","homepage":"https://curb.example.com/"}""");
		var (missing, _, _) = Create();

		(await secret.ReadAsync("nullean/curb", CancellationToken.None)).Should().Be(new Services.ProjectFacts("nullean/curb", ProjectVisibility.Private));
		(await missing.ReadAsync("owner/gone", CancellationToken.None)).Should().Be(new Services.ProjectFacts("owner/gone", ProjectVisibility.Unknown));
		github.Requests.Should().ContainSingle();
		web.Requests.Should().BeEmpty();
	}

	[Test]
	public async Task Without_a_preview_on_its_website_the_repositorys_own_is_used_but_never_GitHubs_generated_card()
	{
		var plain = """<link rel="icon" href="favicon.svg">""";
		var (own, _, _) = Create(page: plain);
		var (generated, _, _) = Create(page: plain, graph: """{"data":{"repository":{"usesCustomOpenGraphImage":false,"openGraphImageUrl":"https://opengraph.githubassets.com/x/nullean/curb"}}}""");

		var withOwn = await own.ReadAsync("nullean/curb", CancellationToken.None);
		var withGenerated = await generated.ReadAsync("nullean/curb", CancellationToken.None);

		withOwn.ImageUrl.Should().Be("https://repository-images.githubusercontent.com/1/2");
		withOwn.IconUrl.Should().Be("https://curb.example.com/favicon.svg");
		withGenerated.ImageUrl.Should().BeNull();
	}

	[Test]
	[Arguments("""<link rel="icon" type="image/png" sizes="16x16" href="a.png"><link rel="icon" type="image/png" sizes="32x32" href="b.png">""", "https://example.com/b.png")]
	[Arguments("""<link rel="icon" sizes="32x32" href="b.png"><link rel="icon" type="image/svg+xml" href="m.svg">""", "https://example.com/m.svg")]
	[Arguments("""<link rel="icon" href="m.svg"><link rel="apple-touch-icon" href="t.png">""", "https://example.com/t.png")]
	[Arguments("""<link rel="icon" href="http://example.com/insecure.png">""", null)]
	[Arguments("""<link rel="stylesheet" href="style.css">""", null)]
	public void The_icon_is_the_apple_touch_icon_else_the_svg_else_the_largest_png_and_only_https(string head, string? icon) =>
		GitHubProjectFacts.Pictures(new Uri("https://example.com/"), head).Icon.Should().Be(icon);

	[Test]
	public void A_preview_that_points_off_https_is_not_followed() =>
		GitHubProjectFacts.Pictures(new Uri("https://example.com/"), """<meta property="og:image" content="http://192.168.1.1/admin.png">""").Image.Should().BeNull();

	[Test]
	[Arguments("image/png", ".png")]
	[Arguments("image/jpeg", ".jpg")]
	[Arguments("image/svg+xml", ".svg")]
	[Arguments("text/html", null)]
	public async Task A_picture_is_fetched_when_it_is_an_image(string type, string? extension)
	{
		var pictures = new WebPictures(new HttpClient(new StubHttp((_, _) => Image([1, 2], type))));

		var picture = await pictures.FetchAsync("https://example.com/icon", CancellationToken.None);

		picture?.Extension.Should().Be(extension);
		(picture is null).Should().Be(extension is null);
	}

	[Test]
	public async Task A_picture_too_large_or_not_over_https_is_not_fetched()
	{
		var http = new StubHttp((_, _) => Image(new byte[WebPictures.Limit + 1], "image/png"));
		var pictures = new WebPictures(new HttpClient(http));

		(await pictures.FetchAsync("https://example.com/huge.png", CancellationToken.None)).Should().BeNull();
		(await pictures.FetchAsync("http://example.com/icon.png", CancellationToken.None)).Should().BeNull();
		http.Requests.Should().ContainSingle("an address that is not https is never asked");
	}

	[Test]
	public async Task A_picture_of_unknown_length_stops_being_read_past_the_limit()
	{
		var http = new StubHttp((_, _) =>
		{
			// A stream that does not say its length: the limit is kept while reading, not from a header.
			var content = new StreamContent(new Unsized(new byte[WebPictures.Limit + 1]));
			content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
			content.Headers.ContentLength.Should().BeNull();
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
		});

		(await new WebPictures(new HttpClient(http)).FetchAsync("https://example.com/huge.png", CancellationToken.None)).Should().BeNull();
	}

	private sealed class Unsized(byte[] bytes) : MemoryStream(bytes)
	{
		public override bool CanSeek => false;
	}

	private static HttpResponseMessage Image(byte[] bytes, string type)
	{
		var content = new ByteArrayContent(bytes);
		content.Headers.ContentType = new MediaTypeHeaderValue(type);
		return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
	}

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
