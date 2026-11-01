using AwesomeAssertions;
using Braggarr.Domain;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

/// <summary>A rebalance as the owner applies it: on a proposal's branch, on main, and what that does to an approval.</summary>
public class RebalanceServiceTests
{
	private static readonly Actor Owner = Actor.Owner("mpdreamz");

	// Monday 2 November 2026, nine in the morning in Amsterdam: a weekday, so Saturday and Sunday are the rest days ahead.
	private static readonly DateTimeOffset Monday = SchedulerWorld.Start;

	private static (CampaignService Service, FakeReviews Reviews, SchedulerWorld World) Create(FakeJudges? judges = null)
	{
		var time = new ManualTimeProvider(Monday.AddHours(-1));
		var world = new SchedulerWorld(time);
		world.Repository.Campaigns.Clear();
		var reviews = new FakeReviews();
		var service = new CampaignService(reviews, world.Repository, world.Service, DraftingWorld.Service(null, world.Repository), world.Notifier,
			new OwnerIn(), world.Time, world.Videos, judges ?? new FakeJudges(null), world.Accounts);
		return (service, reviews, world);
	}

	private static Campaign Approved(string slug, params DateTimeOffset[] times)
	{
		var campaign = new Campaign(slug, slug, slug, CampaignStatus.Approved, [Network.Bluesky], ["https://github.com/nullean/curb"], "", "owner:mpdreamz",
			[.. times.Select((t, i) => new Post($"0{i + 1}-post", t, [new Variant(Network.Bluesky, $"{slug} {i + 1}.")]))]);
		return campaign with { Approval = new Approval("owner:mpdreamz", campaign.Digest, Monday.AddDays(-1)) };
	}

	// Reads the repository without delivering anything, so a post that is overdue stays undelivered.
	private static Task SyncAsync(SchedulerWorld world) => world.Service.RefreshAsync(CancellationToken.None);

	[Test]
	public async Task An_approved_campaign_moved_off_a_rest_day_is_approved_again_by_the_owner_for_its_new_time()
	{
		var (service, _, world) = Create();
		// Its second post falls on Saturday.
		world.Repository.Campaigns.Add(Approved("curb-1-0", Monday.AddHours(1), Monday.AddDays(5).AddHours(1)));
		await SyncAsync(world);

		var plan = await service.PlanRebalanceAsync(RebalanceOptions.Default, CancellationToken.None);
		var outcome = await service.RebalanceAsync(RebalanceOptions.Default, Owner, CancellationToken.None);

		plan.Items.Should().ContainSingle().Which.Should().Match<RebalanceItem>(i => i.StaysApproved && !i.InReview && i.Move.Post == "02-post");
		outcome.Should().BeOfType<Outcome<RebalanceResult>.Done>().Which.Value.ApprovedAgain.Should().Equal("curb-1-0");
		var after = world.Repository.Campaigns.Single();
		after.Posts[1].At.Should().Be(Monday.AddDays(7), "the next Monday at nine");
		after.IsApproved.Should().BeTrue();
		after.Approval!.By.Should().Be(Owner.ToString());
	}

	[Test]
	public async Task With_the_judge_set_up_a_moved_approved_campaign_waits_for_it()
	{
		var (service, _, world) = Create(new FakeJudges(new FakeJudge()));
		world.Repository.Criteria.Add(new Criterion("honest", "Claims nothing the links do not show.", Blocks: true));
		world.Repository.Campaigns.Add(Approved("curb-1-0", Monday.AddDays(5).AddHours(1)));
		await SyncAsync(world);

		var plan = await service.PlanRebalanceAsync(RebalanceOptions.Default, CancellationToken.None);
		var outcome = await service.RebalanceAsync(RebalanceOptions.Default, Owner, CancellationToken.None);

		plan.Items.Single().StaysApproved.Should().BeFalse();
		outcome.Should().BeOfType<Outcome<RebalanceResult>.Done>().Which.Value.NeedApproving.Should().Equal("curb-1-0");
		world.Repository.Campaigns.Single().NeedsApproval.Should().BeTrue();
	}

	[Test]
	public async Task A_proposal_is_moved_on_its_branch_and_two_campaigns_on_one_day_are_spread()
	{
		var (service, reviews, world) = Create();
		world.Repository.Campaigns.Add(Approved("curb-1-0", Monday.AddHours(1)));
		await SyncAsync(world);
		_ = await service.ProposeAsync(new CampaignDraft(null, "Heed 1.0", "heed", [Network.Bluesky], ["https://heed.build"], "Announce.",
			[new PostDraft("01-post", Monday.AddHours(3), new Dictionary<Network, string> { [Network.Bluesky] = "Heed 1.0." })]), Actor.Agent("release-bot"), CancellationToken.None);

		var plan = await service.PlanRebalanceAsync(RebalanceOptions.Default, CancellationToken.None);
		_ = await service.RebalanceAsync(RebalanceOptions.Default, Owner, CancellationToken.None);

		plan.Items.Should().ContainSingle().Which.InReview.Should().BeTrue();
		reviews.Open.Single().Campaign.Posts.Single().At.Should().Be(Monday.AddDays(1), "one Bluesky post a day: Tuesday at nine");
		world.Repository.Campaigns.Single().Posts.Single().At.Should().Be(Monday.AddHours(1), "the approved one already kept the rules");
	}

	[Test]
	public async Task A_post_that_went_out_stays_and_only_the_owner_rebalances()
	{
		var (service, _, world) = Create();
		world.Repository.Campaigns.Add(Approved("curb-1-0", Monday.AddDays(-2), Monday.AddDays(-1).AddHours(1)));
		world.Repository.Deliveries[new DeliveryKey("curb-1-0", "01-post", Network.Bluesky)] =
			Delivery.New(new DeliveryKey("curb-1-0", "01-post", Network.Bluesky)).Begin(Monday.AddDays(-2)).Succeed(new Receipt("1", "https://bsky.app/1"));
		await SyncAsync(world);

		var plan = await service.PlanRebalanceAsync(RebalanceOptions.Default, CancellationToken.None);
		var byAgent = await service.RebalanceAsync(RebalanceOptions.Default, Actor.Agent("release-bot"), CancellationToken.None);

		plan.Items.Select(i => i.Move.Post).Should().Equal(["02-post"], "only the overdue post that has not gone out moves");
		byAgent.Should().BeOfType<Outcome<RebalanceResult>.Refused>();
	}
}
