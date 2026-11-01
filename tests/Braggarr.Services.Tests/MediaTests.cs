using AwesomeAssertions;
using Braggarr.Domain;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

public class MediaTests
{
	private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

	private static async Task<(CampaignService Service, FakeReviews Reviews, FakeDrafter Drafter)> ProposedAsync(bool withModel = true)
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));
		var reviews = new FakeReviews();
		var drafter = new FakeDrafter();
		var service = new CampaignService(reviews, world.Repository, world.Service, DraftingWorld.Service(withModel ? drafter : null, world.Repository), world.Notifier, new OwnerIn(), world.Time, world.Videos, new FakeJudges(null), new FakeAccounts());
		_ = await service.ProposeAsync(
			new CampaignDraft(null, "Mermaider 2.0", "mermaider", [Network.Bluesky, Network.Mastodon, Network.HackerNews], ["https://github.com/nullean/curb"], "", [
				new PostDraft("01-launch", SchedulerWorld.Start.AddDays(1), new Dictionary<Network, string>
				{
					[Network.Bluesky] = "Out.", [Network.Mastodon] = "Out.", [Network.HackerNews] = "Show HN: Curb\nhttps://github.com/nullean/curb"
				})
			]),
			Actor.Agent("release-bot"),
			CancellationToken.None
		);
		return (service, reviews, drafter);
	}

	[Test]
	public async Task An_image_without_alt_text_is_described_by_claude_and_lands_on_every_network_that_takes_images()
	{
		var (service, reviews, drafter) = await ProposedAsync();

		var outcome = await service.AddMediaAsync("mermaider-2-0", "Diff View.PNG", Png, "01-launch", null, null, Actor.Agent("release-bot"), CancellationToken.None);

		var post = outcome.Should().BeOfType<Outcome<Proposal>.Done>().Which.Value.Campaign.Posts.Single();
		var image = new MediaRef("media/diff-view.png", "A terminal showing a diff that changed one line.");
		post.For(Network.Bluesky)!.Media.Should().Equal(image);
		post.For(Network.Mastodon)!.Media.Should().Equal(image);
		post.For(Network.HackerNews)!.Media.Should().BeEmpty();
		reviews.Media[(1, "media/diff-view.png")].Should().Equal(Png);
		drafter.Described.Single().ContentType.Should().Be("image/png");
	}

	[Test]
	public async Task Given_alt_text_is_used_as_is_and_only_on_the_networks_named()
	{
		var (service, _, drafter) = await ProposedAsync();

		var outcome = await service.AddMediaAsync("mermaider-2-0", "card.jpg", Png, "01-launch", [Network.Mastodon], "Curb's logo.", Actor.Owner("mpdreamz"), CancellationToken.None);

		var post = outcome.Should().BeOfType<Outcome<Proposal>.Done>().Which.Value.Campaign.Posts.Single();
		post.For(Network.Mastodon)!.Media.Single().Alt.Should().Be("Curb's logo.");
		post.For(Network.Bluesky)!.Media.Should().BeEmpty();
		drafter.Described.Should().BeEmpty();
	}

	[Test]
	[Arguments("notes.pdf", 10, "PNG, JPEG, WebP or GIF")]
	[Arguments("huge.png", 1_000_001, "at most 1000 kB")]
	public async Task What_is_not_an_image_bluesky_can_take_is_refused(string name, int size, string reason)
	{
		var (service, reviews, _) = await ProposedAsync();

		var outcome = await service.AddMediaAsync("mermaider-2-0", name, new byte[size], "01-launch", null, "alt", Actor.Agent("bot"), CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Should().Contain(r => r.Contains(reason));
		reviews.Media.Should().BeEmpty();
	}

	[Test]
	public async Task Without_a_model_alt_text_is_required()
	{
		var (service, _, _) = await ProposedAsync(withModel: false);

		var outcome = await service.AddMediaAsync("mermaider-2-0", "card.png", Png, "01-launch", null, null, Actor.Agent("bot"), CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Single().Should().Contain("alt text is required");
	}

	[Test]
	public async Task A_fifth_image_is_refused()
	{
		var (service, _, _) = await ProposedAsync();
		for (var i = 1; i <= 4; i++)
		{
			(await service.AddMediaAsync("mermaider-2-0", $"shot-{i}.png", Png, "01-launch", null, $"Shot {i}", Actor.Agent("bot"), CancellationToken.None))
				.Should().BeOfType<Outcome<Proposal>.Done>();
		}

		var fifth = await service.AddMediaAsync("mermaider-2-0", "shot-5.png", Png, "01-launch", null, "Shot 5", Actor.Agent("bot"), CancellationToken.None);

		fifth.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Should().Contain(r => r.Contains("more than its network takes"));
	}

	[Test]
	public async Task The_scheduler_hands_the_images_bytes_to_the_publisher_and_a_missing_one_fails_the_delivery()
	{
		var time = new ManualTimeProvider(SchedulerWorld.Start);
		var world = new SchedulerWorld(time);
		var card = new MediaRef("media/card.png", "Curb's logo.");
		world.Repository.Campaigns[0] = SchedulerWorld.Approved(world.Repository.Campaigns[0] with
		{
			Posts = world.Repository.Campaigns[0].Posts.Replace(p => p.Id == "01-launch", world.Repository.Campaigns[0].Posts[0] with
			{
				Variants = world.Repository.Campaigns[0].Posts[0].Variants.Select(v => v.Network.IsManual() ? v : v with { Media = [card] }).ToValueList()
			})
		});
		world.Repository.Media[("curb-1-0", "media/card.png")] = Png;
		world.Service.RequestSync();

		world.Repository.Media.Remove(("curb-1-0", "media/card.png"));
		await world.TickAsync();
		world.Repository.Deliveries[new DeliveryKey("curb-1-0", "01-launch", Network.Bluesky)].Error.Should().Contain("media/card.png is not in campaigns/curb-1-0");

		world.Repository.Media[("curb-1-0", "media/card.png")] = Png;
		time.Advance(TimeSpan.FromMinutes(2));
		await world.TickAsync();
		world.Bluesky.Requests.Should().Contain(r => r.Media.Count == 1 && r.Media[0].Bytes.SequenceEqual(Png) && r.Media[0].Alt == "Curb's logo.");
	}
}
