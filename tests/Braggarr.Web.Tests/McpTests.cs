using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Braggarr.Domain;

namespace Braggarr.Web.Tests;

/// <summary>The MCP endpoint over its wire: JSON-RPC posted to /mcp, answered as JSON or as one server-sent event.</summary>
public class McpTests
{
	private static readonly string[] BlueskyAndMastodon = ["bluesky", "mastodon"];

	private static async Task<(HttpStatusCode Status, JsonElement? Result)> CallAsync(HttpClient client, string method, object? parameters = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
		{
			Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters ?? new { } }), Encoding.UTF8, "application/json")
		};
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
		request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
		using var response = await client.SendAsync(request);
		if (!response.IsSuccessStatusCode)
		{
			return (response.StatusCode, null);
		}
		var body = await response.Content.ReadAsStringAsync();
		var json = body.TrimStart().StartsWith('{') ? body : body.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal))[5..];
		var root = JsonDocument.Parse(json).RootElement;
		return root.TryGetProperty("result", out var result) ? (response.StatusCode, result.Clone()) : throw new InvalidOperationException($"MCP answered: {json}");
	}

	[Test]
	public async Task Without_a_key_there_is_no_mcp()
	{
		using var factory = new BraggarrFactory();

		var (status, _) = await CallAsync(factory.Browser(), "tools/list");

		status.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Test]
	public async Task The_tools_propose_read_and_redraft_and_none_approves()
	{
		using var factory = new BraggarrFactory();

		var (_, result) = await CallAsync(factory.Agent(), "tools/list");

		var names = result!.Value.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
		names.Should().BeEquivalentTo(["list_campaigns", "get_campaign", "calendar", "propose_campaign", "propose_blog_post", "propose_introduction", "add_post", "redraft_variant", "add_image"]);
		names.Should().NotContain(n => n!.Contains("approve") || n.Contains("merge") || n.Contains("publish"));
	}

	[Test]
	public async Task A_blog_post_proposed_through_mcp_is_its_own_campaign_and_a_bad_one_is_told_why()
	{
		using var factory = new BraggarrFactory();

		var (_, refused) = await CallAsync(factory.Agent(), "tools/call", new
		{
			name = "propose_blog_post",
			arguments = new { project = "curb", title = "Curb 1.0", description = new string('x', 161), markdown = "# Curb" }
		});
		var (_, result) = await CallAsync(factory.Agent(), "tools/call", new
		{
			name = "propose_blog_post",
			arguments = new { project = "curb", title = "Curb 1.0", description = "What curb does to a diff.", markdown = "# Curb\n\nIt keeps diffs small." }
		});

		refused!.Value.GetProperty("content")[0].GetProperty("text").GetString().Should().Contain("at most 160 characters");
		result!.Value.GetProperty("content")[0].GetProperty("text").GetString().Should().Contain("\"slug\":\"blog-curb-1-0\"");
		var proposal = factory.Reviews.Open.Single().Campaign;
		proposal.CreatedBy.Should().Be("agent:release-bot");
		proposal.Posts.Single().For(Network.Blog)!.Article!.Description.Should().Be("What curb does to a diff.");
	}

	[Test]
	public async Task Proposing_through_mcp_opens_a_pull_request_in_the_agents_name()
	{
		using var factory = new BraggarrFactory();

		var (_, result) = await CallAsync(factory.Agent(), "tools/call", new
		{
			name = "propose_campaign",
			arguments = new
			{
				title = "Hunch 0.3",
				project = "hunch",
				networks = BlueskyAndMastodon,
				brief = "A faster index.",
				posts = new[] { new { at = "2026-11-05T10:00:00+01:00" } }
			}
		});

		result!.Value.GetProperty("content")[0].GetProperty("text").GetString().Should().Contain("\"slug\":\"hunch-0-3\"");
		var proposal = factory.Reviews.Open.Single().Campaign;
		proposal.CreatedBy.Should().Be("agent:release-bot");
		proposal.Status.Should().Be(CampaignStatus.Proposed);
		proposal.IsComplete.Should().BeTrue();
		factory.Reviews.Merged.Should().BeEmpty();
	}
}
