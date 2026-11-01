using AwesomeAssertions;

namespace Braggarr.Services.Tests;

public class Mp4Tests
{
	[Test]
	public void A_landscape_video_reads_as_stored()
	{
		var info = Mp4.Read(Mp4Fixture.Make(1920, 1080, 12.5));

		info.Should().Be(new VideoInfo(1920, 1080, TimeSpan.FromSeconds(12.5)));
		info!.IsVertical.Should().BeFalse();
	}

	[Test]
	public void A_phone_video_stored_sideways_reads_upright()
	{
		var info = Mp4.Read(Mp4Fixture.Make(1920, 1080, 30, rotated: true));

		info.Should().Be(new VideoInfo(1080, 1920, TimeSpan.FromSeconds(30)));
		info!.IsVertical.Should().BeTrue();
	}

	[Test]
	public void A_sound_track_before_the_picture_is_skipped() =>
		Mp4.Read(Mp4Fixture.Make(1280, 720, 5, soundFirst: true)).Should().Be(new VideoInfo(1280, 720, TimeSpan.FromSeconds(5)));

	[Test]
	public void Anything_else_is_not_a_video()
	{
		Mp4.Read([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]).Should().BeNull();
		Mp4.Read(Mp4Fixture.Make(1280, 720, 5).AsSpan(0, 40)).Should().BeNull("a file cut short has no moov");
		Mp4.Read(Mp4Fixture.Make(0, 0, 5)).Should().BeNull("sound alone has no picture");
	}
}
