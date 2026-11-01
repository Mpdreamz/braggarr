using AwesomeAssertions;
using Braggarr.Domain;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

public class DraftingServiceTests
{
	private static readonly DateTimeOffset At = new(2026, 11, 3, 10, 0, 0, TimeSpan.FromHours(1));

	private static CampaignDraft Draft(params PostDraft[] posts) =>
		new(null, "Mermaider 2.0", "mermaider", [Network.Bluesky, Network.HackerNews], ["https://github.com/nullean/mermaider"], "Announce 2.0.", posts);

	[Test]
	public async Task Missing_variants_are_written_and_written_ones_are_kept()
	{
		var drafter = new FakeDrafter();
		var repository = new FakeRepository { Voice = "Dry, short." };
		var usage = new FakeUsage();
		var service = DraftingWorld.Service(drafter, repository, usage);

		var (filled, problems) = await service.FillAsync(
			Draft(new PostDraft("01-launch", At, new Dictionary<Network, string> { [Network.Bluesky] = "My own words." })),
			null,
			CancellationToken.None
		);

		problems.Should().BeEmpty();
		filled.Posts.Single().Variants.Should().Contain(Network.Bluesky, "My own words.").And.ContainKey(Network.HackerNews);
		var request = drafter.Requests.Single();
		request.Targets.Should().Equal(new DraftTarget("01-launch", Network.HackerNews));
		request.Voice.Should().Be("Dry, short.");
		request.Posts.Single().Variants.Should().ContainSingle(v => v.Text == "My own words.");
		usage.Calls.Should().ContainSingle();
	}

	[Test]
	public async Task A_variant_that_is_too_long_is_asked_for_again_with_its_length_and_the_limit()
	{
		var drafter = new FakeDrafter();
		drafter.Script[("01-post", Network.HackerNews)] = new([new string('x', 95), "Show HN: Mermaider 2.0, diagrams to SVG without a browser"]);
		var service = DraftingWorld.Service(drafter, new FakeRepository());

		var (filled, problems) = await service.FillAsync(Draft(new PostDraft("01-post", At, new Dictionary<Network, string>())), null, CancellationToken.None);

		problems.Should().BeEmpty();
		filled.Posts.Single().Variants[Network.HackerNews].Should().StartWith("Show HN:");
		drafter.Requests.Should().HaveCount(2);
		var retry = drafter.Requests[1].Targets.Single();
		retry.Network.Should().Be(Network.HackerNews);
		retry.Problem.Should().Contain("95 long").And.Contain("the limit is 80");
		retry.Previous.Should().HaveLength(95);
	}

	[Test]
	public async Task A_blog_article_without_a_title_is_asked_for_again_and_kept_with_its_fields()
	{
		var drafter = new FakeDrafter();
		drafter.Articles.Enqueue(new Article("", "What is new.", ["release"]));
		drafter.Articles.Enqueue(new Article("Mermaider 2.0, without a browser", "Diagrams to SVG, in CI.", ["dotnet", "diagrams"]));
		var service = DraftingWorld.Service(drafter, new FakeRepository());

		var (filled, problems) = await service.FillAsync(
			Draft(new PostDraft("00-blog", At, new Dictionary<Network, string>()) { Networks = [Network.Blog] }) with { Networks = [Network.Blog] },
			null,
			CancellationToken.None
		);

		problems.Should().BeEmpty();
		var post = filled.Posts.Single();
		post.Variants[Network.Blog].Should().Be("Mermaider 2.0 on Blog.");
		post.Article.Should().BeEquivalentTo(new Article("Mermaider 2.0, without a browser", "Diagrams to SVG, in CI.", ["dotnet", "diagrams"]));
		drafter.Requests.Should().HaveCount(2);
		drafter.Requests[1].Targets.Single().Problem.Should().Contain("a title of 1 to 100 characters");
	}

	[Test]
	public async Task A_variant_that_never_fits_is_left_out_and_said_so()
	{
		var drafter = new FakeDrafter();
		drafter.Script[("01-post", Network.HackerNews)] = new(Enumerable.Repeat(new string('x', 120), 5));
		var service = DraftingWorld.Service(drafter, new FakeRepository());

		var (filled, problems) = await service.FillAsync(Draft(new PostDraft("01-post", At, new Dictionary<Network, string>())), null, CancellationToken.None);

		drafter.Requests.Should().HaveCount(DraftingService.Attempts);
		filled.Posts.Single().Variants.Should().NotContainKey(Network.HackerNews).And.ContainKey(Network.Bluesky);
		problems.Should().ContainSingle().Which.Should().Contain("no HackerNews variant that fits");
	}

	[Test]
	public async Task Without_a_model_nothing_is_drafted()
	{
		var service = DraftingWorld.Service(null, new FakeRepository());
		var draft = Draft(new PostDraft("01-post", At, new Dictionary<Network, string>()));

		var (filled, problems) = await service.FillAsync(draft, null, CancellationToken.None);

		filled.Should().Be(draft);
		problems.Should().BeEmpty();
		service.IsAvailable.Should().BeFalse();
	}

	[Test]
	public async Task A_redraft_replaces_one_variant_and_passes_the_note_and_the_old_text()
	{
		var drafter = new FakeDrafter();
		drafter.Script[("01-launch", Network.Bluesky)] = new(["Shorter, with the SVG detail."]);
		var service = DraftingWorld.Service(drafter, new FakeRepository());
		var campaign = new Campaign(
			"mermaider-2-0", "Mermaider 2.0", "mermaider", CampaignStatus.Proposed, [Network.Bluesky, Network.Mastodon], [], "", "agent:bot",
			[new Post("01-launch", At, [new Variant(Network.Bluesky, "Old bluesky."), new Variant(Network.Mastodon, "Old mastodon.")])]
		);

		var outcome = await service.RedraftAsync(campaign, "01-launch", Network.Bluesky, "Mention SVG.", CancellationToken.None);

		var redrafted = outcome.Should().BeOfType<Outcome<Campaign>.Done>().Which.Value;
		redrafted.Posts.Single().For(Network.Bluesky)!.Text.Should().Be("Shorter, with the SVG detail.");
		redrafted.Posts.Single().For(Network.Mastodon)!.Text.Should().Be("Old mastodon.");
		var request = drafter.Requests.Single();
		request.Note.Should().Be("Mention SVG.");
		request.Targets.Single().Previous.Should().Be("Old bluesky.");
	}

	[Test]
	public async Task An_agent_that_sends_only_times_gets_a_complete_proposal()
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));
		var reviews = new FakeReviews();
		var campaigns = new CampaignService(reviews, world.Repository, world.Service, DraftingWorld.Service(new FakeDrafter(), world.Repository), world.Notifier, new OwnerIn(), world.Time, world.Videos, new FakeJudges(null), new FakeAccounts());

		var outcome = await campaigns.ProposeAsync(Draft(new PostDraft(null, At, new Dictionary<Network, string>())), Actor.Agent("release-bot"), CancellationToken.None);

		var proposal = outcome.Should().BeOfType<Outcome<Proposal>.Done>().Which.Value;
		proposal.Campaign.IsComplete.Should().BeTrue();
		proposal.Campaign.Status.Should().Be(CampaignStatus.Proposed);
		proposal.Campaign.Posts.Single().Id.Should().Be("01-post");
	}
}
