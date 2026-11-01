namespace Braggarr.Domain;

/// <summary>
/// Where a variant is posted. Reddit and Hacker News are manual: Braggarr prepares the post and the owner submits it. The blog
/// is a site of the owner's own, built from a repository Braggarr commits the post to.
/// </summary>
public enum Network
{
	Bluesky,
	Mastodon,
	LinkedIn,
	Threads,
	X,
	Reddit,
	HackerNews,
	YouTube,
	Blog
}

/// <summary>How much a network's post holds, which decides how a campaign's talking points are spread over it.</summary>
public enum NetworkShape
{
	/// <summary>A stream people scroll: one point per post, spread over days. Bluesky, Mastodon, Threads, X.</summary>
	Stream,

	/// <summary>Room for an article-like post with the top points: LinkedIn, Reddit.</summary>
	Article,

	/// <summary>A title and a link, for the first point: Hacker News.</summary>
	Title,

	/// <summary>A video with a title and a description, for each point that wants a video: YouTube.</summary>
	Video,

	/// <summary>An article with every point, first, for the other posts to link: the blog.</summary>
	Longform
}

public static class Networks
{
	public static NetworkShape Shape(this Network network) => network switch
	{
		Network.LinkedIn or Network.Reddit => NetworkShape.Article,
		Network.HackerNews => NetworkShape.Title,
		Network.YouTube => NetworkShape.Video,
		Network.Blog => NetworkShape.Longform,
		_ => NetworkShape.Stream
	};

	public static IReadOnlyList<Network> All { get; } = Enum.GetValues<Network>();

	public static bool IsManual(this Network network) => network is Network.Reddit or Network.HackerNews;

	/// <summary>The name used in the posts repo, as the <c>## heading</c> of a variant and in front matter.</summary>
	public static string Slug(this Network network) => network switch
	{
		Network.Bluesky => "bluesky",
		Network.Mastodon => "mastodon",
		Network.LinkedIn => "linkedin",
		Network.Threads => "threads",
		Network.X => "x",
		Network.Reddit => "reddit",
		Network.HackerNews => "hackernews",
		Network.YouTube => "youtube",
		Network.Blog => "blog",
		_ => throw new ArgumentOutOfRangeException(nameof(network), network, null)
	};

	public static bool TryParse(string slug, out Network network)
	{
		foreach (var candidate in All)
		{
			if (string.Equals(candidate.Slug(), slug.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				network = candidate;
				return true;
			}
		}
		network = default;
		return false;
	}
}
