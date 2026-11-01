using System.Text;
using System.Text.RegularExpressions;

namespace Braggarr.Domain;

public static partial class Slugs
{
	public static bool IsValid(string slug) => slug.Length is > 0 and <= 64 && Pattern().IsMatch(slug);

	/// <summary>A slug from a title: "Curb 1.0 is out!" becomes "curb-1-0-is-out".</summary>
	public static string From(string title)
	{
		var builder = new StringBuilder();
		foreach (var c in title.ToLowerInvariant().Normalize(NormalizationForm.FormD))
		{
			if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
			{
				builder.Append(c);
			}
			else if (builder.Length > 0 && builder[^1] != '-' && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
			{
				builder.Append('-');
			}
		}
		var slug = builder.ToString().Trim('-');
		return slug.Length > 64 ? slug[..64].TrimEnd('-') : slug;
	}

	[GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
	private static partial Regex Pattern();
}
