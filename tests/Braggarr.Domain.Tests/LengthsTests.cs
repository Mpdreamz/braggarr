using AwesomeAssertions;

namespace Braggarr.Domain.Tests;

public class LengthsTests
{
	[Test]
	public void Bluesky_counts_graphemes_not_utf16_units()
	{
		const string family = "👨‍👩‍👧‍👦";

		family.Length.Should().Be(11);
		Lengths.Measure(Network.Bluesky, family).Should().Be(1);
		Lengths.Fits(Network.Bluesky, new string('a', 299) + family).Should().BeTrue();
		Lengths.Fits(Network.Bluesky, new string('a', 300) + family).Should().BeFalse();
	}

	[Test]
	public void A_url_counts_as_23_on_x_and_mastodon_however_long_it_is()
	{
		var text = "Out now: https://github.com/nullean/curb/releases/tag/1.0.0-with-a-very-long-suffix-indeed";

		Lengths.Measure(Network.X, text).Should().Be("Out now: ".Length + 23);
		Lengths.Measure(Network.Mastodon, text).Should().Be("Out now: ".Length + 23);
	}

	[Test]
	public void X_counts_cjk_and_emoji_double()
	{
		Lengths.Measure(Network.X, "日本").Should().Be(4);
		Lengths.Measure(Network.X, "👍🏽").Should().Be(2);
		Lengths.Measure(Network.X, "café").Should().Be(4);
	}

	[Test]
	public void Hacker_news_and_reddit_count_only_the_title()
	{
		var text = "Show HN: Curb, a C# formatter that keeps diffs small\nhttps://github.com/nullean/curb\n" + new string('x', 500);

		Lengths.Measure(Network.HackerNews, text).Should().Be(52);
		Lengths.Fits(Network.HackerNews, text).Should().BeTrue();
		Lengths.Fits(Network.HackerNews, new string('x', 81)).Should().BeFalse();
	}

	[Test]
	public void An_empty_variant_never_fits() => Lengths.Fits(Network.LinkedIn, "  \n ").Should().BeFalse();

	[Test]
	public void An_image_needs_alt_text_a_media_path_and_room_on_its_network()
	{
		MediaRef card = new("media/card.png", "Curb's logo over a diff.");

		new Variant(Network.Bluesky, "Out.", [card]).MediaFits.Should().BeTrue();
		new Variant(Network.Bluesky, "Out.", [card with { Alt = " " }]).MediaFits.Should().BeFalse();
		new Variant(Network.Bluesky, "Out.", [card with { Path = "../secrets.json" }]).MediaFits.Should().BeFalse();
		new Variant(Network.Bluesky, "Out.", [card, card, card, card, card]).MediaFits.Should().BeFalse();
		new Variant(Network.HackerNews, "Show HN: Curb", [card]).MediaFits.Should().BeFalse();
	}

	[Test]
	public void Threads_counts_an_emoji_by_its_bytes()
	{
		Lengths.Measure(Network.Threads, "Out now 🎉").Should().Be(8 + 4);
		Lengths.Measure(Network.Threads, "café").Should().Be(4);
		Lengths.MediaLimit(Network.Threads).Should().Be(0);
	}

	[Test]
	public void A_youtube_title_is_the_first_line_and_the_description_counts_bytes()
	{
		var title = new string('t', 100);

		Lengths.Fits(Network.YouTube, $"{title}\n\nWhat the video shows.").Should().BeTrue();
		Lengths.Fits(Network.YouTube, $"{title}t\n\nWhat the video shows.").Should().BeFalse("the title is 101 characters");
		Lengths.Fits(Network.YouTube, $"Title\n\n{new string('é', 2500)}").Should().BeTrue("2500 two-byte characters are 5000 bytes");
		Lengths.Fits(Network.YouTube, $"Title\n\n{new string('é', 2500)}e").Should().BeFalse("5001 bytes");
		Lengths.Description("Title\n\n  Body.\nMore.  ").Should().Be("Body.\nMore.");
	}

	[Test]
	public void YouTube_refuses_angle_brackets_anywhere()
	{
		Lengths.Fits(Network.YouTube, "Curb <3\n\nBody.").Should().BeFalse();
		Lengths.Fits(Network.YouTube, "Curb\n\nList<T> formatting.").Should().BeFalse();
		Lengths.Fits(Network.Bluesky, "List<T> formatting.").Should().BeTrue("only YouTube refuses them");
	}

	[Test]
	public void A_youtube_variant_carries_one_video_and_never_an_image()
	{
		var video = MediaRef.Video(new string('a', 64), "A run");

		new Variant(Network.YouTube, "Title\n\nBody.", [video]).MediaFits.Should().BeTrue();
		new Variant(Network.YouTube, "Title\n\nBody.", [MediaRef.Wanted(new WantedMedia(MediaKind.Video, "A run"))]).MediaFits.Should().BeTrue();
		new Variant(Network.YouTube, "Title\n\nBody.", [new MediaRef("media/card.png", "A card")]).MediaFits.Should().BeFalse();
		new Variant(Network.YouTube, "Title\n\nBody.", [video, video]).MediaFits.Should().BeFalse();
	}

	[Test]
	public void A_campaign_with_a_youtube_post_is_not_ready_until_its_video_is_uploaded()
	{
		var wanted = new Variant(Network.YouTube, "Curb formats a file\n\nWhat it does.", [MediaRef.Wanted(new WantedMedia(MediaKind.Video, "A run"))]);
		var campaign = new Campaign("curb-1-0", "Curb", "curb", CampaignStatus.Proposed, [Network.YouTube], ["https://github.com/nullean/curb"], "", "",
			[new Post("01-run", Gens.Start, [wanted])]);

		campaign.IsComplete.Should().BeTrue();
		campaign.HasItsVideos.Should().BeFalse();
		campaign.Approve(new(Gens.Owner, Gens.Start)).Status.Should().Be(CampaignStatus.Proposed);
		var uploaded = campaign with { Posts = [campaign.Posts[0] with { Variants = [wanted with { Media = [Gens.Video] }] }] };
		uploaded.Approve(new(Gens.Owner, Gens.Start)).Status.Should().Be(CampaignStatus.Approved);
	}

	[Test]
	public void A_link_to_the_blog_counts_as_a_link_before_its_address_is_known()
	{
		Lengths.Measure(Network.Bluesky, "Read it: {blog}").Should().Be("Read it: ".Length + 60);
		Lengths.Measure(Network.X, "Read it: {blog}").Should().Be("Read it: ".Length + Lengths.UrlLength);
		Lengths.Measure(Network.Mastodon, "Read it: {blog}").Should().Be("Read it: ".Length + Lengths.UrlLength);
		Lengths.Measure(Network.Blog, "Read it: {blog}").Should().Be("Read it: {blog}".Length, "the article itself never links itself");
	}
}
