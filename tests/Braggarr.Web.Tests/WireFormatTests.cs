using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Braggarr.Adapters.Claude;
using Braggarr.Adapters.Git;
using Braggarr.Adapters.GitHub;
using Braggarr.Domain;
using Braggarr.Services;
using Braggarr.Web.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Braggarr.Web.Tests;

/// <summary>
/// What Braggarr writes on the wire, byte for byte, where no other test pins it: GitHub's tree, merge and issue calls, the app
/// manifest, the app's JWT, the judge's prompt and the MCP tools' answers. The files in <c>wire/</c> were written by the code before
/// its JSON became source-generated (<c>BRAGGARR_WRITE_GOLDEN=1</c>), so a change in how it serializes shows here.
/// </summary>
public class WireFormatTests
{
	private static readonly string[] BlueskyAndMastodon = ["bluesky", "mastodon"];

	private static readonly Campaign Curb = new(
		"curb-1-0", "Curb 1.0", "curb", CampaignStatus.Proposed, [Network.Bluesky, Network.Reddit], ["https://github.com/nullean/curb"], "Brief.", "agent:release-bot",
		[
			new Post("01-launch", new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)),
			[
				new Variant(Network.Bluesky, "Curb 1.0 is out.", [new MediaRef("media/card.png", "A diff with one changed line")]),
				new Variant(Network.Reddit, "I made a C# formatter\n\nIt keeps diffs small.")
			])
		]
	)
	{ Points = [new TalkingPoint("Keeps diffs small")] };

	private static void Golden(string name, string actual, [CallerFilePath] string here = "")
	{
		var path = Path.Combine(Path.GetDirectoryName(here)!, "wire", name);
		if (Environment.GetEnvironmentVariable("BRAGGARR_WRITE_GOLDEN") == "1")
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, actual);
			return;
		}
		actual.Should().Be(File.ReadAllText(path), $"wire/{name} is what was written before");
	}

	private static string Bodies(StubHttp http) =>
		string.Join("\n", http.Requests.Where(r => r.Body.Length > 0).Select(r => $"{r.Request.Method} {r.Request.RequestUri!.AbsolutePath}\n{r.Body}"));

	private static GitHubClient Client(StubHttp http) =>
		new(new HttpClient(http), new Token(), new StaticMonitor<GitHubAppOptions>(new GitHubAppOptions()), new StaticMonitor<PostsRepositoryOptions>(new PostsRepositoryOptions { Repository = "me/posts" }));

	private static StubHttp GitHub() => new((request, _) => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
	{
		("GET", "/repos/me/posts/git/ref/heads/campaign/curb-1-0") => StubHttp.Json("""{"object":{"sha":"head-sha"}}"""),
		("GET", "/repos/me/posts/git/commits/head-sha") => StubHttp.Json("""{"tree":{"sha":"head-tree"}}"""),
		("GET", "/repos/me/posts/git/trees/head-sha") => StubHttp.Json(
			"""{"tree":[{"path":"campaigns/curb-1-0/campaign.md","type":"blob","sha":"a"},{"path":"campaigns/curb-1-0/02-gone.md","type":"blob","sha":"b"}]}"""),
		("POST", "/repos/me/posts/git/blobs") => StubHttp.Json("""{"sha":"blob-sha"}"""),
		("POST", "/repos/me/posts/git/trees") => StubHttp.Json("""{"sha":"new-tree"}"""),
		("POST", "/repos/me/posts/git/commits") => StubHttp.Json("""{"sha":"new-commit"}"""),
		("PATCH", "/repos/me/posts/git/refs/heads/campaign/curb-1-0") => StubHttp.Json("""{"object":{"sha":"new-commit"}}"""),
		("PUT", "/repos/me/posts/pulls/7/merge") => StubHttp.Json("""{"merged":true}"""),
		("POST", "/repos/me/posts/issues/7/comments") => StubHttp.Json("""{"id":1}"""),
		("PATCH", "/repos/me/posts/pulls/7") => StubHttp.Json("""{"number":7,"html_url":"u","head":{"ref":"campaign/curb-1-0","sha":"s"}}"""),
		("POST", "/repos/me/posts/issues") => StubHttp.Json("""{"number":9,"state":"open","html_url":"https://github.com/me/posts/issues/9"}"""),
		("POST", "/repos/me/posts/issues/9/comments") => StubHttp.Json("""{"id":2}"""),
		("PATCH", "/repos/me/posts/issues/9") => StubHttp.Json("""{"number":9,"state":"closed","html_url":"https://github.com/me/posts/issues/9"}"""),
		("DELETE", _) => new HttpResponseMessage(System.Net.HttpStatusCode.NoContent),
		_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
	});

	[Test]
	public async Task An_update_writes_text_inline_uploads_images_as_blobs_and_deletes_a_post_that_is_gone()
	{
		var http = GitHub();
		var proposal = new Proposal(7, "https://github.com/me/posts/pull/7", Curb);

		_ = await new GitHubReviews(Client(http)).UpdateAsync(proposal, Curb, Actor.Owner("mpdreamz"), CancellationToken.None,
			new Dictionary<string, byte[]> { ["media/card.png"] = [0x89, 0x50, 0x4E, 0x47] });

		Golden("github-update.txt", Bodies(http));
	}

	[Test]
	public async Task Merging_and_closing_a_proposal()
	{
		var http = GitHub();
		var reviews = new GitHubReviews(Client(http));
		var proposal = new Proposal(7, "https://github.com/me/posts/pull/7", Curb);

		await reviews.MergeAsync(proposal, Actor.Owner("mpdreamz"), CancellationToken.None);
		await reviews.CloseAsync(proposal, Actor.Owner("mpdreamz"), CancellationToken.None);

		Golden("github-merge-close.txt", Bodies(http));
	}

	[Test]
	public async Task A_manual_post_is_an_issue_opened_then_closed_as_completed()
	{
		var http = GitHub();
		var manual = new GitHubManualPosts(Client(http));

		_ = await manual.OpenAsync(Curb, Curb.Posts[0], Curb.Posts[0].Variants[1], CancellationToken.None);
		await manual.CloseAsync(9, CancellationToken.None);

		Golden("github-manual.txt", Bodies(http));
	}

	[Test]
	public void The_app_manifest()
	{
		var http = new DefaultHttpContext();
		http.Request.Scheme = "https";
		http.Request.Host = new HostString("braggarr.example.com");

		Golden("github-manifest.json", GitHubSignIn.Manifest(http));
	}

	[Test]
	public void The_apps_jwt_payload()
	{
		var key = System.Security.Cryptography.RSA.Create(2048).ExportRSAPrivateKeyPem();
		var token = new GitHubAppToken(new HttpClient(GitHub()), new StaticMonitor<GitHubAppOptions>(new GitHubAppOptions { AppId = 42, PrivateKeyPem = key }),
			new StaticMonitor<PostsRepositoryOptions>(new PostsRepositoryOptions { Repository = "me/posts" }),
			new Nullean.Entities.ManualTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));

		var payload = token.Jwt().Split('.')[1].Replace('-', '+').Replace('_', '/');
		payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

		Golden("github-jwt.json", Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
	}

	[Test]
	public void The_judges_prompt_with_media() =>
		Golden("judge-prompt.txt", JudgePrompt.UserPrompt(Curb, [new Criterion("Honest", "No claims it cannot back.", Blocks: true)]));

	[Test]
	public async Task The_mcp_tools_answers()
	{
		using var factory = new BraggarrFactory();
		var agent = factory.Agent();

		var proposed = await ToolAsync(agent, "propose_campaign", new
		{
			title = "Hunch 0.3",
			project = "hunch",
			networks = BlueskyAndMastodon,
			brief = "A faster index.",
			posts = new[] { new { at = "2030-11-05T10:00:00+01:00" } }
		});
		var one = await ToolAsync(agent, "get_campaign", new { slug = "hunch-0-3" });
		var all = await ToolAsync(agent, "list_campaigns", new { });
		var calendar = await ToolAsync(agent, "calendar", new { from = "2030-01-01T00:00:00Z", to = "2031-01-01T00:00:00Z" });

		Golden("mcp-tools.txt", string.Join("\n", proposed, one, all, calendar));
	}

	[Test]
	public async Task The_mcp_tool_list()
	{
		using var factory = new BraggarrFactory();
		using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
		{
			Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json")
		};
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
		request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
		using var response = await factory.Agent().SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var json = body.TrimStart().StartsWith('{') ? body : body.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal))[5..];

		Golden("mcp-tool-list.json", JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement.GetProperty("result"), Indented));
	}

	[Test]
	public async Task The_openapi_document()
	{
		using var factory = new BraggarrFactory();

		var document = await factory.Agent().GetStringAsync("/openapi/v1.json");

		Golden("openapi.json", JsonSerializer.Serialize(JsonDocument.Parse(document).RootElement, Indented));
	}

	/// <summary>The agent API's answers, status and body, as an agent reads them.</summary>
	[Test]
	public async Task The_apis_answers()
	{
		using var factory = new BraggarrFactory();
		var agent = factory.Agent();
		// Plain text is the developer exception page or the status page, with this machine's paths: its first line is enough.
		async Task<string> Answer(HttpResponseMessage response)
		{
			var body = await response.Content.ReadAsStringAsync();
			var shown = response.Content.Headers.ContentType?.MediaType == "text/plain" ? body.Split('\n')[0] : body;
			return $"{(int)response.StatusCode} {response.Content.Headers.ContentType}\n{shown}";
		}
		StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

		var answers = new List<string>
		{
			await Answer(await agent.PostAsync("/api/v1/campaigns", Body("""{"title":"Hunch 0.3","project":"hunch","networks":["bluesky","mastodon"],"brief":"A faster index.","points":[{"text":"Twice as fast","wanted":[{"kind":"image","description":"A chart"}]}],"start":"2030-11-05T10:00:00+01:00"}"""))),
			await Answer(await agent.PostAsync("/api/v1/campaigns", Body("""{"title":"","project":"hunch","networks":["myspace"],"brief":""}"""))),
			await Answer(await agent.GetAsync("/api/v1/campaigns")),
			await Answer(await agent.GetAsync("/api/v1/campaigns/hunch-0-3")),
			await Answer(await agent.GetAsync("/api/v1/campaigns/nothing")),
			await Answer(await agent.PostAsync("/api/v1/campaigns/hunch-0-3/posts", Body("""{"at":"2030-11-09T10:00:00+01:00","variants":{"bluesky":"One more thing."}}"""))),
			await Answer(await agent.PostAsync("/api/v1/campaigns/hunch-0-3/posts/01-launch/redraft", Body("""{"network":"bluesky","note":"Shorter."}"""))),
			await Answer(await agent.PostAsync("/api/v1/projects/introduce", Body("""{"repository":"nullean/curb","networks":["bluesky"]}"""))),
			await Answer(await agent.PostAsync("/api/v1/projects/introduce", Body("""{"repository":"curb"}"""))),
			await Answer(await agent.GetAsync("/api/v1/calendar?from=2030-01-01T00:00:00Z&to=2031-01-01T00:00:00Z")),
			await Answer(await agent.PostAsync("/api/v1/campaigns", Body("""{"title": 3}"""))),
			await Answer(await agent.PostAsync("/api/v1/campaigns/hunch-0-3/media", Upload(("post", "01-launch"), ("alt", "A chart"), ("networks", "bluesky")))),
			await Answer(await agent.PostAsync("/api/v1/campaigns/hunch-0-3/media", Upload(("alt", "No post named")))),
		};

		static MultipartFormDataContent Upload(params (string Name, string Value)[] fields)
		{
			var form = new MultipartFormDataContent { { new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]), "file", "chart.png" } };
			foreach (var (name, value) in fields)
			{
				form.Add(new StringContent(value), name);
			}
			return form;
		}

		Golden("api.txt", string.Join("\n\n", answers));
	}

	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

	private static async Task<string> ToolAsync(HttpClient client, string name, object arguments)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
		{
			Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } }), Encoding.UTF8, "application/json")
		};
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
		request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
		using var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var json = body.TrimStart().StartsWith('{') ? body : body.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal))[5..];
		return JsonDocument.Parse(json).RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
	}

	private sealed class Token : IGitHubToken
	{
		public Task<string?> GetAsync(CancellationToken ct) => Task.FromResult<string?>("installation-token");
	}

	private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
	{
		public T CurrentValue => value;

		public T Get(string? name) => value;

		public IDisposable? OnChange(Action<T, string?> listener) => null;
	}
}
