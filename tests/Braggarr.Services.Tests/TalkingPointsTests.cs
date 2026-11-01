using AwesomeAssertions;
using Braggarr.Domain;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

/// <summary>Proposals made from talking points: laid out by network shape, drafted to each post's points, with slots for the media they want.</summary>
public class TalkingPointsTests
{
	private static readonly Actor Agent = Actor.Agent("release-bot");
	private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

	// 15:00 UTC on Wednesday 28 October 2026: ten the next morning in Amsterdam is 09:00 UTC on the 29th.
	private static readonly DateTimeOffset Now = new(2026, 10, 28, 15, 0, 0, TimeSpan.Zero);

	private static (CampaignService Service, FakeReviews Reviews, FakeDrafter Drafter) Create(bool withModel = true)
	{
		var world = new SchedulerWorld(new ManualTimeProvider(Now));
		var reviews = new FakeReviews();
		var drafter = new FakeDrafter();
		var drafting = DraftingWorld.Service(withModel ? drafter : null, world.Repository);
		return (new CampaignService(reviews, world.Repository, world.Service, drafting, world.Notifier, new OwnerIn(), world.Time, world.Videos, new FakeJudges(null), new FakeAccounts()), reviews, drafter);
	}

	private static CampaignDraft Draft(params TalkingPoint[] points) =>
		new(null, "Mermaider 3.0", "mermaider", [Network.Bluesky, Network.Mastodon, Network.LinkedIn, Network.HackerNews], ["https://github.com/nullean/curb"], "Curb formats C#.", [])
		{
			Points = points
		};

	[Test]
	public async Task Points_without_posts_are_laid_out_from_ten_the_next_morning_and_drafted_to_each_posts_points()
	{
		var (service, _, drafter) = Create();

		var outcome = await service.ProposeAsync(
			Draft(new TalkingPoint("Keeps diffs small", [new WantedMedia(MediaKind.Video, "A run where one line changes")]), new TalkingPoint("Fast")),
			Agent,
			CancellationToken.None
		);

		var campaign = outcome.Should().BeOfType<Outcome<Proposal>.Done>().Which.Value.Campaign;
		campaign.Posts.Select(p => (p.Id, string.Join(",", p.NetworksIn(campaign)), string.Join(",", p.Points))).Should().Equal(
			("00-overview", "LinkedIn", "0,1"),
			("01-keeps-diffs-small", "Bluesky,Mastodon,HackerNews", "0"),
			("02-fast", "Bluesky,Mastodon", "1"));
		campaign.Posts[1].At.Should().Be(new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.FromHours(1)));
		campaign.Posts[2].At.Should().Be(new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.FromHours(1)));
		campaign.IsComplete.Should().BeTrue();
		campaign.IsReady.Should().BeTrue();
		drafter.PointsAsked.Should().BeEmpty("the draft brought its own points");
		drafter.Requests.Single().Targets.Should().HaveCount(6, "one per post and network it goes to, not every network for every post");
		drafter.Requests.Single().Points.Should().HaveCount(2);

		var wanted = new MediaRef("wanted:video", "A run where one line changes");
		campaign.Posts[0].For(Network.LinkedIn)!.Media.Should().Equal(wanted);
		campaign.Posts[1].For(Network.Bluesky)!.Media.Should().Equal(wanted);
		campaign.Posts[1].For(Network.HackerNews)!.Media.Should().BeEmpty("Hacker News takes no media");
		campaign.Posts[2].For(Network.Bluesky)!.Media.Should().BeEmpty();
	}

	[Test]
	public async Task A_brief_alone_is_turned_into_points_by_the_model()
	{
		var (service, _, drafter) = Create();
		drafter.Points = [new TalkingPoint("Keeps diffs small"), new TalkingPoint("Fast"), new TalkingPoint("Free")];

		var outcome = await service.ProposeAsync(Draft() with { Networks = [Network.Bluesky] }, Agent, CancellationToken.None);

		var campaign = outcome.Should().BeOfType<Outcome<Proposal>.Done>().Which.Value.Campaign;
		drafter.PointsAsked.Single().Brief.Should().Be("Curb formats C#.");
		campaign.Points.Select(p => p.Text).Should().Equal("Keeps diffs small", "Fast", "Free");
		campaign.Posts.Select(p => p.Id).Should().Equal("01-keeps-diffs-small", "02-fast", "03-free");
	}

	[Test]
	public async Task Without_a_model_the_title_is_the_one_point()
	{
		var (service, _, _) = Create(withModel: false);

		var outcome = await service.ProposeAsync(Draft() with { Networks = [Network.Bluesky, Network.LinkedIn] }, Agent, CancellationToken.None);

		var campaign = outcome.Should().BeOfType<Outcome<Proposal>.Done>().Which.Value.Campaign;
		campaign.Points.Should().Equal(new TalkingPoint("Mermaider 3.0"));
		campaign.Posts.Select(p => p.Id).Should().Equal("00-overview", "01-mermaider-3-0");
		campaign.IsComplete.Should().BeFalse("there is no model to write the text");
	}

	[Test]
	public async Task An_uploaded_image_fills_the_wanted_slot_instead_of_adding_to_it()
	{
		var (service, _, _) = Create();
		_ = await service.ProposeAsync(
			Draft(new TalkingPoint("Keeps diffs small", [new WantedMedia(MediaKind.Image, "A diff"), new WantedMedia(MediaKind.Image, "The CLI")])),
			Agent,
			CancellationToken.None
		);

		var outcome = await service.AddMediaAsync("mermaider-3-0", "diff.png", Png, "01-keeps-diffs-small", null, "A diff where one line changes", Agent, CancellationToken.None);

		var post = outcome.Should().BeOfType<Outcome<Proposal>.Done>().Which.Value.Campaign.Posts.Single(p => p.Id == "01-keeps-diffs-small");
		post.For(Network.Bluesky)!.Media.Should().Equal(new MediaRef("media/diff.png", "A diff where one line changes"), new MediaRef("wanted:image", "The CLI"));
		post.For(Network.LinkedIn).Should().BeNull("the overview post is a different post");
	}

	[Test]
	public async Task Approving_an_uncovered_campaign_names_the_point_and_network_missing()
	{
		var (service, reviews, _) = Create();
		_ = await service.ProposeAsync(Draft(new TalkingPoint("Keeps diffs small"), new TalkingPoint("Fast")) with { Networks = [Network.Bluesky] }, Agent, CancellationToken.None);
		var proposal = reviews.Open.Single();
		reviews.Open[0] = proposal with { Campaign = proposal.Campaign with { Posts = [proposal.Campaign.Posts[0]] } };

		var outcome = await service.ApproveAsync(proposal.Number, Actor.Owner("mpdreamz"), CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Should().Contain("Point 2 (\"Fast\") has no Bluesky post.");
		reviews.Merged.Should().BeEmpty();
	}

	[Test]
	public async Task A_wanted_slot_still_empty_when_the_post_is_due_goes_out_without_it()
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));
		var launch = world.Repository.Campaigns[0];
		world.Repository.Campaigns[0] = SchedulerWorld.Approved(launch with
		{
			Posts = launch.Posts.Replace(p => p.Id == "01-launch", launch.Posts[0] with
			{
				Variants = launch.Posts[0].Variants.Replace(v => v.Network == Network.Bluesky, launch.Posts[0].Variants[0] with { Media = [new MediaRef("wanted:image", "A diff")] })
			})
		});
		await world.RestartAsync();

		await world.TickAsync();

		world.Bluesky.Requests.Single().Media.Should().BeEmpty();
		world.Bluesky.Posted.Single().Text.Should().Be("curb-1-0 is out.");
	}

	[Test]
	[Arguments("1.0.0", true)]
	[Arguments("v2.0", true)]
	[Arguments("3", true)]
	[Arguments("v4.0.0-rc.1", true)]
	[Arguments("0.4.0", false)]
	[Arguments("v0.0.0", false)]
	[Arguments("1.2.0", false)]
	[Arguments("2.0.1", false)]
	[Arguments("nightly", false)]
	public void A_major_version_is_a_first_or_whole_number(string tag, bool major) => ReleaseWatcher.IsMajor(tag).Should().Be(major);
}
