using System.Text.RegularExpressions;
using Nullean.Entities;

namespace Braggarr.Blog;

/// <summary>Who writes the site: shown on every post, and in the feed.</summary>
public sealed record Author(string Name)
{
	/// <summary>A picture beside the name: a path in the site (<c>images/me.jpg</c>) or a full https address.</summary>
	public string? Avatar { get; init; }

	public string? Bio { get; init; }
}

/// <summary>A link in the site's header: to a page, or to any path or address.</summary>
public sealed record NavLink(string Title, string Href);

/// <summary>Comments through giscus: GitHub Discussions on the site's repository. The ids come from giscus.app.</summary>
public sealed record Giscus(string Repo, string RepoId, string Category, string CategoryId)
{
	/// <summary>How a page finds its discussion: by its path, which a post keeps.</summary>
	public string Mapping { get; init; } = "pathname";
}

/// <summary>
/// A site, as its <c>braggarr.yml</c> says: what it is called, where it lives, who writes it, and where its posts go. Profile links
/// are not here: Braggarr writes them from the owner's connected networks.
/// </summary>
public sealed record SiteConfig(string Title, string Description, Uri Url, Author Author)
{
	public string Language { get; init; } = "en";

	public Permalink Permalink { get; init; } = Permalink.Default;

	public ValueList<NavLink> Nav { get; init; } = [];

	/// <summary>The theme's accent, as <c>#rgb</c> or <c>#rrggbb</c>. The theme picks one when there is none.</summary>
	public string? Accent { get; init; }

	public Giscus? Comments { get; init; }

	/// <summary>The Atom feed's path. <c>feed.xml</c> by default, which is what feed readers subscribed to most blogs already ask for.</summary>
	public string Feed { get; init; } = "feed.xml";

	public int PostsPerPage { get; init; } = 10;

	/// <summary>How many posts the feed holds, newest first.</summary>
	public int FeedSize { get; init; } = 20;

	/// <summary>The keys <c>braggarr.yml</c> had that the site does not know, so a typo is reported instead of ignored.</summary>
	public ValueList<string> UnknownKeys { get; init; } = [];

	/// <summary>The full address of a path on the site.</summary>
	public string Absolute(string path) => new Uri(Url, path).ToString();
}

[Constraints<SiteConfig>]
public static partial class SiteConfigRules
{
	public static readonly string[] Keys =
		["title", "description", "url", "language", "author", "permalink", "nav", "accent", "comments", "feed", "posts_per_page", "feed_size"];

	public static readonly Constraint<SiteConfig> HasATitle = Constraints<SiteConfig>.Always(
		c => !string.IsNullOrWhiteSpace(c.Title),
		"A site has a title: it heads every page and names the feed."
	);

	public static readonly Constraint<SiteConfig> UrlIsTheSitesRoot = Constraints<SiteConfig>.Always(
		c => c.Url.IsAbsoluteUri && c.Url.Scheme is "https" or "http" && c.Url.Query.Length == 0 && c.Url.Fragment.Length == 0
			&& c.Url.AbsolutePath.EndsWith('/'),
		"The url is the site's root (https://example.com/ or https://example.com/blog/): feeds, cards and the sitemap give full addresses from it."
	);

	public static readonly Constraint<SiteConfig> AuthorHasAName = Constraints<SiteConfig>.Always(
		c => !string.IsNullOrWhiteSpace(c.Author.Name),
		"The author has a name: every post and the feed say who wrote it."
	);

	public static readonly Constraint<SiteConfig> AvatarIsInTheSiteOrHttps = Constraints<SiteConfig>.Always(
		c => c.Author.Avatar is null || IsSiteFile(c.Author.Avatar) || IsHttps(c.Author.Avatar),
		"The avatar is a file in the site (images/me.jpg) or a full https address."
	);

	public static readonly Constraint<SiteConfig> PermalinkNamesTheSlug = Constraints<SiteConfig>.Always(
		c => c.Permalink.Named().Contains("slug"),
		"The permalink names {slug}: without it, two posts on one day would share an address."
	);

	public static readonly Constraint<SiteConfig> PermalinkNamesOnlyKnownTokens = Constraints<SiteConfig>.Always(
		c => c.Permalink.Named().All(Permalink.Tokens.Contains),
		"The permalink names only {slug}, {year}, {month} and {day}."
	);

	public static readonly Constraint<SiteConfig> PermalinkIsAFolder = Constraints<SiteConfig>.Always(
		c => Segments.IsSitePath(c.Permalink.Pattern) && c.Permalink.Pattern.EndsWith('/') && c.Permalink.Pattern.Length > 1,
		"The permalink is a folder path on the site, like /articles/{slug}/: each post is written as its index.html."
	);

	public static readonly Constraint<SiteConfig> NavGoesSomewhere = Constraints<SiteConfig>.Always(
		c => c.Nav.All(n => !string.IsNullOrWhiteSpace(n.Title) && (Segments.IsSitePath(n.Href) || IsHttps(n.Href))),
		"Each header link has a title and goes to a path on the site (/about/) or a full https address."
	);

	public static readonly Constraint<SiteConfig> AccentIsAColor = Constraints<SiteConfig>.Always(
		c => c.Accent is null || Hex().IsMatch(c.Accent),
		"The accent is a colour as #rgb or #rrggbb: it goes into the theme's CSS as it is."
	);

	public static readonly Constraint<SiteConfig> CommentsAreComplete = Constraints<SiteConfig>.Always(
		c => c.Comments is null || (new[] { c.Comments.Repo, c.Comments.RepoId, c.Comments.Category, c.Comments.CategoryId }.All(v => !string.IsNullOrWhiteSpace(v))
			&& c.Comments.Repo.Count(ch => ch == '/') == 1),
		"giscus needs the repository (owner/name), its id, the category and its id, as giscus.app gives them."
	);

	public static readonly Constraint<SiteConfig> FeedIsAFileInTheRoot = Constraints<SiteConfig>.Always(
		c => Segments.IsSafe(c.Feed) && c.Feed.EndsWith(".xml", StringComparison.Ordinal),
		"The feed is an .xml file in the site's root, like feed.xml."
	);

	public static readonly Constraint<SiteConfig> PagesHoldPosts = Constraints<SiteConfig>.Always(
		c => c.PostsPerPage is >= 1 and <= 100 && c.FeedSize is >= 1 and <= 100,
		"A page of the index and the feed each hold between 1 and 100 posts."
	);

	public static readonly Constraint<SiteConfig> NoUnknownKeys = Constraints<SiteConfig>.Always(
		c => c.UnknownKeys.Count == 0,
		"braggarr.yml has only keys the site knows: a misspelt one would be ignored without a word."
	);

	internal static bool IsHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https";

	/// <summary>A file in the site, given relative to its root: <c>images/me.jpg</c>, never <c>../</c> or hidden.</summary>
	internal static bool IsSiteFile(string value) =>
		!value.StartsWith('/') && !Uri.TryCreate(value, UriKind.Absolute, out _) && value.Split('/').All(Segments.IsSafe);

	[GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
	private static partial Regex Hex();
}
