using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Braggarr.Web.Api;
using CsCheck;
using Nullean.Entities.CsCheck;

namespace Braggarr.Web.Tests;

/// <summary>A blog post from an agent: its own small campaign, through the API and MCP, held to what a request must say.</summary>
public class BlogRequestTests
{
	private static readonly string[] Dotnet = ["dotnet"];
	private static readonly string[] Bluesky = ["bluesky"];
	private static readonly string[] Myspace = ["myspace"];

	private static readonly Gen<BlogRequest> Requests =
		from title in Gen.OneOfConst("Curb 1.0, from the start", "Why diffs stay small")
		from tags in Gen.OneOfConst<string[]?>(null, ["dotnet"], ["dotnet", "formatting"])
		from announce in Gen.OneOfConst<string[]?>(null, ["bluesky"], ["bluesky", "hackernews"])
		from slug in Gen.OneOfConst<string?>(null, "curb-1-0")
		select new BlogRequest("curb", title, "What curb does to a diff.", "# Curb\n\nIt keeps diffs small.", tags, null, announce, slug);

	public static IEnumerable<Func<AxisCase>> Axes() =>
		AutoTest.For(BlogRequestRules.Instance, Requests)
			.CounterExample(BlogRequestRules.NamesItsProject, r => r with { Project = " " })
			.CounterExample(BlogRequestRules.HasATitle, r => r with { Title = new string('x', 101) })
			.CounterExample(BlogRequestRules.HasADescription, r => r with { Description = new string('x', 161) })
			.CounterExample(BlogRequestRules.SaysSomething, r => r with { Markdown = " " })
			.CounterExample(BlogRequestRules.TagsAreSlugs, r => r with { Tags = ["Not A Slug"] })
			.CounterExample(BlogRequestRules.AnnouncesOnKnownNetworks, r => r with { Announce = ["blog"] })
			.CounterExample(BlogRequestRules.SlugIsAFolderName, r => r with { Slug = "../elsewhere" })
			.AsDataSource();

	[Test]
	[MethodDataSource(nameof(Axes))]
	public Task EveryDeclaredAxisHolds(AxisCase axis) => axis.RunAsync();

	[Test]
	public async Task A_blog_post_is_proposed_as_its_own_campaign_with_its_article_and_an_announcement_that_links_it()
	{
		using var factory = new BraggarrFactory();

		var response = await factory.Agent().PostAsJsonAsync("/api/v1/blog", new
		{
			project = "curb",
			title = "Curb 1.0, from the start",
			description = "What curb does to a diff.",
			markdown = "# Curb\n\nIt keeps diffs small.",
			tags = Dotnet,
			at = "2030-11-05T10:00:00+01:00",
			announce = Bluesky
		});

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		response.Headers.Location!.ToString().Should().Be("/api/v1/campaigns/blog-curb-1-0-from-the-start");
		var campaign = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
		campaign.GetProperty("networks").EnumerateArray().Select(n => n.GetString()).Should().Equal("bluesky", "blog");
		var posts = campaign.GetProperty("posts").EnumerateArray().ToList();
		posts.Select(p => p.GetProperty("id").GetString()).Should().Equal("00-blog", "01-announce");
		var blog = posts[0].GetProperty("variants").EnumerateArray().Single();
		blog.GetProperty("article").GetProperty("title").GetString().Should().Be("Curb 1.0, from the start");
		blog.GetProperty("limit").GetInt32().Should().Be(0);
		posts[1].GetProperty("at").GetDateTimeOffset().Should().Be(new DateTimeOffset(2030, 11, 5, 10, 30, 0, TimeSpan.FromHours(1)));
		factory.Reviews.Open.Should().ContainSingle(p => p.Campaign.Slug == "blog-curb-1-0-from-the-start");
	}

	[Test]
	public async Task A_blog_request_that_says_too_little_is_refused_with_each_problem()
	{
		using var factory = new BraggarrFactory();

		var response = await factory.Agent().PostAsJsonAsync("/api/v1/blog", new { project = "curb", title = new string('x', 101), description = "", markdown = "x", announce = Myspace });

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().Contain("at most 100 characters").And.Contain("at most 160 characters").And.Contain("Announce on bluesky");
		factory.Reviews.Open.Should().BeEmpty();
	}
}
