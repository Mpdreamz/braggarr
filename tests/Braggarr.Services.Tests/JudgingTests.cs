using AwesomeAssertions;
using Braggarr.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

/// <summary>
/// The judge reads proposals and changed campaigns against the owner's criteria, writes its verdict next to them, and stands in
/// the way of approving what a blocking criterion failed. It approves by itself only when told it may, only when everything
/// passed, and never so that anything goes out before the owner's delay.
/// </summary>
public class JudgingTests
{
	private static readonly Actor Owner = Actor.Owner("mpdreamz");
	private static readonly Criterion Honest = new("honest", "Claims nothing the links do not show.", Blocks: true);
	private static readonly Criterion Hashtags = new("hashtags", "No more than two hashtags.", Blocks: false);

	private sealed class World
	{
		public World(bool autoApprove = false, IJudge? judge = null)
		{
			Time = new ManualTimeProvider(SchedulerWorld.Start);
			Scheduler = new SchedulerWorld(Time);
			Options.Value.AutoApprove = autoApprove;
			Judge = judge as FakeJudge ?? new FakeJudge();
			var judges = new FakeJudges(judge ?? Judge);
			Campaigns = new CampaignService(Reviews, Scheduler.Repository, Scheduler.Service, DraftingWorld.Service(null, Scheduler.Repository),
				Scheduler.Notifier, new OwnerIn(), Time, Scheduler.Videos, judges, new FakeAccounts());
			Judging = new JudgingService(judges, Scheduler.Repository, Reviews, Scheduler.Service, Campaigns, DraftingWorld.Service(null, Scheduler.Repository), Scheduler.Notifier, Usage, Options,
				new OwnerIn(), Time, NullLogger<JudgingService>.Instance);
			Scheduler.Repository.Criteria.AddRange([Honest, Hashtags]);
		}

		public ManualTimeProvider Time { get; }
		public SchedulerWorld Scheduler { get; }
		public FakeReviews Reviews { get; } = new();
		public FakeUsage Usage { get; } = new();
		public FakeJudge Judge { get; }
		public Monitor Options { get; } = new();
		public CampaignService Campaigns { get; }
		public JudgingService Judging { get; }

		public Proposal Proposal => Reviews.Open.Single();

		public async Task<Proposal> ProposeAsync()
		{
			_ = await Campaigns.ProposeAsync(Draft(), Actor.Agent("release-bot"), CancellationToken.None);
			return Proposal;
		}
	}

	public sealed class Monitor : IOptionsMonitor<JudgingOptions>
	{
		public JudgingOptions Value { get; } = new();
		public JudgingOptions CurrentValue => Value;
		public JudgingOptions Get(string? name) => Value;
		public IDisposable? OnChange(Action<JudgingOptions, string?> listener) => null;
	}

	private static CampaignDraft Draft() =>
		new(null, "Mermaider 2.0", "mermaider", [Network.Bluesky, Network.Mastodon], ["https://github.com/nullean/mermaider"], "Announce 2.0.",
			[new PostDraft(null, SchedulerWorld.Start.AddHours(1), new Dictionary<Network, string> { [Network.Bluesky] = "Mermaider 2.0.", [Network.Mastodon] = "Mermaider 2.0 is out." })]);

	private static Func<JudgeRequest, IReadOnlyList<Finding>> Failing(string criterion) => r =>
		[.. Criteria.Expected(r.Campaign, r.Criteria).Select(e => new Finding(e.Criterion.Name, e.Post, e.Network, e.Criterion.Name != criterion, false, "Because."))];

	[Test]
	public async Task A_proposal_is_judged_once_per_version_against_every_criterion_and_variant_and_the_verdict_is_written_to_it()
	{
		var world = new World();
		var proposal = await world.ProposeAsync();

		await world.Judging.CheckAsync(CancellationToken.None);
		await world.Judging.CheckAsync(CancellationToken.None);

		var judgement = world.Proposal.Campaign.Judgement!;
		world.Judge.Requests.Should().ContainSingle("a version already judged against the same criteria is not paid for again");
		judgement.Digest.Should().Be(proposal.Campaign.Digest);
		judgement.Criteria.Should().Be(Criteria.Digest([Honest, Hashtags]));
		judgement.Findings.Should().HaveCount(4, "two criteria, one post, two networks");
		judgement.At.Should().Be(world.Time.GetUtcNow());
		judgement.Model.Should().Be("fake-judge");
		world.Usage.Calls.Should().ContainSingle();
		world.Reviews.Merged.Should().BeEmpty("the judge approves nothing unless the owner lets it");
	}

	[Test]
	public async Task New_criteria_mean_judging_again()
	{
		var world = new World();
		_ = await world.ProposeAsync();
		await world.Judging.CheckAsync(CancellationToken.None);

		world.Scheduler.Repository.Criteria[1] = Hashtags with { Prompt = "No hashtags at all." };
		await world.Judging.CheckAsync(CancellationToken.None);

		world.Judge.Requests.Should().HaveCount(2);
	}

	[Test]
	public async Task What_the_judge_leaves_out_fails_and_whether_a_failure_blocks_is_the_criterions_to_say()
	{
		var world = new World();
		// Says one finding only, and claims a warning criterion blocks while the blocking one does not.
		world.Judge.Says = r => [new Finding("hashtags", r.Campaign.Posts[0].Id, Network.Bluesky, Passed: false, Blocks: true, "Five hashtags.")];
		_ = await world.ProposeAsync();

		await world.Judging.CheckAsync(CancellationToken.None);

		var findings = world.Proposal.Campaign.Judgement!.Findings;
		findings.Should().HaveCount(4);
		findings.Single(f => f.Criterion == "hashtags" && f.Network == Network.Bluesky).Should().Match<Finding>(f => !f.Passed && !f.Blocks && f.Reason == "Five hashtags.");
		findings.Where(f => f.Criterion == "honest").Should().OnlyContain(f => !f.Passed && f.Blocks);
		world.Proposal.Campaign.Judgement!.Blocked.Should().BeTrue();
	}

	[Test]
	public async Task A_blocking_failure_stops_the_owner_too_and_says_why()
	{
		var world = new World();
		world.Judge.Says = Failing("honest");
		_ = await world.ProposeAsync();
		await world.Judging.CheckAsync(CancellationToken.None);

		var outcome = await world.Campaigns.ApproveAsync(1, Owner, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Should().Contain(r => r.StartsWith("honest,", StringComparison.Ordinal));
		world.Reviews.Merged.Should().BeEmpty();
		world.Scheduler.Notifier.Sent.Should().ContainSingle(n => n.Title == "The judge blocks Mermaider 2.0");
	}

	[Test]
	public async Task The_owner_approves_over_a_warning_and_the_judge_does_not()
	{
		var world = new World(autoApprove: true);
		world.Judge.Says = Failing("hashtags");
		_ = await world.ProposeAsync();
		await world.Judging.CheckAsync(CancellationToken.None);

		world.Reviews.Merged.Should().BeEmpty("the judge approves only when everything passed");
		(await world.Campaigns.ApproveAsync(1, Actor.Judge, CancellationToken.None)).Should().BeOfType<Outcome<Proposal>.Refused>();
		world.Scheduler.Notifier.Sent.Should().ContainSingle(n => n.Title == "The judge has warnings on Mermaider 2.0");
		(await world.Campaigns.ApproveAsync(1, Owner, CancellationToken.None)).Should().BeOfType<Outcome<Proposal>.Done>();
	}

	[Test]
	public async Task Nobody_approves_a_version_the_judge_has_not_seen()
	{
		var world = new World();
		_ = await world.ProposeAsync();

		var outcome = await world.Campaigns.ApproveAsync(1, Owner, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Should().ContainSingle(r => r.StartsWith("The judge has not judged", StringComparison.Ordinal));
	}

	[Test]
	public async Task Without_criteria_the_owner_approves_as_before_and_the_judge_cannot()
	{
		var world = new World();
		world.Scheduler.Repository.Criteria.Clear();
		_ = await world.ProposeAsync();
		await world.Judging.CheckAsync(CancellationToken.None);

		world.Judge.Requests.Should().BeEmpty();
		(await world.Campaigns.ApproveAsync(1, Actor.Judge, CancellationToken.None)).Should().BeOfType<Outcome<Proposal>.Refused>();
		(await world.Campaigns.ApproveAsync(1, Owner, CancellationToken.None)).Should().BeOfType<Outcome<Proposal>.Done>();
	}

	[Test]
	public async Task Without_a_judge_the_owner_approves_as_before()
	{
		var time = new ManualTimeProvider(SchedulerWorld.Start);
		var scheduler = new SchedulerWorld(time);
		scheduler.Repository.Criteria.Add(Honest);
		var reviews = new FakeReviews();
		var campaigns = new CampaignService(reviews, scheduler.Repository, scheduler.Service, DraftingWorld.Service(null, scheduler.Repository),
			scheduler.Notifier, new OwnerIn(), time, scheduler.Videos, new FakeJudges(null), new FakeAccounts());
		_ = await campaigns.ProposeAsync(Draft(), Actor.Agent("release-bot"), CancellationToken.None);

		(await campaigns.ApproveAsync(1, Owner, CancellationToken.None)).Should().BeOfType<Outcome<Proposal>.Done>();
	}

	[Test]
	public async Task A_judge_that_fails_to_answer_writes_nothing_and_is_asked_again()
	{
		var world = new World();
		var calls = 0;
		world.Judge.Says = r => ++calls == 1 ? throw new DraftException("Overloaded.") : new FakeJudge().Says(r);
		_ = await world.ProposeAsync();

		await world.Judging.CheckAsync(CancellationToken.None);
		var first = world.Proposal.Campaign.Judgement;
		await world.Judging.CheckAsync(CancellationToken.None);

		first.Should().BeNull();
		world.Proposal.Campaign.Judgement.Should().NotBeNull();
	}

	[Test]
	public async Task A_proposal_still_being_written_is_not_judged()
	{
		var world = new World();
		_ = await world.Campaigns.ProposeAsync(Draft() with { Posts = [new PostDraft(null, SchedulerWorld.Start.AddHours(1), new Dictionary<Network, string>())] },
			Actor.Agent("release-bot"), CancellationToken.None);

		await world.Judging.CheckAsync(CancellationToken.None);

		world.Judge.Requests.Should().BeEmpty();
	}

	[Test]
	public async Task The_judge_gets_the_criteria_and_the_voice_from_the_repository()
	{
		var world = new World();
		world.Scheduler.Repository.Voice = "Dry. No exclamation marks.";
		_ = await world.ProposeAsync();

		await world.Judging.CheckAsync(CancellationToken.None);

		world.Judge.Requests.Single().Criteria.Should().Equal(Honest, Hashtags);
		world.Judge.Requests.Single().Voice.Should().Be("Dry. No exclamation marks.");
	}

	[Test]
	public async Task Only_when_let_and_everything_passed_does_the_judge_approve_and_say_so()
	{
		var world = new World(autoApprove: true);
		_ = await world.ProposeAsync();

		await world.Judging.CheckAsync(CancellationToken.None);

		var merged = world.Reviews.Merged.Single();
		merged.By.Should().Be(Actor.Judge);
		merged.Proposal.Campaign.Approval.Should().Be(
			new Approval("judge:claude", merged.Proposal.Campaign.Digest, SchedulerWorld.Start) { NotBefore = SchedulerWorld.Start.AddHours(12) });
		world.Scheduler.Notifier.Sent.Should().ContainSingle(n => n.Title == "The judge approved Mermaider 2.0" && n.Message.Contains("Nothing goes out before"));
	}

	[Test]
	public async Task What_the_judge_approves_waits_for_its_delay()
	{
		var world = new World(autoApprove: true);
		_ = await world.ProposeAsync();
		await world.Judging.CheckAsync(CancellationToken.None);
		world.Scheduler.Repository.Campaigns.Add(world.Reviews.Merged.Single().Proposal.Campaign with { Status = CampaignStatus.Approved });
		world.Scheduler.Service.RequestSync();

		world.Time.Advance(TimeSpan.FromHours(2));
		await world.Scheduler.TickAsync();
		var early = world.Scheduler.Bluesky.Posted.Count(v => v.Text == "Mermaider 2.0.");
		world.Time.Advance(TimeSpan.FromHours(10));
		await world.Scheduler.TickAsync();

		early.Should().Be(0, "its time came an hour in, but the judge's approval holds it for twelve");
		world.Scheduler.Bluesky.Posted.Should().Contain(v => v.Text == "Mermaider 2.0.");
	}

	[Test]
	public async Task A_campaign_edited_on_main_after_approval_is_judged_again_and_the_judge_may_approve_it_again()
	{
		var world = new World(autoApprove: true);
		var approved = (await world.ProposeAsync()).Campaign;
		var stamp = new Approval("owner:mpdreamz", approved.Digest, SchedulerWorld.Start);
		var edited = approved with
		{
			Status = CampaignStatus.Approved,
			Approval = stamp,
			Posts = [approved.Posts[0] with { Variants = [.. approved.Posts[0].Variants.Select(v => v with { Text = v.Text + " Now faster." })] }]
		};
		world.Scheduler.Repository.Campaigns.Add(edited);
		world.Reviews.Open.Clear();
		world.Scheduler.Service.RequestSync();
		await world.Scheduler.TickAsync();
		world.Scheduler.Service.Current.Campaigns.Single(c => c.Slug == edited.Slug).NeedsApproval.Should().BeTrue();

		await world.Judging.CheckAsync(CancellationToken.None);
		await world.Scheduler.TickAsync();

		var now = world.Scheduler.Service.Current.Campaigns.Single(c => c.Slug == edited.Slug);
		now.Judgement!.Digest.Should().Be(edited.Digest);
		now.Approval.Should().Be(new Approval("judge:claude", edited.Digest, SchedulerWorld.Start) { NotBefore = SchedulerWorld.Start.AddHours(12) });
		now.NeedsApproval.Should().BeFalse();
	}

	[Test]
	public async Task A_campaign_on_main_that_fails_stays_unapproved_and_the_owner_is_told()
	{
		var world = new World(autoApprove: true);
		world.Judge.Says = Failing("honest");
		var approved = (await world.ProposeAsync()).Campaign;
		var edited = approved with
		{
			Status = CampaignStatus.Approved,
			Approval = new Approval("owner:mpdreamz", "the-digest-before-an-edit", SchedulerWorld.Start)
		};
		world.Scheduler.Repository.Campaigns.Add(edited);
		world.Reviews.Open.Clear();
		world.Scheduler.Service.RequestSync();
		await world.Scheduler.TickAsync();

		await world.Judging.CheckAsync(CancellationToken.None);

		world.Scheduler.Service.Current.Campaigns.Single(c => c.Slug == edited.Slug).NeedsApproval.Should().BeTrue();
		(await world.Campaigns.ReapproveAsync(edited.Slug, Owner, CancellationToken.None)).Should().BeOfType<Outcome<Campaign>.Refused>();
		world.Scheduler.Notifier.Sent.Should().Contain(n => n.Title == "The judge blocks Mermaider 2.0");
	}

	[Test]
	public async Task Criteria_that_cannot_be_read_stop_approval_and_are_told_once()
	{
		var world = new World();
		_ = await world.ProposeAsync();
		await world.Judging.CheckAsync(CancellationToken.None);
		world.Scheduler.Repository.CriteriaProblem = "judges/tone.md: on_failure is block or warn, not \"maybe\".";

		await world.Judging.CheckAsync(CancellationToken.None);
		await world.Judging.CheckAsync(CancellationToken.None);
		var outcome = await world.Campaigns.ApproveAsync(1, Owner, CancellationToken.None);

		world.Scheduler.Notifier.Sent.Should().ContainSingle(n => n.Title == "The judge's criteria cannot be read" && n.Message.Contains("maybe"));
		outcome.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Single().Should().Contain("judges/tone.md");
	}
}
