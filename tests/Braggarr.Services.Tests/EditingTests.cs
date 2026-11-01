using AwesomeAssertions;
using Braggarr.Domain;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

public class EditingTests
{
	private static readonly Actor Owner = Actor.Owner("mpdreamz");

	private static (CampaignService Service, FakeReviews Reviews, SchedulerWorld World) Create()
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));
		var reviews = new FakeReviews();
		return (new CampaignService(reviews, world.Repository, world.Service, DraftingWorld.Service(null, world.Repository), world.Notifier, new OwnerIn(), world.Time, world.Videos, new FakeJudges(null), new FakeAccounts()), reviews, world);
	}

	[Test]
	public async Task The_owner_edits_a_proposals_text_and_time_on_its_branch()
	{
		var (service, reviews, _) = Create();
		_ = await service.ProposeAsync(
			new CampaignDraft(null, "Hunch 0.3", "hunch", [Network.Bluesky], [], "", [new PostDraft("01-launch", SchedulerWorld.Start.AddDays(2), new Dictionary<Network, string> { [Network.Bluesky] = "Old." })]),
			Actor.Agent("bot"), CancellationToken.None);

		var outcome = await service.EditPostAsync("hunch-0-3", "01-launch", SchedulerWorld.Start.AddDays(3), new Dictionary<Network, string> { [Network.Bluesky] = " New. " }, Owner, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Campaign>.Done>();
		var post = reviews.Open.Single().Campaign.Posts.Single();
		post.For(Network.Bluesky)!.Text.Should().Be("New.");
		post.At.Should().Be(SchedulerWorld.Start.AddDays(3));
	}

	[Test]
	public async Task An_edited_blog_article_keeps_its_fields_and_has_the_images_its_new_text_shows()
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));
		var reviews = new FakeReviews();
		var service = new CampaignService(reviews, world.Repository, world.Service, DraftingWorld.Service(null, world.Repository), world.Notifier, new OwnerIn(), world.Time, world.Videos,
			new FakeJudges(null), new FakeAccounts(), new FakeVisibility());
		_ = await service.ProposeAsync(
			new CampaignDraft(null, "Hunch 0.3", "hunch", [Network.Blog], [], "",
			[
				new PostDraft("00-blog", SchedulerWorld.Start.AddDays(2), new Dictionary<Network, string> { [Network.Blog] = "# Hunch\n\n![Old](media/old.png)" })
				{
					Article = new Article("Hunch 0.3", "A faster index.", ["dotnet"])
				}
			]),
			Actor.Agent("bot"), CancellationToken.None);

		var outcome = await service.EditPostAsync("hunch-0-3", "00-blog", SchedulerWorld.Start.AddDays(2),
			new Dictionary<Network, string> { [Network.Blog] = "# Hunch\n\n![The chart](media/chart.png)" }, Owner, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Campaign>.Done>();
		var blog = reviews.Open.Single().Campaign.Posts.Single().For(Network.Blog)!;
		blog.Article!.Title.Should().Be("Hunch 0.3");
		blog.Media.Should().Equal(new MediaRef("media/chart.png", "The chart"));
	}

	[Test]
	public async Task The_owner_edits_what_has_not_gone_out_of_an_approved_campaign_on_main()
	{
		var (service, _, world) = Create();

		var outcome = await service.EditPostAsync("heed-0-20", "01-launch", SchedulerWorld.Start.AddHours(2),
			new Dictionary<Network, string> { [Network.Mastodon] = "heed 0.20: review a branch from the keyboard." }, Owner, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Campaign>.Done>();
		var (slug, post) = world.Repository.PostEdits.Single();
		slug.Should().Be("heed-0-20");
		post.For(Network.Mastodon)!.Text.Should().Be("heed 0.20: review a branch from the keyboard.");
	}

	[Test]
	public async Task What_already_went_out_cannot_be_edited()
	{
		var (service, _, world) = Create();
		await world.TickAsync(); // curb-1-0's launch goes out now

		var outcome = await service.EditPostAsync("curb-1-0", "01-launch", SchedulerWorld.Start,
			new Dictionary<Network, string> { [Network.Bluesky] = "Rewritten history." }, Owner, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Campaign>.Refused>().Which.Reasons.Single().Should().Contain("Bluesky already went out");
		world.Repository.PostEdits.Should().BeEmpty();
	}

	[Test]
	public async Task An_approved_campaign_stays_within_its_limits()
	{
		var (service, _, world) = Create();

		var outcome = await service.EditPostAsync("heed-0-20", "01-launch", SchedulerWorld.Start.AddHours(2),
			new Dictionary<Network, string> { [Network.Bluesky] = new string('x', 301) }, Owner, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Campaign>.Refused>();
		world.Repository.PostEdits.Should().BeEmpty();
	}

	[Test]
	public async Task Agents_do_not_edit_here()
	{
		var (service, _, _) = Create();

		var outcome = await service.EditPostAsync("heed-0-20", "01-launch", SchedulerWorld.Start, new Dictionary<Network, string>(), Actor.Agent("bot"), CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Campaign>.Refused>();
	}
}
