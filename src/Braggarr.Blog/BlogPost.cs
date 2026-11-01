using System.Globalization;
using Nullean.Entities;

namespace Braggarr.Blog;

/// <summary>
/// A post: its front matter and its markdown. <see cref="Folder"/> is where it was read from, relative to the site
/// (<c>posts/2026-11-02-curb-1-0</c>); files beside its <c>index.md</c> are copied next to it, so a relative image keeps working.
/// </summary>
public sealed record BlogPost(string Slug, string Title, DateTimeOffset Date, string Markdown)
{
	public string Folder { get; init; } = "";

	/// <summary>The markdown file the post was read from, relative to the site: where its problems are reported.</summary>
	public string Source { get; init; } = "";

	/// <summary>The files beside the post's markdown, relative to <see cref="Folder"/>.</summary>
	public ValueList<string> Assets { get; init; } = [];

	/// <summary>What the post is about, in a sentence: for cards, feeds and search results. The first paragraph when it has none.</summary>
	public string? Description { get; init; }

	public ValueList<string> Tags { get; init; } = [];

	public bool Draft { get; init; }

	/// <summary>A picture for cards and the top of the post, relative to the post's folder or the site's root.</summary>
	public string? Cover { get; init; }

	public string? CoverAlt { get; init; }

	/// <summary>Paths this post used to live at: each gets a page that sends readers on.</summary>
	public ValueList<string> Aliases { get; init; } = [];

	/// <summary>Where the post first appeared, when it is a copy.</summary>
	public string? Canonical { get; init; }

	/// <summary>The project the post is about, as Braggarr names it.</summary>
	public string? Project { get; init; }

	/// <summary>The Braggarr campaign the post came from.</summary>
	public string? Campaign { get; init; }

	/// <summary>The keys the front matter had that a post does not know.</summary>
	public ValueList<string> UnknownKeys { get; init; } = [];

	/// <summary>Whether the post is out at <paramref name="now"/>: not a draft, and its date has come.</summary>
	public bool IsOutAt(DateTimeOffset now) => !Draft && Date <= now;

	public string Permalink(SiteConfig config) => config.Permalink.For(Slug, Date);

	public override string ToString() => $"{Folder} ({Slug}, {Date.ToString("u", CultureInfo.InvariantCulture)})";
}

[Constraints<BlogPost>]
public static partial class BlogPostRules
{
	public static readonly string[] Keys =
		["title", "date", "description", "tags", "draft", "slug", "cover", "cover_alt", "aliases", "canonical", "project", "campaign"];

	public const int TitleLength = 100;

	public const int DescriptionLength = 160;

	public static readonly Constraint<BlogPost> HasATitle = Constraints<BlogPost>.Always(
		p => !string.IsNullOrWhiteSpace(p.Title),
		"A post has a title: it heads the post, its card and its feed entry."
	);

	public static readonly Constraint<BlogPost> TitleFitsACard = Constraints<BlogPost>.Always(
		p => new StringInfo(p.Title).LengthInTextElements <= TitleLength,
		$"A post's title is at most {TitleLength} characters, so cards and search results show it whole."
	);

	public static readonly Constraint<BlogPost> DescriptionFitsASnippet = Constraints<BlogPost>.Always(
		p => p.Description is null || (p.Description.Trim().Length > 0 && new StringInfo(p.Description).LengthInTextElements <= DescriptionLength),
		$"A post's description says something in at most {DescriptionLength} characters, the length search results and cards show."
	);

	public static readonly Constraint<BlogPost> SlugIsASafeSegment = Constraints<BlogPost>.Always(
		p => Segments.IsSafe(p.Slug),
		"A post's slug names its folder in the built site: no slash, no leading dot, nothing a URL or a file system refuses."
	);

	public static readonly Constraint<BlogPost> TagsAreSlugs = Constraints<BlogPost>.Always(
		p => p.Tags.All(Segments.IsTag),
		"Tags are lowercase letters, digits and dashes, because each names a folder under /tags/."
	);

	public static readonly Constraint<BlogPost> TagsAreDistinct = Constraints<BlogPost>.Always(
		p => p.Tags.Distinct(StringComparer.Ordinal).Count() == p.Tags.Count,
		"A post names each tag once."
	);

	public static readonly Constraint<BlogPost> CoverIsDescribed = Constraints<BlogPost>.Always(
		p => p.Cover is null || !string.IsNullOrWhiteSpace(p.CoverAlt),
		"A cover has alt text: it is the first thing a screen reader meets."
	);

	public static readonly Constraint<BlogPost> AliasesArePathsOnTheSite = Constraints<BlogPost>.Always(
		p => p.Aliases.All(Segments.IsSitePath),
		"An alias is a path on this site (/old/path/ or /old.html), with no .., query or fragment."
	);

	public static readonly Constraint<BlogPost> CanonicalIsAFullAddress = Constraints<BlogPost>.Always(
		p => p.Canonical is null || (Uri.TryCreate(p.Canonical, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"),
		"A canonical address is a full one (https://…)."
	);

	public static readonly Constraint<BlogPost> AssetsStayBesideThePost = Constraints<BlogPost>.Always(
		p => p.Assets.All(a => a.Split('/').All(Segments.IsSafe)),
		"A post's files sit beside its markdown, in its folder or below: none is hidden or reached through .."
	);

	public static readonly Constraint<BlogPost> NoUnknownKeys = Constraints<BlogPost>.Always(
		p => p.UnknownKeys.Count == 0,
		"A post's front matter has only keys a post knows: a misspelt one would be ignored without a word."
	);
}
