using System.Globalization;
using System.Text;

namespace Braggarr.Blog;

/// <summary>
/// Where a post lives on the site, as a pattern: <c>/articles/{slug}/</c>, <c>/{year}/{month}/{slug}/</c>. Braggarr renders the
/// same pattern before it commits a post, so it knows the post's address before the site is built.
/// </summary>
public sealed record Permalink(string Pattern)
{
	public static readonly Permalink Default = new("/posts/{slug}/");

	/// <summary>The tokens a pattern may name. Anything else between braces is a mistake.</summary>
	public static IReadOnlyList<string> Tokens { get; } = ["slug", "year", "month", "day"];

	/// <summary>The names between braces, in order, known or not.</summary>
	public IEnumerable<string> Named()
	{
		var at = 0;
		while ((at = Pattern.IndexOf('{', at)) >= 0)
		{
			var end = Pattern.IndexOf('}', at);
			if (end < 0)
			{
				yield return Pattern[(at + 1)..];
				yield break;
			}
			yield return Pattern[(at + 1)..end];
			at = end + 1;
		}
	}

	/// <summary>
	/// The address of a post with <paramref name="slug"/>, dated <paramref name="date"/> in its own offset. The slug goes in as it is
	/// (a slug is a safe segment): a moved site keeps <c>/articles/oss-development,-a-lesson/</c> to the character.
	/// </summary>
	public string For(string slug, DateTimeOffset date) =>
		new StringBuilder(Pattern)
			.Replace("{slug}", slug)
			.Replace("{year}", date.Year.ToString("0000", CultureInfo.InvariantCulture))
			.Replace("{month}", date.Month.ToString("00", CultureInfo.InvariantCulture))
			.Replace("{day}", date.Day.ToString("00", CultureInfo.InvariantCulture))
			.ToString();

	public override string ToString() => Pattern;
}

/// <summary>What a slug or a path segment may be: it names a folder in the built site.</summary>
public static class Segments
{
	/// <summary>
	/// A path segment the built site can hold: not empty, no slash or backslash, not <c>.</c> or <c>..</c>, not hidden, no characters
	/// a file system or URL refuses. Looser than a Braggarr slug, because sites moved from elsewhere keep slugs like
	/// <c>the-joys-of-.net</c>.
	/// </summary>
	public static bool IsSafe(string segment) =>
		segment.Length is > 0 and <= 128
		&& segment[0] != '.'
		&& segment.All(c => !char.IsControl(c) && !char.IsWhiteSpace(c) && c is not ('/' or '\\' or '?' or '#' or '%' or ':' or '*' or '"' or '<' or '>' or '|'));

	/// <summary>A tag: lowercase letters, digits and dashes, because it names a folder under <c>/tags/</c>.</summary>
	public static bool IsTag(string tag) =>
		tag.Length is > 0 and <= 64
		&& tag[0] != '-' && tag[^1] != '-'
		&& tag.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
		&& !tag.Contains("--", StringComparison.Ordinal);

	/// <summary>
	/// A path on this site: starts with <c>/</c>, no <c>..</c>, no empty or hidden segment, no query or fragment. An alias or a
	/// permalink is one.
	/// </summary>
	public static bool IsSitePath(string path)
	{
		if (path.Length < 1 || path[0] != '/' || path.Contains('?') || path.Contains('#') || path.Contains('\\'))
		{
			return false;
		}
		var segments = path[1..].Split('/');
		// The last segment may be empty: "/about/" is a folder.
		return segments.Take(segments.Length - 1).All(s => s.Length > 0 && s[0] != '.')
			&& (segments[^1].Length == 0 || segments[^1][0] != '.');
	}

	/// <summary>
	/// A GitHub repository as <c>owner/name</c>: letters, digits and dashes for the owner, and dots and underscores too for the name,
	/// which is never <c>.</c> or <c>..</c>. The page asks GitHub about it by that name.
	/// </summary>
	public static bool IsRepository(string repository) =>
		repository.Split('/') is [var owner, var name]
		&& owner.Length is > 0 and <= 39 && owner.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
		&& name.Length is > 0 and <= 100 && name is not ("." or "..") && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_');

	/// <summary>A slug from a title: "Curb 1.0 is out!" becomes "curb-1-0-is-out". Braggarr's own rule, so new posts get the same.</summary>
	public static string Slug(string title)
	{
		var builder = new StringBuilder();
		foreach (var c in title.ToLowerInvariant().Normalize(NormalizationForm.FormD))
		{
			if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
			{
				builder.Append(c);
			}
			else if (builder.Length > 0 && builder[^1] != '-' && char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
			{
				builder.Append('-');
			}
		}
		var slug = builder.ToString().Trim('-');
		return slug.Length > 64 ? slug[..64].TrimEnd('-') : slug;
	}
}
