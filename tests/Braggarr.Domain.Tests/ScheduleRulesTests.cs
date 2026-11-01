using AwesomeAssertions;
using CsCheck;
using Nullean.Entities.CsCheck;

namespace Braggarr.Domain.Tests;

public class ScheduleRulesTests
{
	private static readonly Delivery Twice = Delivery.New(new DeliveryKey("curb-1-0", "99-twice", Network.Bluesky));

	/// <summary>An approved campaign with a blog post, and a Bluesky post half an hour later that links it.</summary>
	private static readonly Campaign Linked = Gens.Stamped(new Campaign(
		"hunch-0-3", "Hunch 0.3", "hunch", CampaignStatus.Approved, [Network.Bluesky, Network.Blog], [], "", "agent:release-bot",
		[
			new Post("00-blog", Gens.Start, [Gens.Drafted(Network.Blog, "# Hunch 0.3\n\nA faster index.")]) { Networks = [Network.Blog] },
			new Post("01-launch", Gens.Start + Blogging.DeployMargin, [new Variant(Network.Bluesky, $"Hunch 0.3 is out. {Blogging.Placeholder}")]) { Networks = [Network.Bluesky] }
		]
	));

	public static AutoTest<Schedule> Auto() =>
		AutoTest.For(ScheduleRules.Instance, Gens.Schedules)
			.CounterExample(ScheduleRules.OneDeliveryPerPostAndNetwork, s => s with { Deliveries = [.. s.Deliveries, Twice, Twice] })
			.CounterExample(ScheduleRules.RetriesStopAfterThreeAttempts, s => s.With(Gens.Exhausted with { State = DeliveryState.Pending }))
			// A delivery the owner was asked about, settled by the schedule itself.
			.CounterExample(
				ScheduleRules.UnknownWaitsForTheOwner,
				s => s.Deliveries.FirstOrDefault(d => d.State == DeliveryState.Unknown) is { } unknown ? s.With(unknown.ConfirmMissing()) : s
			)
			// A post that links the blog post sent while the blog post is still waiting.
			.CounterExample(
				ScheduleRules.LinksToTheBlogWaitForIt,
				s => s.Sync([.. s.Campaigns, Linked]) is var synced && synced.Find(new DeliveryKey("hunch-0-3", "01-launch", Network.Bluesky)) is { } linking
					? synced.With(linking.Begin(Gens.Start))
					: s
			)
			// A delivery nothing happened to yet, dropped while its post is still owed.
			.CounterExample(
				ScheduleRules.EveryApprovedPostIsOwed,
				s => s.Deliveries.FirstOrDefault(d => !d.HasHistory) is { } fresh ? s with { Deliveries = [.. s.Deliveries.Where(d => d != fresh)] } : s
			)
			// A delivery with a history, dropped along with its campaign's approval: nothing else is owed, so only the record is lost.
			.CounterExample(
				ScheduleRules.NothingThatHappenedIsForgotten,
				s => s.Deliveries.FirstOrDefault(d => d.HasHistory && d.Attempts < Delivery.MaxAttempts && d.State != DeliveryState.Unknown) is { } done
					? s with
					{
						Campaigns = s.Campaigns.Replace(c => c.Slug == done.Key.Campaign, s.Campaign(done.Key.Campaign)! with { Status = CampaignStatus.Proposed }),
						Deliveries = [.. s.Deliveries.Where(d => d != done)]
					}
					: s
			)
			// One attempt fewer on a delivery that is not waiting, and not out of attempts: only the count per delivery notices.
			.CounterExample(
				ScheduleRules.AttemptsNeverGoBackForAnyDelivery,
				s => s.Deliveries.FirstOrDefault(d => d.Attempts is > 0 and < Delivery.MaxAttempts && d.State != DeliveryState.Pending) is { } tried
					? s.With(tried with { Attempts = tried.Attempts - 1 })
					: s
			)
			// What the scheduler does to one delivery at a time, on a delivery that is really there.
			.Action(
				"Publish",
				(s, key) => s.Find(key) is { } d ? s.With(d.Begin(Gens.Start).Succeed(new Receipt("1", "https://bsky.app/1"))) : s,
				start => Gen.OneOfConst([.. start.Deliveries.Select(d => d.Key)])
			)
			.Action(
				"FailThreeTimes",
				(s, key) => s.Find(key) is { State: DeliveryState.Pending } d
					? s.With(Enumerable.Range(0, Delivery.MaxAttempts).Aggregate(d, (x, i) => x.Retry(Gens.Start.AddDays(i)).Begin(Gens.Start.AddDays(i)).Fail("503")))
					: s,
				start => Gen.OneOfConst([.. start.Deliveries.Select(d => d.Key)])
			)
			.Action("Resync", s => s.Sync(s.Campaigns))
			// The posts repo lost a campaign (a revert, a deleted folder): what happened to its deliveries must stay on record.
			.Action("DropACampaign", s => s.Sync(s.Campaigns.Skip(1)))
			// Long after every backoff, so a retry the schedule should not make has its chance to show.
			.Action("RetryMuchLater", s => s.Retry(Gens.Start.AddYears(1)));

	public static IEnumerable<Func<AxisCase>> Axes() => Auto().AsDataSource();

	[Test]
	[MethodDataSource(nameof(Axes))]
	public Task EveryDeclaredAxisHolds(AxisCase axis) => axis.RunAsync();

	/// <summary>Due is exactly the waiting deliveries of live campaigns whose time has come: nothing else, and nothing missed.</summary>
	[Test]
	public void Due_is_every_waiting_delivery_of_a_live_campaign_whose_time_has_come_and_nothing_else() =>
		Gen.Select(Gens.Schedules, Gen.Int[0, 72]).Sample((s, hours) =>
		{
			var now = Gens.Start.AddHours(hours);
			var expected = s.Deliveries
				.Where(d => d.State == DeliveryState.Pending && s.Campaign(d.Key.Campaign) is { IsLive: true } c && c.Posts.Any(p => p.Id == d.Key.Post && p.At <= now))
				.Select(d => d.Key)
				.ToHashSet();

			s.Due(now).Select(d => d.Key).Should().BeEquivalentTo(expected);
		});

	/// <summary>Syncing what the posts repo says twice is syncing it once, whatever the schedule held before.</summary>
	[Test]
	public void Syncing_the_same_campaigns_twice_is_syncing_them_once() =>
		Gen.Select(Gens.Schedules, Gens.Campaign("curb-1-0"), Gens.Campaign("mermaider-svg")).Sample((s, a, b) =>
		{
			var once = s.Sync([a, b]);

			once.Sync([a, b]).Should().Be(once);
			s.Sync([]).Sync([]).Should().Be(s.Sync([]));
		});

	[Test]
	public void A_post_is_due_only_once_its_time_has_come_and_only_while_the_campaign_is_live()
	{
		var campaign = Approved("curb-1-0", Gens.Start);
		var schedule = Schedule.Empty.Sync([campaign]);

		schedule.Due(Gens.Start.AddMinutes(-1)).Should().BeEmpty();
		schedule.Due(Gens.Start).Should().ContainSingle().Which.Key.Network.Should().Be(Network.Bluesky);

		schedule.Sync([campaign.Hold(Gens.Owner)]).Due(Gens.Start).Should().BeEmpty();
	}

	[Test]
	public void A_sync_that_drops_a_post_keeps_whatever_already_happened_to_it()
	{
		var campaign = Approved("curb-1-0", Gens.Start);
		var schedule = Schedule.Empty.Sync([campaign]);
		var key = schedule.Deliveries.Single().Key;
		var published = schedule.With(schedule.Find(key)!.Begin(Gens.Start).Succeed(new Receipt("1", "https://bsky.app/1")));

		var resynced = published.Sync([campaign with { Posts = [] }]);

		resynced.Find(key)!.State.Should().Be(DeliveryState.Published);
		Schedule.Empty.Sync([campaign]).Sync([campaign with { Posts = [] }]).Deliveries.Should().BeEmpty();
	}

	[Test]
	public void Recovering_after_a_restart_never_sends_again()
	{
		var schedule = Schedule.Empty.Sync([Approved("curb-1-0", Gens.Start)]);
		var sending = schedule.With(schedule.Deliveries.Single().Begin(Gens.Start));

		var recovered = sending.Recover();

		recovered.Deliveries.Single().State.Should().Be(DeliveryState.Unknown);
		recovered.Due(Gens.Start.AddDays(1)).Should().BeEmpty();
	}

	private static Campaign Approved(string slug, DateTimeOffset at) =>
		new Campaign(
			slug, "Curb 1.0", "curb", CampaignStatus.Proposed, [Network.Bluesky], [], "", "owner:mpdreamz",
			[new Post("01-launch", at, [new Variant(Network.Bluesky, "Curb 1.0 is out.")])]
		).Approve(new(Gens.Owner, Gens.Start));
}
