using System.Net.Http.Json;
using System.Net;
using AwesomeAssertions;
using Braggarr.Domain;
using Braggarr.Web.Settings;

namespace Braggarr.Web.Tests;

/// <summary>Writing a project's brief on its page, with Claude's suggestion to start from.</summary>
public class BriefPageTests
{
	private static readonly string[] Bluesky = ["bluesky"];

	private static readonly string[] CurbLinks = ["https://curb.nullean.net"];

	private static async Task<(BraggarrFactory Factory, HttpClient Browser)> SignedInAsync(bool watched = true)
	{
		var factory = new BraggarrFactory();
		if (watched)
		{
			_ = factory.Settings.Update(s => s with { Projects = [new ProjectSettings("nullean/curb", "curb", ["bluesky"])] });
		}
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);
		return (factory, browser);
	}

	private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields) =>
		new(fields.Select(f => KeyValuePair.Create(f.Key, f.Value)).Prepend(KeyValuePair.Create("__RequestVerificationToken", token)));

	[Test]
	public async Task Claude_fills_the_form_without_keeping_it_and_saving_keeps_it_on_main()
	{
		var (factory, browser) = await SignedInAsync();
		using var owned = factory;
		var token = await BraggarrFactory.FormTokenAsync(browser, "/Projects/curb");

		var suggested = await (await browser.PostAsync("/Projects/curb?handler=Suggest", Form(token, ("project", "curb")))).Content.ReadAsStringAsync();
		var keptBefore = factory.Repository.Briefs.Count;
		var saved = await browser.PostAsync("/Projects/curb?handler=SaveBrief", Form(token,
			("project", "curb"), ("tldr", "A C# formatter that keeps diffs small."), ("apart", "It never moves a line it does not have to."),
			("audience", "C# teams"), ("avoid", "Calling it a linter\n\n"), ("links", "Docs: https://curb.dev\nhttps://www.nuget.org/packages/curb")));

		suggested.Should().Contain("Nothing is saved yet").And.Contain("value=\"curb: A formatter.\"").And.Contain("Keep this brief");
		keptBefore.Should().Be(0);
		saved.StatusCode.Should().Be(HttpStatusCode.OK);
		factory.Repository.Briefs.Should().ContainSingle().Which.Should().BeEquivalentTo(
			new ProjectBrief("curb", "A C# formatter that keeps diffs small.", "It never moves a line it does not have to.", "C# teams")
			{
				Avoid = ["Calling it a linter"],
				Links = [new BriefLink("Docs", "https://curb.dev"), new BriefLink("www.nuget.org", "https://www.nuget.org/packages/curb")]
			});
		(await browser.GetStringAsync("/Projects")).Should().Contain("A C# formatter that keeps diffs small.");
	}

	[Test]
	public async Task A_brief_that_breaks_a_rule_comes_back_with_what_was_typed_and_why()
	{
		var (factory, browser) = await SignedInAsync();
		using var owned = factory;
		var token = await BraggarrFactory.FormTokenAsync(browser, "/Projects/curb");

		var page = await (await browser.PostAsync("/Projects/curb?handler=SaveBrief", Form(token, ("project", "curb"), ("tldr", "Formats C#."), ("apart", " "),
			("links", "Docs: ftp://curb.dev")))).Content.ReadAsStringAsync();

		page.Should().Contain("Say what sets it apart").And.Contain("Every link has a label").And.Contain("value=\"Formats C#.\"");
		factory.Repository.Briefs.Should().BeEmpty();
	}

	[Test]
	public async Task A_project_nobody_watches_has_a_brief_but_no_claude_button()
	{
		var (factory, browser) = await SignedInAsync(watched: false);
		using var owned = factory;

		var page = await browser.GetStringAsync("/Projects/heed");

		page.Should().NotContain("Fill the brief with Claude").And.Contain("Watch its releases to have Claude suggest one");
		(await factory.Agent().GetAsync("/Projects/heed")).StatusCode.Should().NotBe(HttpStatusCode.OK, "a key never reaches the owner's pages");
	}

	[Test]
	public async Task A_watched_project_is_edited_on_its_page_and_lists_its_campaigns()
	{
		var (factory, browser) = await SignedInAsync();
		using var owned = factory;
		_ = await factory.Agent().PostAsJsonAsync("/api/v1/campaigns", new
		{
			title = "Curb 1.0",
			project = "curb",
			networks = Bluesky,
			links = CurbLinks,
			brief = "Announce 1.0.",
			posts = new[] { new { at = "2030-11-03T10:00:00+01:00", variants = new Dictionary<string, string> { ["bluesky"] = "Curb 1.0 is out." } } }
		});
		var token = await BraggarrFactory.FormTokenAsync(browser, "/Projects/curb");

		var page = await browser.GetStringAsync("/Projects/curb");
		_ = await browser.PostAsync("/Projects/curb?handler=Watching", Form(token, ("project", "curb"), ("networks", "Bluesky"), ("networks", "X"), ("prereleases", "true")));
		var edited = factory.Settings.Current.Projects.Single();
		var none = await (await browser.PostAsync("/Projects/curb?handler=Watching", Form(token, ("project", "curb")))).Content.ReadAsStringAsync();
		var unwatched = await browser.PostAsync("/Projects/curb?handler=Unwatch", Form(token, ("repository", "nullean/curb")));

		page.Should().Contain("<a href=\"/Campaigns/Proposal?number=1\">Curb 1.0</a>").And.Contain("in review").And.Contain("Save the brief");
		edited.Should().BeEquivalentTo(new ProjectSettings("nullean/curb", "curb", ["bluesky", "x"], IncludePrereleases: true, AskClaude: false));
		none.Should().Contain("Choose at least one network");
		unwatched.Headers.Location!.OriginalString.Should().Be("/Projects");
		factory.Settings.Current.Projects.Should().BeEmpty();
	}

	[Test]
	public async Task A_project_nobody_watches_can_start_being_watched_from_its_page()
	{
		var (factory, browser) = await SignedInAsync(watched: false);
		using var owned = factory;
		var token = await BraggarrFactory.FormTokenAsync(browser, "/Projects/heed");

		var bad = await (await browser.PostAsync("/Projects/heed?handler=Watch", Form(token, ("project", "heed"), ("repository", "not a repo")))).Content.ReadAsStringAsync();
		_ = await browser.PostAsync("/Projects/heed?handler=Watch", Form(token, ("project", "heed"), ("repository", "https://github.com/heed-build/heed/")));

		bad.Should().Contain("owner/name");
		factory.Settings.Current.Projects.Should().ContainSingle().Which.Should().Match<ProjectSettings>(p => p.Repository == "heed-build/heed" && p.Name == "heed" && !p.Networks.Contains("hackernews"));
	}
}
