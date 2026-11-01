using AwesomeAssertions;
using Braggarr.Blog;

namespace Braggarr.Cli.Tests;

/// <summary>The CLI as the GitHub Action runs it, and a build on the real disk.</summary>
[NotInParallel]
public partial class CliTests
{
	private static readonly BlogCommands Commands = new();

	private static Func<string, string?> Env(params (string Name, string Value)[] variables) =>
		name => variables.FirstOrDefault(v => v.Name == name).Value;

	[Test]
	public void The_actions_inputs_become_a_build_with_the_options_given()
	{
		GitHubActions.Args(Env()).Should().Equal("blog", "build");
		GitHubActions.Args(Env(("INPUT_PATH", "site"), ("INPUT_OUTPUT", "out"), ("INPUT_URL", "https://example.com/"), ("INPUT_STRICT", "true"), ("INPUT_DRAFTS", "false")))
			.Should().Equal("blog", "build", "--path", "site", "--output", "out", "--url", "https://example.com/", "--strict");
		GitHubActions.Args(Env(("INPUT_PATH", " "), ("INPUT_STRICT", "TRUE"))).Should().Equal("blog", "build", "--strict");
	}

	[Test]
	public void Only_a_github_workflow_counts_as_one()
	{
		GitHubActions.IsRunning(Env(("GITHUB_ACTIONS", "true"))).Should().BeTrue();
		GitHubActions.IsRunning(Env()).Should().BeFalse();
	}

	[Test]
	public void A_diagnostic_becomes_an_annotation_on_its_file_with_its_text_escaped()
	{
		GitHubActions.Annotation(Diagnostic.Warning("posts/a,b.md", "50% done:\nnext line"), "./site")
			.Should().Be("::warning file=site/posts/a%2Cb.md,title=braggarr::50%25 done:%0Anext line");
		GitHubActions.Annotation(Diagnostic.Error("braggarr.yml", "no url"), ".")
			.Should().Be("::error file=braggarr.yml,title=braggarr::no url");
	}

	[Test]
	public async Task Init_then_build_writes_the_site_and_strict_fails_on_a_warning()
	{
		var root = Path.Combine(Path.GetTempPath(), "braggarr-cli-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			(await Commands.Init(root)).Should().Be(0);
			(await Commands.Init(root)).Should().Be(1, "a folder that is a site already is left alone");
			(await Commands.Build(root)).Should().Be(0);
			File.Exists(Path.Combine(root, ".artifacts", "site", "posts", "hello", "index.html")).Should().BeTrue();

			await File.WriteAllTextAsync(Path.Combine(root, "posts", "2026-01-01-broken.md"), "---\ntitle: Broken\n---\n[gone](/nowhere/)\n");
			(await Commands.Build(root)).Should().Be(0);
			(await Commands.Build(root, strict: true)).Should().Be(1);

			var elsewhere = root + "-out";
			(await Commands.Build(root, output: elsewhere)).Should().Be(0);
			File.Exists(Path.Combine(elsewhere, "index.html")).Should().BeTrue();
			Directory.Delete(elsewhere, recursive: true);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public async Task Serve_answers_every_file_of_the_site_and_its_404_page_for_the_rest()
	{
		var root = Path.Combine(Path.GetTempPath(), "braggarr-serve-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		using var stop = new CancellationTokenSource();
		Task<int>? serving = null;
		try
		{
			(await Commands.Init(root)).Should().Be(0);
			Directory.CreateDirectory(Path.Combine(root, "static", "images"));
			await File.WriteAllBytesAsync(Path.Combine(root, "static", "images", "me.png"), [137, 80, 78, 71]);
			var port = FreePort();
			serving = Commands.Serve(root, port, stop.Token);
			using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
			var index = await ReadyAsync(http);

			var css = Stylesheet().Match(index).Groups[1].Value;
			css.Should().NotBeEmpty();
			foreach (var (path, type) in new[] { (css, "text/css"), ("/images/me.png", "image/png"), ("/feed.xml", "text/xml"), ("/sitemap.xml", "text/xml"), ("/robots.txt", "text/plain") })
			{
				using var response = await http.GetAsync(path);
				response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, path);
				response.Content.Headers.ContentType!.MediaType.Should().Be(type, path);
			}
			using var missing = await http.GetAsync("/nowhere.html");
			missing.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
			(await missing.Content.ReadAsStringAsync()).Should().Contain("<html", "a missing page shows the site's 404 page");
		}
		finally
		{
			await stop.CancelAsync();
			if (serving is not null)
			{
				_ = await serving;
			}
			Directory.Delete(root, recursive: true);
		}
	}

	[System.Text.RegularExpressions.GeneratedRegex("href=\"(/_theme/[^\"]+\\.css)\"")]
	private static partial System.Text.RegularExpressions.Regex Stylesheet();

	private static int FreePort()
	{
		using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		listener.Start();
		return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
	}

	private static async Task<string> ReadyAsync(HttpClient http)
	{
		for (var attempt = 0; ; attempt++)
		{
			try
			{
				return await http.GetStringAsync("/");
			}
			catch (HttpRequestException) when (attempt < 100)
			{
				await Task.Delay(100);
			}
		}
	}

	[Test]
	public async Task A_site_with_an_error_is_not_written()
	{
		var root = Path.Combine(Path.GetTempPath(), "braggarr-cli-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			await File.WriteAllTextAsync(Path.Combine(root, "braggarr.yml"), "title: s\nurl: https://example.com/\nauthor: { name: me }\npermalink: /no-slug/\n");

			(await Commands.Build(root)).Should().Be(1);
			Directory.Exists(Path.Combine(root, ".artifacts")).Should().BeFalse();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}
}
