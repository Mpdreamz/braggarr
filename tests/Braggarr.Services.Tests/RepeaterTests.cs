using AwesomeAssertions;
using Braggarr.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

public class RepeaterTests
{
	private sealed class Amsterdam : IOwnerTime
	{
		public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
	}

	[Test]
	public async Task A_campaign_whose_repeat_is_due_is_proposed_fresh_once_and_marked()
	{
		var time = new ManualTimeProvider(SchedulerWorld.Start);
		var world = new SchedulerWorld(time);
		world.Repository.Campaigns[0] = SchedulerWorld.Approved(world.Repository.Campaigns[0] with { RepeatMonths = 3 });
		world.Service.RequestSync();
		await world.TickAsync();
		world.Manual.CloseAll();
		time.Advance(TimeSpan.FromDays(2)); // the follow-up post goes out too
		await world.TickAsync();
		world.Manual.CloseAll();
		await world.TickAsync();

		var reviews = new FakeReviews();
		var campaigns = new CampaignService(reviews, world.Repository, world.Service, DraftingWorld.Service(new FakeDrafter(), world.Repository), world.Notifier, new OwnerIn(), world.Time, world.Videos, new FakeJudges(null), new FakeAccounts());
		var repeater = new Repeater(world.Service, campaigns, world.Repository, new Amsterdam(), time, NullLogger<Repeater>.Instance);

		(await repeater.CheckAsync(CancellationToken.None)).Should().BeEmpty("three months have not passed");

		time.Advance(TimeSpan.FromDays(95));
		world.Service.RequestSync();
		await world.TickAsync();
		var proposed = await repeater.CheckAsync(CancellationToken.None);

		var again = proposed.Should().ContainSingle().Which.Campaign;
		again.Slug.Should().Be("curb-1-0-again-2027-02");
		again.CreatedBy.Should().Be("agent:repeater");
		again.RepeatMonths.Should().Be(0, "a copy does not repeat itself");
		again.Brief.Should().Contain("never the same sentences").And.Contain("curb-1-0 is out.");
		again.IsComplete.Should().BeTrue();
		world.Repository.Campaigns[0].LastRepeat.Should().Be(time.GetUtcNow());

		world.Service.RequestSync();
		await world.TickAsync();
		(await repeater.CheckAsync(CancellationToken.None)).Should().BeEmpty("it was just proposed again");
	}

	[Test]
	public async Task Only_the_owner_sets_how_a_campaign_repeats_and_within_two_years()
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));
		var campaigns = new CampaignService(new FakeReviews(), world.Repository, world.Service, DraftingWorld.Service(null, world.Repository), world.Notifier, new OwnerIn(), world.Time, world.Videos, new FakeJudges(null), new FakeAccounts());

		(await campaigns.SetRepeatAsync("curb-1-0", 3, Actor.Agent("bot"), CancellationToken.None)).Should().BeOfType<Outcome<Campaign>.Refused>();
		(await campaigns.SetRepeatAsync("curb-1-0", 36, Actor.Owner("me"), CancellationToken.None)).Should().BeOfType<Outcome<Campaign>.Refused>();
		(await campaigns.SetRepeatAsync("curb-1-0", 6, Actor.Owner("me"), CancellationToken.None)).Should().BeOfType<Outcome<Campaign>.Done>();
		world.Repository.Campaigns[0].RepeatMonths.Should().Be(6);
	}
}
