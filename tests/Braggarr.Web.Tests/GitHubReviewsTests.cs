using System.Text.Json;
using AwesomeAssertions;
using Braggarr.Adapters.Git;
using Braggarr.Adapters.GitHub;
using Braggarr.Domain;
using Microsoft.Extensions.Options;

namespace Braggarr.Web.Tests;

public class GitHubReviewsTests
{
	private static readonly Campaign Curb = new(
		"curb-1-0", "Curb 1.0", "curb", CampaignStatus.Proposed, [Network.Bluesky], [], "Brief.", "agent:release-bot",
		[new Post("01-launch", new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)), [new Variant(Network.Bluesky, "Out.")])]
	);

	[Test]
	public async Task A_proposal_is_a_commit_on_its_own_branch_and_a_pull_request_never_a_push_to_main()
	{
		var github = new StubHttp((request, _) => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
		{
			("GET", "/repos/me/posts/git/ref/heads/main") => StubHttp.Json("""{"object":{"sha":"main-sha"}}"""),
			("GET", "/repos/me/posts/git/commits/main-sha") => StubHttp.Json("""{"tree":{"sha":"main-tree"}}"""),
			("POST", "/repos/me/posts/git/trees") => StubHttp.Json("""{"sha":"new-tree"}"""),
			("POST", "/repos/me/posts/git/commits") => StubHttp.Json("""{"sha":"new-commit"}"""),
			("POST", "/repos/me/posts/git/refs") => StubHttp.Json("""{"object":{"sha":"new-commit"}}"""),
			("POST", "/repos/me/posts/pulls") => StubHttp.Json("""{"number":7,"html_url":"https://github.com/me/posts/pull/7","head":{"ref":"campaign/curb-1-0","sha":"new-commit"}}"""),
			_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
		});
		var client = new GitHubClient(
			new HttpClient(github), new Token(), Monitor(new GitHubAppOptions()), Monitor(new PostsRepositoryOptions { Repository = "me/posts" })
		);

		var proposal = await new GitHubReviews(client).ProposeAsync(Curb, Actor.Agent("release-bot"), CancellationToken.None);

		proposal.Number.Should().Be(7);
		var tree = JsonDocument.Parse(github.Requests.Single(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/git/trees", StringComparison.Ordinal)).Body).RootElement;
		tree.GetProperty("base_tree").GetString().Should().Be("main-tree");
		tree.GetProperty("tree").EnumerateArray().Select(e => e.GetProperty("path").GetString())
			.Should().BeEquivalentTo(["campaigns/curb-1-0/campaign.md", "campaigns/curb-1-0/01-launch.md"]);
		var branch = JsonDocument.Parse(github.Requests.Single(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/git/refs", StringComparison.Ordinal)).Body).RootElement;
		branch.GetProperty("ref").GetString().Should().Be("refs/heads/campaign/curb-1-0");
		github.Requests.Should().NotContain(r => r.Request.Method == HttpMethod.Patch && r.Request.RequestUri!.AbsolutePath.Contains("heads/main", StringComparison.Ordinal));
		var pull = JsonDocument.Parse(github.Requests.Last().Body).RootElement;
		pull.GetProperty("base").GetString().Should().Be("main");
		pull.GetProperty("body").GetString().Should().Contain("Proposed by **release-bot** (agent)").And.Contain("Merging this pull request approves the campaign.");
		github.Requests.Should().OnlyContain(r => r.Request.Headers.Authorization!.Parameter == "installation-token");
	}

	private static StaticMonitor<T> Monitor<T>(T value) => new(value);

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
