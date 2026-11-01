using AwesomeAssertions;
using Braggarr.Domain;
using CsCheck;
using Nullean.Entities;
using Nullean.Entities.CsCheck;

namespace Braggarr.Services.Tests;

public class SchedulingServiceTests
{
	public static TemporalRuleSet<DeliveryEvent> Rules => SchedulerRules.All;

	public static readonly Script<SchedulerWorld> Script = Nullean.Entities.CsCheck.Script.For<SchedulerWorld>()
		.StepAsync("Tick", w => w.TickAsync())
		.Step("BlueskyRefusesNext", w => w.Bluesky.Next.Enqueue(Answer.Refused))
		.Step("MastodonGoesSilentNext", w => w.Mastodon.Next.Enqueue(Answer.Silent))
		.Step("CrashWhileSending", w => w.Repository.CrashAfterNextSending = true)
		.StepAsync("Restart", w => w.RestartAsync())
		.Step("OwnerPostsTheIssues", w => w.Manual.CloseAll())
		.Delays(Gen.Int[0, 120].Select(m => TimeSpan.FromMinutes(m)))
		.Length(1, 30);

	/// <summary>One test per axis (Absence, Temporal, Liveness, Vacancy), over histories against in-memory fakes.</summary>
	public static IEnumerable<Func<AxisCase>> Axes() =>
		EventAutoTest.For(Rules, Script, time => new SchedulerWorld(time), w => w.Log).Cases.Select(c => (Func<AxisCase>)(() => c));

	[Test]
	[MethodDataSource(nameof(Axes))]
	public Task Every_rule_holds_over_any_history_of_ticks_failures_and_crashes(AxisCase axis) => axis.RunAsync();

	[Test]
	public async Task A_post_goes_out_once_its_time_has_come_and_is_recorded_before_and_after()
	{
		var time = new ManualTimeProvider(SchedulerWorld.Start);
		var world = new SchedulerWorld(time);

		await world.TickAsync();

		world.Bluesky.Posted.Should().ContainSingle().Which.Text.Should().Be("curb-1-0 is out.");
		world.Repository.Deliveries.Values.Where(d => d.State == DeliveryState.Published).Should().HaveCount(2);
		world.Journal.Events.Select(e => e.Event.GetType().Name).Should().StartWith(["Started", "Published"]);
	}

	[Test]
	public async Task A_network_that_never_answers_marks_the_delivery_unknown_after_the_timeout()
	{
		var time = new ManualTimeProvider(SchedulerWorld.Start);
		var world = new SchedulerWorld(time);
		world.Bluesky.Next.Enqueue(Answer.Hangs);

		var tick = world.TickAsync();
		time.Advance(TimeSpan.FromSeconds(61));
		await tick;

		var bluesky = world.Repository.Deliveries.Values.Single(d => d.Key is { Campaign: "curb-1-0", Post: "01-launch", Network: Network.Bluesky });
		bluesky.State.Should().Be(DeliveryState.Unknown);
		world.Notifier.Sent.Should().Contain(n => n.Level == NoticeLevel.Problem);
	}

	[Test]
	public async Task A_crash_while_sending_is_asked_about_and_never_resent()
	{
		var time = new ManualTimeProvider(SchedulerWorld.Start);
		var world = new SchedulerWorld(time);
		world.Repository.CrashAfterNextSending = true;

		await world.TickAsync();
		time.Advance(TimeSpan.FromDays(3));
		await world.TickAsync();

		world.Restarts.Should().Be(1);
		world.Bluesky.Posted.Should().NotContain(v => v.Text == "curb-1-0 is out.");
		world.Notifier.Sent.Should().Contain(n => n.Title == "Did this post go out?");
	}

	[Test]
	public async Task Reddit_waits_for_the_owner_and_is_done_once_the_issue_is_closed()
	{
		var time = new ManualTimeProvider(SchedulerWorld.Start);
		var world = new SchedulerWorld(time);

		await world.TickAsync();
		var reddit = new DeliveryKey("curb-1-0", "01-launch", Network.Reddit);
		world.Repository.Deliveries[reddit].State.Should().Be(DeliveryState.ManualDue);

		world.Manual.CloseAll();
		await world.TickAsync();
		world.Repository.Deliveries[reddit].State.Should().Be(DeliveryState.ManualDone);
	}

	[Test]
	public async Task A_due_reddit_post_sends_a_notice_whose_tap_opens_the_submit_form()
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));

		await world.TickAsync();

		var notice = world.Notifier.Sent.Single(n => n.Title.StartsWith("Time to post on Reddit", StringComparison.Ordinal));
		notice.Level.Should().Be(NoticeLevel.Action);
		notice.Url.Should().Be("https://www.reddit.com/submit?url=x");
		notice.Actions.Select(a => a.Label).Should().Equal("Submit to Reddit", "Issue");
	}

	[Test]
	public async Task Saying_it_is_posted_closes_the_issue_and_finishes_the_delivery()
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));
		await world.TickAsync();
		var reddit = new DeliveryKey("curb-1-0", "01-launch", Network.Reddit);
		var issue = world.Repository.Deliveries[reddit].Issue!.Value;

		await world.Service.MarkManualDoneAsync(reddit, CancellationToken.None);

		world.Manual.Closed.Should().Contain(issue);
		world.Repository.Deliveries[reddit].State.Should().Be(DeliveryState.ManualDone);
	}

	[Test]
	public async Task A_held_campaign_sends_nothing_until_it_is_resumed()
	{
		var time = new ManualTimeProvider(SchedulerWorld.Start);
		var world = new SchedulerWorld(time);
		await world.Repository.SetHeldAsync("curb-1-0", held: true, Actor.Owner("mpdreamz"), CancellationToken.None);
		world.Service.RequestSync();

		await world.TickAsync();

		world.Bluesky.Posted.Should().BeEmpty();
	}
}
