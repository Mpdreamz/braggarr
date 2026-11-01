using AwesomeAssertions;

namespace Braggarr.Domain.Tests;

/// <summary>What a complete profile has on each network, and what Braggarr may set there.</summary>
public class ProfilesTests
{
	private static AccountProfile Full(Network network) =>
		new(network, "me", "Martijn", "I make heed, a Mac app for running coding agents, and the Nullean .NET tools. heed.build", "https://heed.build", true, true);

	[Test]
	public void A_complete_profile_has_nothing_missing_on_any_network()
	{
		foreach (var network in Networks.All.Where(Profiles.HasProfile))
		{
			Profiles.Check(Full(network)).Should().OnlyContain(f => f.Ok, network.ToString());
		}
	}

	[Test]
	public void An_empty_profile_misses_its_name_bio_link_picture_and_banner_where_the_network_has_them()
	{
		var findings = Profiles.Check(new AccountProfile(Network.Mastodon, "me", null, null, null, false, false));
		var bluesky = Profiles.Check(new AccountProfile(Network.Bluesky, "me", "Martijn", "Short bio.", null, true, null));

		findings.Where(f => !f.Ok).Select(f => f.Field).Should().Equal(ProfileField.DisplayName, ProfileField.Bio, ProfileField.Link, ProfileField.Avatar, ProfileField.Banner);
		bluesky.Where(f => !f.Ok).Select(f => f.Field).Should().Equal([ProfileField.Bio, ProfileField.Link], "a short bio with no link; a banner nobody told about is not judged");
	}

	[Test]
	public void Linkedin_is_judged_only_on_what_its_api_tells()
	{
		Profiles.Check(new AccountProfile(Network.LinkedIn, "Martijn", "Martijn", null, null, false, null)).Select(f => f.Field)
			.Should().Equal(ProfileField.DisplayName, ProfileField.Avatar);
	}

	[Test]
	public void A_bio_counts_graphemes_on_bluesky_and_characters_elsewhere()
	{
		var flags = string.Concat(Enumerable.Repeat("🇳🇱", 200));

		Profiles.Fits(Network.Bluesky, ProfileField.Bio, flags).Should().BeTrue("200 flags are 200 graphemes, within 256");
		Profiles.Fits(Network.X, ProfileField.Bio, flags).Should().BeFalse();
		Profiles.Fits(Network.X, ProfileField.Bio, new string('a', 160)).Should().BeTrue();
		Profiles.Fits(Network.X, ProfileField.Bio, new string('a', 161)).Should().BeFalse();
		Profiles.Fits(Network.Mastodon, ProfileField.DisplayName, "Two\nlines").Should().BeFalse();
		Profiles.Fits(Network.Mastodon, ProfileField.Link, "javascript:alert(1)").Should().BeFalse();
	}

	[Test]
	public void Only_bluesky_mastodon_x_and_youtube_take_changes_and_never_a_picture()
	{
		Networks.All.Where(n => Enum.GetValues<ProfileField>().Any(f => Profiles.CanApply(n, f))).Should().BeEquivalentTo([Network.Bluesky, Network.Mastodon, Network.X, Network.YouTube]);
		Networks.All.Should().NotContain(n => Profiles.CanApply(n, ProfileField.Avatar) || Profiles.CanApply(n, ProfileField.Banner));
		Profiles.CanApply(Network.YouTube, ProfileField.DisplayName).Should().BeFalse("a channel's name is not changed through the API");
	}
}
