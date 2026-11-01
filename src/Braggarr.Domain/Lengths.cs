using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Braggarr.Domain;

/// <summary>How each network counts the length of a post, and its limit. Pure functions, one per network.</summary>
public static partial class Lengths
{
	/// <summary>Mastodon's default. A server may allow more; Braggarr keeps to the default so a variant fits anywhere.</summary>
	public const int MastodonDefault = 500;

	/// <summary>The length a URL counts as on X and Mastodon, however long it is.</summary>
	public const int UrlLength = 23;

	public static int Limit(Network network) => network switch
	{
		Network.Bluesky => 300,
		Network.Mastodon => MastodonDefault,
		Network.LinkedIn => 3000,
		Network.Threads => 500,
		Network.X => 280,
		Network.Reddit => 300,
		Network.HackerNews => 80,
		Network.YouTube => YouTubeTitle,
		// An article: as long as it needs to be. Its title and description have their own limits (Blogging).
		Network.Blog => int.MaxValue,
		_ => throw new ArgumentOutOfRangeException(nameof(network), network, null)
	};

	/// <summary>The length of <paramref name="text"/> as <paramref name="network"/> counts it. For Reddit and HN, only the title (the first line) counts.</summary>
	public static int Measure(Network network, string text) => MeasureAsSent(network, network == Network.Blog ? text : text.Replace(Blogging.Placeholder, Blogging.MeasuredAs, StringComparison.Ordinal));

	private static int MeasureAsSent(Network network, string text) => network switch
	{
		Network.Bluesky => Graphemes(text),
		Network.Mastodon => WithUrlsAs23(text, CodePoints),
		Network.LinkedIn => CodePoints(text),
		Network.Threads => ThreadsWeighted(text),
		Network.X => WithUrlsAs23(text, XWeighted),
		Network.Reddit or Network.HackerNews or Network.YouTube => Graphemes(Title(text)),
		Network.Blog => CodePoints(text),
		_ => throw new ArgumentOutOfRangeException(nameof(network), network, null)
	};

	/// <summary>
	/// How many images a variant may carry. Four is what Bluesky, Mastodon and X allow, and Braggarr keeps every automatic network
	/// to it so one set of images works everywhere. Reddit and Hacker News posts are links, so none. Threads fetches images from a
	/// public URL, which Braggarr behind Tailscale does not have, so none there either.
	/// </summary>
	public static int MediaLimit(Network network) => network switch
	{
		_ when network.IsManual() || network == Network.Threads => 0,
		Network.YouTube => 1,
		// The cover and the images in the article, copied beside it on its site.
		Network.Blog => Blogging.ImageLimit,
		_ => 4
	};

	/// <summary>Bluesky, Mastodon, LinkedIn and X take a video in place of images, one per post.</summary>
	public static bool TakesVideo(Network network) => network is Network.Bluesky or Network.Mastodon or Network.LinkedIn or Network.X or Network.YouTube;

	/// <summary>A network whose post is the video: YouTube. Its variant carries one video, never an image, and is not ready without it.</summary>
	public static bool NeedsVideo(Network network) => network == Network.YouTube;

	/// <summary>
	/// The shortest and longest video a network takes, as its docs said in October 2026: Bluesky three minutes, LinkedIn three
	/// seconds to thirty minutes, X twenty minutes without Premium. Mastodon's servers set their own size limit and no length.
	/// </summary>
	public static (TimeSpan Shortest, TimeSpan? Longest) VideoLength(Network network) => network switch
	{
		Network.Bluesky => (TimeSpan.Zero, TimeSpan.FromMinutes(3)),
		Network.LinkedIn => (TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(30)),
		Network.X => (TimeSpan.Zero, TimeSpan.FromMinutes(20)),
		// Fifteen minutes until the Google account is verified; Braggarr keeps to what every account allows.
		Network.YouTube => (TimeSpan.Zero, TimeSpan.FromMinutes(15)),
		_ => (TimeSpan.Zero, null)
	};

	/// <summary>The longest alt text Braggarr accepts: under Mastodon's 1500 and Bluesky's 2000.</summary>
	public const int AltLimit = 1000;

	public static bool Fits(Network network, string text) =>
		text.Trim().Length > 0 && Measure(network, text) <= Limit(network) && (network != Network.YouTube || YouTubeFits(text));

	/// <summary>YouTube's title, the first line: at most 100 characters.</summary>
	public const int YouTubeTitle = 100;

	/// <summary>YouTube's description, everything after the title: at most 5000 bytes of UTF-8.</summary>
	public const int YouTubeDescriptionBytes = 5000;

	/// <summary>The description fits in bytes, and neither it nor the title has a <c>&lt;</c> or <c>&gt;</c>, which YouTube refuses.</summary>
	private static bool YouTubeFits(string text) =>
		Encoding.UTF8.GetByteCount(Description(text)) <= YouTubeDescriptionBytes && !text.Contains('<', StringComparison.Ordinal) && !text.Contains('>', StringComparison.Ordinal);

	/// <summary>Everything after the first line, trimmed: a YouTube description, a Reddit body.</summary>
	public static string Description(string text)
	{
		var trimmed = text.TrimStart();
		var newline = trimmed.IndexOf('\n');
		return newline < 0 ? "" : trimmed[(newline + 1)..].Trim();
	}

	public static string Title(string text)
	{
		var trimmed = text.TrimStart();
		var newline = trimmed.IndexOf('\n');
		return (newline < 0 ? trimmed : trimmed[..newline]).Trim();
	}

	public static int Graphemes(string text) => new StringInfo(text).LengthInTextElements;

	public static int CodePoints(string text) => text.EnumerateRunes().Count();

	/// <summary>
	/// X's weighted count: a character in the Latin, Greek, Cyrillic and general punctuation ranges counts 1, anything else
	/// (CJK, emoji) counts 2. An emoji sequence joined into one grapheme counts 2 in total, as X counts it.
	/// </summary>
	public static int XWeighted(string text)
	{
		var total = 0;
		var graphemes = StringInfo.GetTextElementEnumerator(text);
		while (graphemes.MoveNext())
		{
			var runes = graphemes.GetTextElement().EnumerateRunes().ToArray();
			total += runes.All(IsLight) ? runes.Length : 2;
		}
		return total;
	}

	/// <summary>Threads counts characters, but an emoji as the number of bytes it takes in UTF-8.</summary>
	public static int ThreadsWeighted(string text) =>
		text.EnumerateRunes().Sum(r => r.Value is >= 0x1F000 or (>= 0x2600 and <= 0x27BF) ? r.Utf8SequenceLength : 1);

	private static bool IsLight(Rune rune) =>
		rune.Value is (>= 0 and <= 4351) or (>= 8192 and <= 8205) or (>= 8208 and <= 8223) or (>= 8242 and <= 8247);

	private static int WithUrlsAs23(string text, Func<string, int> count)
	{
		var urls = UrlPattern().Matches(text);
		var withoutUrls = UrlPattern().Replace(text, "");
		return count(withoutUrls) + (urls.Count * UrlLength);
	}

	[GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
	private static partial Regex UrlPattern();
}
