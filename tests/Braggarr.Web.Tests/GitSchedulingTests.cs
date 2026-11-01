using AwesomeAssertions;
using Braggarr.Domain;
using Braggarr.Services.Tests;
using CsCheck;
using Nullean.Entities;
using Nullean.Entities.CsCheck;

namespace Braggarr.Web.Tests;

/// <summary>
/// The scheduler's rules over random histories, against the real git repository. The same rules as the run against in-memory
/// fakes; what this adds is that what a crash leaves, and what a rebase merges, is decided by git and the real adapter.
/// </summary>
public class GitSchedulingTests
{
	public static readonly Script<GitSchedulerWorld> Script = Nullean.Entities.CsCheck.Script.For<GitSchedulerWorld>()
		.StepAsync("Tick", w => w.TickAsync())
		.Step("BlueskyRefusesNext", w => w.Bluesky.Next.Enqueue(Answer.Refused))
		.Step("MastodonGoesSilentNext", w => w.Mastodon.Next.Enqueue(Answer.Silent))
		.Step("CrashWhileSending", w => w.CrashWhileSending())
		.StepAsync("Restart", w => w.RestartAsync())
		.Step("OwnerPostsTheIssues", w => w.Manual.CloseAll())
		.Step("OwnerEditsAPost", w => w.OwnerEditsAPost())
		.StepAsync("OwnerApprovesAgain", w => w.OwnerApprovesAgainAsync())
		.Delays(Gen.Int[0, 120].Select(m => TimeSpan.FromMinutes(m)))
		.Length(1, 16);

	/// <summary>
	/// One test per axis. A history costs about four seconds of real git (a bare repo, two clones, a commit per record), so this
	/// runs 30 histories an axis of up to 16 steps, not the default 200; the run against fakes covers the breadth.
	/// </summary>
	public static IEnumerable<Func<AxisCase>> Axes() =>
		EventAutoTest.For(SchedulerRules.All, Script, time => new GitSchedulerWorld(time), w => w.Log)
			.Samples(30)
			.Cases.Select(c => (Func<AxisCase>)(() => c));

	[Test]
	[MethodDataSource(nameof(Axes))]
	public Task Every_rule_holds_over_any_history_against_the_real_git_repository(AxisCase axis) => axis.RunAsync();

	[Test]
	public async Task A_crash_while_sending_is_recovered_from_what_git_committed_and_never_resent()
	{
		var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
		using var world = new GitSchedulerWorld(time);
		world.CrashWhileSending();

		await world.TickAsync();
		await world.TickAsync();

		world.Restarts.Should().Be(1);
		world.Log.Select(e => e.Event).OfType<DeliveryEvent.MarkedUnknown>().Should().ContainSingle();
		world.Bluesky.Posted.Concat(world.Mastodon.Posted).Should().HaveCount(1, "the crashed delivery is asked about, the other one goes out");
		world.Service.Current.Deliveries.Count(d => d.State == DeliveryState.Unknown).Should().Be(1);
	}

	[Test]
	public async Task The_owners_edits_on_main_and_braggarrs_records_never_conflict()
	{
		var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
		using var world = new GitSchedulerWorld(time);

		world.OwnerEditsAPost();
		await world.TickAsync();
		world.OwnerEditsAPost();
		time.Advance(TimeSpan.FromHours(25));
		await world.TickAsync();
		var unapproved = world.Bluesky.Posted.Select(v => v.Text).ToList();
		await world.OwnerApprovesAgainAsync();
		await world.TickAsync();

		unapproved.Should().NotContain("A day in, edit 2.", "the edit undid the approval, so the follow-up waited");

		world.Service.Current.Campaign("curb-1-0")!.Posts.Single(p => p.Id == "02-follow-up").For(Network.Bluesky)!.Text.Should().Be("A day in, edit 2.");
		world.Service.Current.Deliveries.Should().Contain(d => d.State == DeliveryState.Published);
		world.Bluesky.Posted.Select(v => v.Text).Should().Contain("A day in, edit 2.");
	}
}
