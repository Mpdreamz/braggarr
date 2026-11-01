using AwesomeAssertions;
using Braggarr.Domain;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

public class CampaignServiceTests
{
	private static readonly Actor Owner = Actor.Owner("mpdreamz");
	private static readonly Actor Agent = Actor.Agent("release-bot");

	private static (CampaignService Service, FakeReviews Reviews, SchedulerWorld World) Create()
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));
		var reviews = new FakeReviews();
		return (new CampaignService(reviews, world.Repository, world.Service, DraftingWorld.Service(null, world.Repository), world.Notifier, new OwnerIn(), world.Time, world.Videos, new FakeJudges(null), new FakeAccounts()), reviews, world);
	}

	private static CampaignDraft Draft(string title = "Mermaider 2.0", bool complete = true) =>
		new(
			null, title, "mermaider", [Network.Bluesky, Network.Mastodon], ["https://github.com/nullean/mermaider"], "Announce 2.0.",
			[
				new PostDraft(
					null,
					SchedulerWorld.Start,
					complete
						? new Dictionary<Network, string> { [Network.Bluesky] = "Mermaider 2.0.", [Network.Mastodon] = "Mermaider 2.0." }
						: new Dictionary<Network, string> { [Network.Bluesky] = "Mermaider 2.0." }
				)
			]
		);

	[Test]
	public async Task An_agents_proposal_opens_a_pull_request_and_starts_as_proposed()
	{
		var (service, reviews, _) = Create();

		var outcome = await service.ProposeAsync(Draft(), Agent, CancellationToken.None);

		var proposal = outcome.Should().BeOfType<Outcome<Proposal>.Done>().Which.Value;
		proposal.Campaign.Slug.Should().Be("mermaider-2-0");
		proposal.Campaign.Status.Should().Be(CampaignStatus.Proposed);
		proposal.Campaign.CreatedBy.Should().Be("agent:release-bot");
		reviews.Open.Should().ContainSingle();
	}

	[Test]
	public async Task An_agent_cannot_approve_through_the_service()
	{
		var (service, reviews, _) = Create();
		_ = await service.ProposeAsync(Draft(), Agent, CancellationToken.None);

		var outcome = await service.ApproveAsync(1, Agent, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Should().ContainSingle().Which.Should().Contain("Only the owner");
		reviews.Merged.Should().BeEmpty();
	}

	[Test]
	public async Task The_owner_approves_by_merging_but_not_an_incomplete_campaign()
	{
		var (service, reviews, _) = Create();
		_ = await service.ProposeAsync(Draft(complete: false), Agent, CancellationToken.None);

		var refused = await service.ApproveAsync(1, Owner, CancellationToken.None);
		refused.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Should().Contain(r => r.Contains("no Mastodon variant"));

		_ = await service.ProposeAsync(Draft("Mermaider 2.1"), Agent, CancellationToken.None);
		var approved = await service.ApproveAsync(2, Owner, CancellationToken.None);
		approved.Should().BeOfType<Outcome<Proposal>.Done>();
		reviews.Merged.Should().ContainSingle().Which.By.Should().Be(Owner);
	}

	[Test]
	public async Task A_slug_that_is_taken_is_refused()
	{
		var (service, _, _) = Create();

		var outcome = await service.ProposeAsync(Draft("curb 1.0"), Agent, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Should().Contain(r => r.Contains("already exists"));
	}

	[Test]
	public async Task Only_the_owner_holds_and_holding_writes_to_the_repo()
	{
		var (service, _, world) = Create();

		(await service.HoldAsync("curb-1-0", Agent, CancellationToken.None)).Should().BeOfType<Outcome<Campaign>.Refused>();
		(await service.HoldAsync("curb-1-0", Owner, CancellationToken.None)).Should().BeOfType<Outcome<Campaign>.Done>();

		world.Repository.Campaigns.Single(c => c.Slug == "curb-1-0").Status.Should().Be(CampaignStatus.Held);
	}
}
