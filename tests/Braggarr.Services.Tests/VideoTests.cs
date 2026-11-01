using AwesomeAssertions;
using Braggarr.Domain;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

/// <summary>Videos: uploaded into a proposal's wanted slot, kept on the server, read back when the post is due.</summary>
public class VideoTests
{
	private static readonly Actor Agent = Actor.Agent("release-bot");

	private static async Task<(CampaignService Service, SchedulerWorld World, FakeReviews Reviews)> ProposedAsync(params Network[] networks)
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));
		var reviews = new FakeReviews();
		var service = new CampaignService(reviews, world.Repository, world.Service, DraftingWorld.Service(new FakeDrafter(), world.Repository), world.Notifier,
			new OwnerIn(), world.Time, world.Videos, new FakeJudges(null), new FakeAccounts());
		_ = await service.ProposeAsync(
			new CampaignDraft(null, "Mermaider 3.0", "mermaider", networks.Length > 0 ? networks : [Network.Bluesky, Network.LinkedIn, Network.HackerNews], [], "Diagrams.", [])
			{
				Points = [new TalkingPoint("Diagrams without a browser", [new WantedMedia(MediaKind.Video, "A terminal run turning a flowchart into an SVG")])]
			},
			Agent,
			CancellationToken.None
		);
		return (service, world, reviews);
	}

	[Test]
	public async Task A_video_fills_the_wanted_slot_takes_its_description_and_is_kept_on_the_server()
	{
		var (service, world, _) = await ProposedAsync();
		var mp4 = Mp4Fixture.Make(1080, 1920, 42);

		var outcome = await service.AddMediaAsync("mermaider-3-0", "run.mp4", mp4, "01-diagrams-without-a-browser", null, null, Agent, CancellationToken.None);

		var campaign = outcome.Should().BeOfType<Outcome<Proposal>.Done>().Which.Value.Campaign;
		var post = campaign.Posts.Single(p => p.Id == "01-diagrams-without-a-browser");
		var video = post.For(Network.Bluesky)!.Media.Should().ContainSingle().Which;
		video.IsStoredVideo.Should().BeTrue();
		video.Alt.Should().Be("A terminal run turning a flowchart into an SVG");
		world.Videos.Files[video.Path].Should().Equal(mp4);
		post.For(Network.HackerNews)!.Media.Should().BeEmpty();
		campaign.Posts.Single(p => p.Id == "00-overview").For(Network.LinkedIn)!.Media.Single().Path.Should().Be("wanted:video", "the overview is another post");
		campaign.IsReady.Should().BeTrue();
	}

	[Test]
	public async Task A_video_longer_than_a_network_takes_is_refused_by_name()
	{
		var (service, world, _) = await ProposedAsync();

		var outcome = await service.AddMediaAsync("mermaider-3-0", "run.mp4", Mp4Fixture.Make(1920, 1080, 200), "01-diagrams-without-a-browser", null, "A run", Agent, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Should().ContainSingle().Which.Should().Be("Bluesky takes videos up to 3 minutes; this one runs 3:20.");
		world.Videos.Files.Should().BeEmpty();
	}

	[Test]
	public async Task A_too_short_video_for_linkedin_and_a_file_that_is_not_an_mp4_are_refused()
	{
		var (service, _, _) = await ProposedAsync();

		var shortOne = await service.AddMediaAsync("mermaider-3-0", "run.mp4", Mp4Fixture.Make(1920, 1080, 2), "00-overview", null, "A run", Agent, CancellationToken.None);
		var notOne = await service.AddMediaAsync("mermaider-3-0", "run.mp4", [1, 2, 3, 4], "00-overview", null, "A run", Agent, CancellationToken.None);

		shortOne.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Single().Should().Be("LinkedIn takes videos of 3 seconds to 30 minutes; this one runs 0:02.");
		notOne.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Single().Should().Contain("not an MP4");
	}

	[Test]
	public async Task A_post_that_has_images_takes_no_video()
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));
		var service = new CampaignService(new FakeReviews(), world.Repository, world.Service, DraftingWorld.Service(null, world.Repository), world.Notifier,
			new OwnerIn(), world.Time, world.Videos, new FakeJudges(null), new FakeAccounts());
		_ = await service.ProposeAsync(
			new CampaignDraft(null, "Mermaider 3.0", "mermaider", [Network.Bluesky], [], "", [new PostDraft("01-launch", SchedulerWorld.Start.AddDays(1), new Dictionary<Network, string> { [Network.Bluesky] = "Out." })]),
			Agent,
			CancellationToken.None
		);
		_ = await service.AddMediaAsync("mermaider-3-0", "a.png", [0x89, 0x50, 0x4E, 0x47], "01-launch", null, "A diagram", Agent, CancellationToken.None);

		var outcome = await service.AddMediaAsync("mermaider-3-0", "run.mp4", Mp4Fixture.Make(1920, 1080, 30), "01-launch", null, "A run", Agent, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Should().Equal("01-launch on Bluesky already has an image; a post carries one video or images, not both.");
		world.Videos.Files.Should().BeEmpty();
	}

	[Test]
	public async Task A_video_without_a_wanted_slot_needs_a_description()
	{
		var (service, _, _) = await ProposedAsync(Network.Bluesky);
		_ = await service.AddMediaAsync("mermaider-3-0", "run.mp4", Mp4Fixture.Make(1920, 1080, 30), "01-diagrams-without-a-browser", null, null, Agent, CancellationToken.None);

		var again = await service.AddMediaAsync("mermaider-3-0", "other.mp4", Mp4Fixture.Make(1920, 1080, 31), "01-diagrams-without-a-browser", null, null, Agent, CancellationToken.None);

		again.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Single().Should().StartWith("Describe the video");
	}

	[Test]
	public async Task When_the_post_is_due_the_video_is_read_from_the_server_with_its_shape()
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));
		var mp4 = Mp4Fixture.Make(1080, 1920, 15, rotated: false);
		var video = await world.Videos.SaveAsync(mp4, "A run", CancellationToken.None);
		var launch = world.Repository.Campaigns[0];
		world.Repository.Campaigns[0] = SchedulerWorld.Approved(launch with
		{
			Posts = launch.Posts.Replace(p => p.Id == "01-launch", launch.Posts[0] with
			{
				Variants = launch.Posts[0].Variants.Replace(v => v.Network == Network.Bluesky, launch.Posts[0].Variants[0] with { Media = [video] })
			})
		});
		await world.RestartAsync();

		await world.TickAsync();

		var sent = world.Bluesky.Requests.Single().Media.Single();
		sent.IsVideo.Should().BeTrue();
		sent.Bytes.Should().Equal(mp4);
		sent.Video.Should().Be(new VideoInfo(1080, 1920, TimeSpan.FromSeconds(15)));
	}

	[Test]
	public async Task A_post_with_a_video_gets_minutes_to_go_out_before_it_counts_as_unknown()
	{
		var time = new ManualTimeProvider(SchedulerWorld.Start);
		var world = new SchedulerWorld(time);
		var video = await world.Videos.SaveAsync(Mp4Fixture.Make(1280, 720, 10), "A run", CancellationToken.None);
		var launch = world.Repository.Campaigns[0];
		world.Repository.Campaigns[0] = SchedulerWorld.Approved(launch with
		{
			Posts = launch.Posts.Replace(p => p.Id == "01-launch", launch.Posts[0] with
			{
				Variants = launch.Posts[0].Variants.Replace(v => v.Network == Network.Bluesky, launch.Posts[0].Variants[0] with { Media = [video] })
			})
		});
		await world.RestartAsync();
		world.Bluesky.Next.Enqueue(Answer.Hangs);
		DeliveryState Bluesky() => world.Repository.Deliveries.Values.Single(d => d.Key is { Campaign: "curb-1-0", Post: "01-launch", Network: Network.Bluesky }).State;

		var tick = world.TickAsync();
		time.Advance(TimeSpan.FromSeconds(61));
		await Task.Delay(100);
		var afterAMinute = Bluesky();
		time.Advance(TimeSpan.FromMinutes(15));
		await tick;

		afterAMinute.Should().Be(DeliveryState.Sending, "an image post would have timed out by now; a video is still uploading");
		Bluesky().Should().Be(DeliveryState.Unknown);
	}

	[Test]
	public async Task A_video_the_server_lost_fails_the_delivery_with_what_to_do()
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));
		var launch = world.Repository.Campaigns[0];
		world.Repository.Campaigns[0] = SchedulerWorld.Approved(launch with
		{
			Posts = launch.Posts.Replace(p => p.Id == "01-launch", launch.Posts[0] with
			{
				Variants = launch.Posts[0].Variants.Replace(v => v.Network == Network.Bluesky, launch.Posts[0].Variants[0] with { Media = [MediaRef.Video(new string('b', 64), "A run")] })
			})
		});
		await world.RestartAsync();

		await world.TickAsync();

		world.Bluesky.Requests.Should().BeEmpty();
		world.Repository.Deliveries.Values.Single(d => d.Key.Network == Network.Bluesky && d.Key.Post == "01-launch" && d.Key.Campaign == launch.Slug)
			.Error.Should().Contain("not on this server");
	}

	[Test]
	public async Task A_youtube_post_waits_for_its_video_and_approval_says_so()
	{
		var (service, _, reviews) = await ProposedAsync(Network.Bluesky, Network.YouTube);
		var post = reviews.Open.Single().Campaign.Posts.Single();

		post.NetworksIn(reviews.Open.Single().Campaign).Should().Equal(Network.Bluesky, Network.YouTube);
		post.For(Network.YouTube)!.Media.Should().Equal(new MediaRef("wanted:video", "A terminal run turning a flowchart into an SVG"));
		var refused = await service.ApproveAsync(1, Actor.Owner("mpdreamz"), CancellationToken.None);

		refused.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Should().Contain("01-diagrams-without-a-browser: no video for YouTube yet.");
	}

	[Test]
	public async Task Uploading_the_video_readies_youtube_and_the_other_networks_with_it()
	{
		var (service, _, _) = await ProposedAsync(Network.Bluesky, Network.YouTube);

		var outcome = await service.AddMediaAsync("mermaider-3-0", "run.mp4", Mp4Fixture.Make(1080, 1920, 40), "01-diagrams-without-a-browser", null, null, Agent, CancellationToken.None);

		var campaign = outcome.Should().BeOfType<Outcome<Proposal>.Done>().Which.Value.Campaign;
		campaign.Posts.Single().For(Network.YouTube)!.Media.Single().IsStoredVideo.Should().BeTrue();
		campaign.Posts.Single().For(Network.Bluesky)!.Media.Single().IsStoredVideo.Should().BeTrue();
		campaign.IsReady.Should().BeTrue();
	}

	[Test]
	public async Task An_image_never_lands_on_youtube()
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));
		var reviews = new FakeReviews();
		var service = new CampaignService(reviews, world.Repository, world.Service, DraftingWorld.Service(null, world.Repository), world.Notifier,
			new OwnerIn(), world.Time, world.Videos, new FakeJudges(null), new FakeAccounts());
		_ = await service.ProposeAsync(
			new CampaignDraft(null, "Mermaider 3.0", "mermaider", [Network.Bluesky, Network.YouTube], [], "", [
				new PostDraft("01-launch", SchedulerWorld.Start.AddDays(1), new Dictionary<Network, string> { [Network.Bluesky] = "Out.", [Network.YouTube] = "Mermaider 3.0\n\nOut." })
			]),
			Agent,
			CancellationToken.None
		);

		var outcome = await service.AddMediaAsync("mermaider-3-0", "card.png", [0x89, 0x50, 0x4E, 0x47], "01-launch", null, "A card", Agent, CancellationToken.None);

		var post = outcome.Should().BeOfType<Outcome<Proposal>.Done>().Which.Value.Campaign.Posts.Single();
		post.For(Network.Bluesky)!.Media.Single().Path.Should().Be("media/card.png");
		post.For(Network.YouTube)!.Media.Should().BeEmpty();
	}
}
