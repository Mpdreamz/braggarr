using AwesomeAssertions;

namespace Braggarr.Domain.Tests;

public class MediaTests
{
	private static readonly MediaRef Video = MediaRef.Video(new string('a', 64), "A run where one line changes");
	private static readonly MediaRef Image = new("media/diff.png", "A diff");

	[Test]
	public void A_stored_video_alone_fits_every_network_that_takes_video()
	{
		foreach (var network in Networks.All)
		{
			new Variant(network, "Out.", [Video]).MediaFits.Should().Be(Lengths.TakesVideo(network), network.ToString());
		}
		Lengths.TakesVideo(Network.Threads).Should().BeFalse();
		Lengths.TakesVideo(Network.HackerNews).Should().BeFalse();
	}

	[Test]
	public void A_post_carries_one_video_or_images_not_both()
	{
		new Variant(Network.Bluesky, "Out.", [Video, Image]).MediaFits.Should().BeFalse();
		new Variant(Network.Bluesky, "Out.", [Video, Video]).MediaFits.Should().BeFalse();
		new Variant(Network.Bluesky, "Out.", [MediaRef.Wanted(new WantedMedia(MediaKind.Video, "A run")), Image]).MediaFits.Should().BeFalse();
		new Variant(Network.Bluesky, "Out.", [Image, Image with { Path = "media/b.png" }]).MediaFits.Should().BeTrue();
	}

	[Test]
	public void Only_a_hash_named_mp4_is_a_stored_video()
	{
		new MediaRef($"video:sha256-{new string('a', 64)}.mp4", "x").IsKnownPath.Should().BeTrue();
		new MediaRef("video:../../etc/passwd", "x").IsKnownPath.Should().BeFalse();
		new MediaRef($"video:sha256-{new string('A', 64)}.mp4", "x").IsKnownPath.Should().BeFalse();
		new MediaRef($"video:sha256-{new string('a', 63)}.mp4", "x").IsKnownPath.Should().BeFalse();
		new MediaRef("media/../secrets.png", "x").IsKnownPath.Should().BeFalse();
	}
}
