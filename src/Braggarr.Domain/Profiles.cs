using System.Globalization;

namespace Braggarr.Domain;

public enum ProfileField
{
	DisplayName,
	Bio,
	Link,
	Avatar,
	Banner
}

/// <summary>
/// One of the owner's accounts as people see it: its name, bio and link, and whether it has a picture and a banner. A value
/// the network does not have, or does not tell, is null.
/// </summary>
public sealed record AccountProfile(Network Network, string Account, string? DisplayName, string? Bio, string? Link, bool? HasAvatar, bool? HasBanner)
{
	/// <summary>Where the profile is seen, when the network says.</summary>
	public string? Url { get; init; }
}

/// <summary>One thing about a profile: whether it is there and good enough, and what to do when it is not.</summary>
public sealed record ProfileFinding(ProfileField Field, bool Ok, string Advice);

/// <summary>What makes a profile complete on each network, and which parts Braggarr can set there.</summary>
public static partial class Profiles
{
	/// <summary>A bio shorter than this says too little to tell a visitor what the account posts.</summary>
	public const int ShortestBio = 40;

	/// <summary>The longest bio each network takes: in graphemes on Bluesky, characters elsewhere. LinkedIn's is the headline.</summary>
	public static int BioLimit(Network network) => network switch
	{
		Network.Bluesky => 256,
		Network.Mastodon => 500,
		Network.X => 160,
		Network.Threads => 150,
		Network.YouTube => 1000,
		Network.LinkedIn => 220,
		_ => 0
	};

	public static int NameLimit(Network network) => network switch
	{
		Network.Bluesky => 64,
		Network.Mastodon => 30,
		Network.X => 50,
		Network.YouTube => 100,
		_ => 100
	};

	/// <summary>A network whose accounts have a profile Braggarr reads: not the manual ones, and not the blog, which is a site.</summary>
	public static bool HasProfile(Network network) => !network.IsManual() && network != Network.Blog;

	/// <summary>The network has a field for a website, apart from the bio.</summary>
	public static bool HasLinkField(Network network) => network is Network.Mastodon or Network.X;

	/// <summary>The network shows a banner over the profile.</summary>
	public static bool HasBanner(Network network) => network is Network.Bluesky or Network.Mastodon or Network.X or Network.YouTube or Network.LinkedIn;

	/// <summary>
	/// What Braggarr can change through the network's API: Bluesky's name and bio, Mastodon's and X's name, bio and link, and
	/// YouTube's description. LinkedIn and Threads have no API for it: their text is for the owner to paste.
	/// </summary>
	public static bool CanApply(Network network, ProfileField field) => (network, field) switch
	{
		(Network.Bluesky, ProfileField.DisplayName or ProfileField.Bio) => true,
		(Network.Mastodon or Network.X, ProfileField.DisplayName or ProfileField.Bio or ProfileField.Link) => true,
		(Network.YouTube, ProfileField.Bio) => true,
		_ => false
	};

	public static int Length(Network network, string text) =>
		network == Network.Bluesky ? new StringInfo(text).LengthInTextElements : text.Length;

	/// <summary>Whether text fits the field on the network; a link must be a web address.</summary>
	public static bool Fits(Network network, ProfileField field, string text) => field switch
	{
		ProfileField.DisplayName => text.Trim().Length > 0 && Length(network, text) <= NameLimit(network) && !text.Contains('\n'),
		ProfileField.Bio => Length(network, text) <= BioLimit(network),
		ProfileField.Link => Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http",
		_ => false
	};

	/// <summary>What a visitor misses on the profile, in order of how much it matters.</summary>
	public static IReadOnlyList<ProfileFinding> Check(AccountProfile profile)
	{
		var network = profile.Network;
		var findings = new List<ProfileFinding>
		{
			string.IsNullOrWhiteSpace(profile.DisplayName)
				? new(ProfileField.DisplayName, false, "No display name: people see only the handle.")
				: new(ProfileField.DisplayName, true, profile.DisplayName)
		};
		if (network == Network.LinkedIn)
		{
			// LinkedIn tells the name and the picture only; the headline and about are the owner's to check on LinkedIn.
			if (profile.HasAvatar is { } picture)
			{
				findings.Add(picture ? new(ProfileField.Avatar, true, "Has a picture.") : new(ProfileField.Avatar, false, "No profile picture."));
			}
			return findings;
		}
		var bio = profile.Bio?.Trim() ?? "";
		findings.Add(
			bio.Length == 0 ? new(ProfileField.Bio, false, "No bio: nothing says what this account posts about.")
			: Length(network, bio) < ShortestBio ? new(ProfileField.Bio, false, "The bio is very short: say what you make and for whom.")
			: Length(network, bio) > BioLimit(network) ? new(ProfileField.Bio, false, $"The bio is longer than {network}'s {BioLimit(network)}.")
			: new(ProfileField.Bio, true, "Says what the account is about.")
		);
		if (HasLinkField(network))
		{
			findings.Add(string.IsNullOrWhiteSpace(profile.Link)
				? new(ProfileField.Link, false, "No website: link to where people can try what you post about.")
				: new(ProfileField.Link, true, profile.Link));
		}
		else if (network is not (Network.YouTube or Network.LinkedIn))
		{
			findings.Add(Address().IsMatch(bio)
				? new(ProfileField.Link, true, "The bio points somewhere.")
				: new(ProfileField.Link, false, $"{network} has no website field: put one link in the bio."));
		}
		if (profile.HasAvatar is false)
		{
			findings.Add(new(ProfileField.Avatar, false, "No profile picture: the default one reads as an empty or new account."));
		}
		else if (profile.HasAvatar is true)
		{
			findings.Add(new(ProfileField.Avatar, true, "Has a picture."));
		}
		if (HasBanner(network) && profile.HasBanner is { } banner)
		{
			findings.Add(banner ? new(ProfileField.Banner, true, "Has a banner.") : new(ProfileField.Banner, false, "No banner: the top of the profile is empty."));
		}
		return findings;
	}

	/// <summary>Something a network shows as a link: a web address, or a domain like heed.build.</summary>
	[System.Text.RegularExpressions.GeneratedRegex(@"https?://|\b[a-z0-9-]+(\.[a-z0-9-]+)*\.[a-z]{2,}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
	private static partial System.Text.RegularExpressions.Regex Address();
}
