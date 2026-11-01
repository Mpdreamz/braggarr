using AwesomeAssertions;
using Braggarr.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Braggarr.Web.Tests;

/// <summary>The calendar's rebalance dialog: options, a preview of every move, then applying it.</summary>
public class RebalancePageTests
{
	private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields) =>
		new(fields.Select(f => KeyValuePair.Create(f.Key, f.Value)).Prepend(KeyValuePair.Create("__RequestVerificationToken", token)));

	[Test]
	public async Task The_owner_previews_a_rebalance_and_applies_it_and_the_options_are_kept()
	{
		using var factory = new BraggarrFactory();
		var now = factory.Time.GetUtcNow();
		// The next Saturday, at ten in Amsterdam.
		var saturday = now.Date.AddDays(((DayOfWeek.Saturday - now.DayOfWeek + 7) % 7) + 7);
		var at = new DateTimeOffset(saturday.AddHours(10), TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam").GetUtcOffset(saturday.AddHours(10)));
		var campaign = new Campaign("curb-1-0", "Curb 1.0", "curb", CampaignStatus.Approved, [Network.Bluesky], ["https://github.com/nullean/curb"], "", "owner:mpdreamz",
			[new Post("01-launch", at, [new Variant(Network.Bluesky, "Curb 1.0 is out.")])]);
		factory.Repository.Campaigns.Add(campaign with { Approval = new Approval("owner:mpdreamz", campaign.Digest, now) });
		await factory.Services.GetRequiredService<Braggarr.Services.SchedulingService>().StartAsync(CancellationToken.None);
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);

		var dialog = await browser.GetStringAsync("/Calendar?rebalance=true");
		var token = await BraggarrFactory.FormTokenAsync(browser, "/Calendar?rebalance=true");
		(string, string)[] options = [("rest", "Saturday"), ("rest", "Sunday"), ("perDay", "1"), ("from", "08:00"), ("to", "12:00")];
		var preview = await (await browser.PostAsync("/Calendar?handler=Preview&month=0", Form(token, options))).Content.ReadAsStringAsync();
		var kept = factory.Settings.Current.Rebalance;
		var refused = await (await browser.PostAsync("/Calendar?handler=Preview&month=0", Form(token,
			("rest", "Monday"), ("rest", "Tuesday"), ("rest", "Wednesday"), ("rest", "Thursday"), ("rest", "Friday"), ("rest", "Saturday"), ("rest", "Sunday"),
			("perDay", "1"), ("from", "08:00"), ("to", "12:00")))).Content.ReadAsStringAsync();
		var applied = await (await browser.PostAsync("/Calendar?handler=Apply&month=0", Form(token, options))).Content.ReadAsStringAsync();

		dialog.Should().Contain("<dialog open class=\"modal\"").And.Contain("name=\"rest\" value=\"Saturday\" checked=\"checked\"");
		preview.Should().Contain("Curb 1.0").And.Contain("Move 1 post");
		kept.Should().BeEquivalentTo(new Braggarr.Web.Settings.RebalanceSettings { RestDays = ["Saturday", "Sunday"], PerDay = 1, From = "08:00", To = "12:00" });
		refused.Should().Contain("Leave at least one day of the week to post on.");
		applied.Should().Contain("Moved 1 post.").And.Contain("Approved again for their new times: Curb 1.0.");
		var moved = factory.Repository.Campaigns.Single();
		TimeZoneInfo.ConvertTime(moved.Posts.Single().At, TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam")).DayOfWeek.Should().Be(DayOfWeek.Monday);
		moved.IsApproved.Should().BeTrue();
	}
}
