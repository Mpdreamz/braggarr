using System.Net;
using AwesomeAssertions;
using Braggarr.Web.Settings;

namespace Braggarr.Web.Tests;

/// <summary>The about page's groups on the Projects page: added, described, moved, removed, and projects put in them.</summary>
public class AboutGroupsTests
{
	private static async Task<(BraggarrFactory Factory, HttpClient Browser, string Token)> SignedInAsync()
	{
		var factory = new BraggarrFactory();
		_ = factory.Settings.Update(s => s with
		{
			Projects = [new ProjectSettings("nullean/curb", "curb", ["bluesky"]), new ProjectSettings("heed-build/heed", "heed", ["bluesky"])]
		});
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);
		return (factory, browser, await BraggarrFactory.FormTokenAsync(browser, "/Projects"));
	}

	private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields) =>
		new(fields.Select(f => KeyValuePair.Create(f.Key, f.Value)).Prepend(KeyValuePair.Create("__RequestVerificationToken", token)));

	[Test]
	public async Task Groups_are_added_in_order_described_again_by_title_and_moved()
	{
		var (factory, browser, token) = await SignedInAsync();

		_ = await browser.PostAsync("/Projects?handler=Group", Form(token, ("title", ".NET tools"), ("description", "")));
		_ = await browser.PostAsync("/Projects?handler=Group", Form(token, ("title", "For the Mac"), ("description", "Apps.")));
		var described = await browser.PostAsync("/Projects?handler=Group", Form(token, ("title", "for the mac "), ("description", "Apps for Apple silicon.")));
		_ = await browser.PostAsync("/Projects?handler=MoveGroup", Form(token, ("title", "For the Mac"), ("direction", "up")));
		_ = await browser.PostAsync("/Projects?handler=MoveGroup", Form(token, ("title", "For the Mac"), ("direction", "up")));

		described.StatusCode.Should().Be(HttpStatusCode.OK);
		factory.Settings.Current.ProjectGroups.Should().Equal(new ProjectGroupSettings("For the Mac", "Apps for Apple silicon."), new ProjectGroupSettings(".NET tools"));
	}

	[Test]
	public async Task A_project_is_put_in_a_group_and_removing_the_group_leaves_it_in_none()
	{
		var (factory, browser, token) = await SignedInAsync();
		_ = await browser.PostAsync("/Projects?handler=Group", Form(token, ("title", "For the Mac")));

		_ = await browser.PostAsync("/Projects?handler=Assign", Form(token, ("repository", "heed-build/heed"), ("group", "for the mac")));
		var page = await browser.GetStringAsync("/Projects");
		var unknown = await (await browser.PostAsync("/Projects?handler=Assign", Form(token, ("repository", "nullean/curb"), ("group", "Nope")))).Content.ReadAsStringAsync();
		var heed = factory.Settings.Current.Projects.Single(p => p.Name == "heed").Group;
		_ = await browser.PostAsync("/Projects?handler=RemoveGroup", Form(token, ("title", "For the Mac")));

		heed.Should().Be("For the Mac");
		page.Should().Contain("<option value=\"For the Mac\" selected=\"selected\">For the Mac</option>");
		unknown.Should().Contain("There is no group Nope.");
		factory.Settings.Current.Projects.Single(p => p.Name == "curb").Group.Should().BeNull();
		factory.Settings.Current.ProjectGroups.Should().BeEmpty();
		factory.Settings.Current.Projects.Should().OnlyContain(p => p.Group == null);
	}

	[Test]
	public async Task A_group_needs_a_title_that_fits()
	{
		var (factory, browser, token) = await SignedInAsync();

		var empty = await (await browser.PostAsync("/Projects?handler=Group", Form(token, ("title", " ")))).Content.ReadAsStringAsync();
		var long_ = await (await browser.PostAsync("/Projects?handler=Group", Form(token, ("title", new string('a', 61))))).Content.ReadAsStringAsync();

		empty.Should().Contain("Give the group a title of at most 60 characters.");
		long_.Should().Contain("Give the group a title of at most 60 characters.");
		factory.Settings.Current.ProjectGroups.Should().BeEmpty();
	}

	[Test]
	public async Task Proposing_the_about_page_runs_in_the_background_and_says_so()
	{
		var (_, browser, token) = await SignedInAsync();

		var page = await (await browser.PostAsync("/Projects?handler=About", Form(token, ("redraft", "true")))).Content.ReadAsStringAsync();

		page.Should().Contain("The about page is being put together.");
	}
}
