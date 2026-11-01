using AwesomeAssertions;
using Braggarr.Domain;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

/// <summary>
/// Which of the owner's accounts a campaign posts as: chosen when it is proposed and written into it, so it is approved with
/// the rest; changed by the owner on the proposal; and the one the scheduler posts as.
/// </summary>
public class AccountTests
{
	private static readonly Actor Owner = Actor.Owner("mpdreamz");
	private static readonly Actor Agent = Actor.Agent("release-bot");

	private static (CampaignService Service, FakeReviews Reviews, SchedulerWorld World) Create()
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));
		world.Accounts.Accounts[Network.Bluesky] = ["me", "nullean"];
		world.Accounts.Accounts[Network.Mastodon] = ["me"];
		world.Accounts.Accounts[Network.X] = [];
		var reviews = new FakeReviews();
		var service = new CampaignService(reviews, world.Repository, world.Service, DraftingWorld.Service(null, world.Repository), world.Notifier,
			new OwnerIn(), world.Time, world.Videos, new FakeJudges(null), world.Accounts);
		return (service, reviews, world);
	}

	private static CampaignDraft Draft(params Network[] networks) =>
		new(null, "Curb 2.0", "curb", networks, ["https://github.com/nullean/curb"], "Announce 1.0.",
			[new PostDraft(null, SchedulerWorld.Start.AddDays(1), networks.ToDictionary(n => n, n => n == Network.HackerNews ? "Curb 1.0\nhttps://github.com/nullean/curb" : "Curb 1.0 is out."))]);

	[Test]
	public async Task Proposing_writes_each_networks_account_into_the_campaign_from_the_defaults_and_none_for_manual_or_unconnected_networks()
	{
		var (service, reviews, world) = Create();
		world.Accounts.ProjectDefaults[(Network.Bluesky, "curb")] = "nullean";
		// Even with something under Hacker News's name, a manual network never posts as an account.
		world.Accounts.Accounts[Network.HackerNews] = ["me"];

		_ = await service.ProposeAsync(Draft(Network.Bluesky, Network.Mastodon, Network.X, Network.HackerNews), Agent, CancellationToken.None);

		reviews.Open.Single().Campaign.Accounts.Should().Equal(new CampaignAccount(Network.Bluesky, "nullean"), new CampaignAccount(Network.Mastodon, "me"));
	}

	[Test]
	public async Task An_account_asked_for_is_used_and_one_that_is_not_connected_is_refused_with_the_ones_that_are()
	{
		var (service, reviews, _) = Create();

		var asked = await service.ProposeAsync(Draft(Network.Bluesky) with { Accounts = new Dictionary<Network, string> { [Network.Bluesky] = "nullean" } }, Agent, CancellationToken.None);
		var unknown = await service.ProposeAsync(
			Draft(Network.Bluesky) with { Title = "Curb 2.1", Accounts = new Dictionary<Network, string> { [Network.Bluesky] = "someone-else" } }, Agent, CancellationToken.None);

		asked.Should().BeOfType<Outcome<Proposal>.Done>().Which.Value.Campaign.AccountFor(Network.Bluesky).Should().Be("nullean");
		unknown.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Should().ContainSingle(r => r.Contains("someone-else") && r.Contains("me, nullean"));
		reviews.Open.Should().ContainSingle();
	}

	[Test]
	public async Task A_change_through_the_api_keeps_the_accounts_and_gives_a_new_network_its_default()
	{
		var (service, reviews, _) = Create();
		_ = await service.ProposeAsync(Draft(Network.Bluesky) with { Accounts = new Dictionary<Network, string> { [Network.Bluesky] = "nullean" } }, Agent, CancellationToken.None);
		var slug = reviews.Open.Single().Campaign.Slug;

		_ = await service.UpdateProposalAsync(slug, Draft(Network.Bluesky, Network.Mastodon), Agent, CancellationToken.None);

		reviews.Open.Single().Campaign.Accounts.Should().Equal(new CampaignAccount(Network.Bluesky, "nullean"), new CampaignAccount(Network.Mastodon, "me"));
	}

	[Test]
	public async Task The_owner_changes_the_account_on_a_proposal_and_an_agent_cannot()
	{
		var (service, reviews, world) = Create();
		world.Accounts.Accounts[Network.X] = ["me"];
		_ = await service.ProposeAsync(Draft(Network.Bluesky, Network.Mastodon), Agent, CancellationToken.None);
		var before = reviews.Open.Single().Campaign;

		var byAgent = await service.ChooseAccountsAsync(1, new Dictionary<Network, string> { [Network.Bluesky] = "nullean" }, Agent, CancellationToken.None);
		var unknown = await service.ChooseAccountsAsync(1, new Dictionary<Network, string> { [Network.Bluesky] = "someone-else" }, Owner, CancellationToken.None);
		var notItsNetwork = await service.ChooseAccountsAsync(1, new Dictionary<Network, string> { [Network.X] = "me" }, Owner, CancellationToken.None);
		var byOwner = await service.ChooseAccountsAsync(1, new Dictionary<Network, string> { [Network.Bluesky] = "nullean" }, Owner, CancellationToken.None);

		byAgent.Should().BeOfType<Outcome<Proposal>.Refused>();
		unknown.Should().BeOfType<Outcome<Proposal>.Refused>();
		notItsNetwork.Should().BeOfType<Outcome<Proposal>.Refused>();
		byOwner.Should().BeOfType<Outcome<Proposal>.Done>();
		var after = reviews.Open.Single().Campaign;
		after.Accounts.Should().Equal(new CampaignAccount(Network.Bluesky, "nullean"), new CampaignAccount(Network.Mastodon, "me"));
		after.Digest.Should().NotBe(before.Digest, "which account posts is part of what is approved");
	}

	[Test]
	public async Task The_scheduler_posts_as_the_campaigns_account_and_a_campaign_from_before_accounts_as_the_default()
	{
		var (_, _, world) = Create();
		world.Repository.Campaigns.Clear();
		var named = world.Campaign("curb-1-0", hoursFromNow: 0) with { Status = CampaignStatus.Approved, Accounts = [new CampaignAccount(Network.Bluesky, "nullean")] };
		var older = world.Campaign("heed-0-20", hoursFromNow: 0) with { Status = CampaignStatus.Approved };
		world.Repository.Campaigns.AddRange(SchedulerWorld.Approved(named with { Approval = new Approval("owner:mpdreamz", "", SchedulerWorld.Start) }),
			SchedulerWorld.Approved(older with { Approval = new Approval("owner:mpdreamz", "", SchedulerWorld.Start) }));
		world.Service.RequestSync();

		await world.TickAsync();

		world.BlueskyNullean.Posted.Select(v => v.Text).Should().Equal("curb-1-0 is out.");
		world.Bluesky.Posted.Select(v => v.Text).Should().Equal("heed-0-20 is out.");
	}

	[Test]
	public async Task A_post_for_an_account_that_is_no_longer_connected_waits()
	{
		var (_, _, world) = Create();
		world.Repository.Campaigns.Clear();
		var gone = world.Campaign("curb-1-0", hoursFromNow: 0) with { Status = CampaignStatus.Approved, Accounts = [new CampaignAccount(Network.Bluesky, "gone")] };
		world.Repository.Campaigns.Add(SchedulerWorld.Approved(gone with { Approval = new Approval("owner:mpdreamz", "", SchedulerWorld.Start) }));
		world.Service.RequestSync();

		await world.TickAsync();

		world.Bluesky.Posted.Should().BeEmpty();
		world.BlueskyNullean.Posted.Should().BeEmpty();
		world.Service.Current.Find(new DeliveryKey("curb-1-0", "01-launch", Network.Bluesky))!.State.Should().Be(DeliveryState.Pending);
		world.Mastodon.Posted.Should().ContainSingle("the other networks go out as usual");
	}
}
