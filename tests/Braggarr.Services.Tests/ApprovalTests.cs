using AwesomeAssertions;
using Braggarr.Domain;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

/// <summary>An approval is of what a campaign says: stamped when given, undone by an edit, and given again by the owner or the judge.</summary>
public class ApprovalTests
{
	private static readonly Actor Owner = Actor.Owner("mpdreamz");

	private static (CampaignService Service, FakeReviews Reviews, SchedulerWorld World, ManualTimeProvider Time) Create()
	{
		var time = new ManualTimeProvider(SchedulerWorld.Start);
		var world = new SchedulerWorld(time);
		var reviews = new FakeReviews();
		var service = new CampaignService(reviews, world.Repository, world.Service, DraftingWorld.Service(null, world.Repository), world.Notifier,
			new OwnerIn(), world.Time, world.Videos, new FakeJudges(null), new FakeAccounts());
		return (service, reviews, world, time);
	}

	private static CampaignDraft Draft() =>
		new(null, "Mermaider 2.0", "mermaider", [Network.Bluesky], ["https://github.com/nullean/mermaider"], "Announce 2.0.",
			[new PostDraft(null, SchedulerWorld.Start.AddHours(1), new Dictionary<Network, string> { [Network.Bluesky] = "Mermaider 2.0." })]);

	[Test]
	public async Task Approving_stamps_what_is_approved_on_the_proposal_before_the_merge()
	{
		var (service, reviews, _, time) = Create();
		_ = await service.ProposeAsync(Draft(), Actor.Agent("release-bot"), CancellationToken.None);

		var outcome = await service.ApproveAsync(1, Owner, CancellationToken.None);

		var merged = reviews.Merged.Single().Proposal.Campaign;
		merged.Approval.Should().Be(new Approval("owner:mpdreamz", merged.Digest, time.GetUtcNow()));
		outcome.Should().BeOfType<Outcome<Proposal>.Done>();
	}

	[Test]
	public async Task A_campaign_merged_on_github_is_stamped_as_the_owners_at_first_sight()
	{
		var (_, _, world, _) = Create();
		var unstamped = world.Repository.Campaigns[0] with { Approval = null };
		world.Repository.Campaigns[0] = unstamped;

		await world.RestartAsync();

		world.Repository.Stamps.Should().ContainSingle().Which.Should().Be((unstamped.Slug, new Approval("owner:github", unstamped.Digest, SchedulerWorld.Start)));
		world.Service.Current.Campaign(unstamped.Slug)!.IsApproved.Should().BeTrue();
	}

	[Test]
	public async Task An_edit_after_approval_stops_what_has_not_gone_out_until_it_is_approved_again()
	{
		var (service, _, world, time) = Create();
		var curb = world.Repository.Campaigns[0];
		world.Repository.Campaigns[0] = curb with
		{
			Posts = curb.Posts.Replace(p => p.Id == "02-follow-up", curb.Posts[1] with { Variants = curb.Posts[1].Variants.Replace(v => v.Network == Network.Bluesky, new Variant(Network.Bluesky, "A day in, edited.")) })
		};
		world.Service.RequestSync();
		time.Advance(TimeSpan.FromHours(25));

		await world.TickAsync();
		var whileEdited = world.Bluesky.Posted.Select(v => v.Text).ToList();
		var agent = await service.ReapproveAsync(curb.Slug, Actor.Agent("release-bot"), CancellationToken.None);
		var owner = await service.ReapproveAsync(curb.Slug, Owner, CancellationToken.None);
		await world.TickAsync();
		await world.TickAsync();

		whileEdited.Should().NotContain("A day in, edited.").And.NotContain(t => t.StartsWith("curb-1-0", StringComparison.Ordinal), "nothing of the edited campaign goes out");
		agent.Should().BeOfType<Outcome<Campaign>.Refused>().Which.Reasons.Single().Should().StartWith("Only the owner approves, or the judge");
		owner.Should().BeOfType<Outcome<Campaign>.Done>().Which.Value.Approval!.By.Should().Be("owner:mpdreamz");
		world.Bluesky.Posted.Select(v => v.Text).Should().Contain("A day in, edited.");
	}

	[Test]
	public async Task Approving_again_what_did_not_change_is_refused_with_why()
	{
		var (service, _, world, _) = Create();

		var outcome = await service.ReapproveAsync(world.Repository.Campaigns[0].Slug, Owner, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Campaign>.Refused>().Which.Reasons.Single().Should().Contain("nothing changed since");
	}
}
