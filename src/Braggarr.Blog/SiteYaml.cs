using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SharpYaml;
using SharpYaml.Serialization;

namespace Braggarr.Blog;

/// <summary>Front matter and YAML files as they are written, through SharpYaml's generated (AOT-safe) serializers.</summary>
internal static partial class SiteYaml
{
	public static ConfigDocument Config(string yaml) => YamlSerializer.Deserialize(yaml, SiteYamlContext.Default.ConfigDocument) ?? new ConfigDocument();

	public static FrontMatter Front(string yaml) => YamlSerializer.Deserialize(yaml, SiteYamlContext.Default.FrontMatter) ?? new FrontMatter();

	public static ProfilesDocument Profiles(string yaml) => YamlSerializer.Deserialize(yaml, SiteYamlContext.Default.ProfilesDocument) ?? new ProfilesDocument();

	public static string Write(NewPost front) => YamlSerializer.Serialize(front, SiteYamlContext.Default.NewPost);

	public static string Write(ProfilesDocument profiles) => YamlSerializer.Serialize(profiles, SiteYamlContext.Default.ProfilesDocument);

	public static string Write(PublishedFront front) => YamlSerializer.Serialize(front, SiteYamlContext.Default.PublishedFront);

	/// <summary>The top-level keys of a YAML mapping, as written: what a misspelt key is found with.</summary>
	public static IEnumerable<string> Keys(string yaml) => TopLevelKey().Matches(yaml).Select(m => m.Groups["key"].Value);

	/// <summary>Front matter and body. A file with no front matter is all body.</summary>
	public static (string Front, string Body) Split(string text)
	{
		var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal);
		if (!normalised.StartsWith("---\n", StringComparison.Ordinal))
		{
			return ("", normalised);
		}
		var end = normalised.IndexOf("\n---\n", 3, StringComparison.Ordinal);
		if (end < 0)
		{
			end = normalised.EndsWith("\n---", StringComparison.Ordinal) ? normalised.Length - 4 : throw new FormatException("The front matter has no closing ---.");
		}
		var front = normalised[4..(end + 1)];
		var body = end + 5 <= normalised.Length ? normalised[(end + 5)..] : "";
		return (front, body.TrimStart('\n'));
	}

	public static string Join(string front, string body) => $"---\n{front.TrimEnd('\n')}\n---\n\n{body.TrimEnd('\n')}\n";

	[GeneratedRegex(@"^(?<key>[A-Za-z_][A-Za-z0-9_-]*)\s*:", RegexOptions.Multiline)]
	private static partial Regex TopLevelKey();
}

internal sealed class ConfigDocument
{
	public string? Title { get; set; }
	public string? Description { get; set; }
	public string? Url { get; set; }
	public string? Language { get; set; }
	public AuthorDocument? Author { get; set; }
	public string? Permalink { get; set; }
	public List<NavDocument>? Nav { get; set; }
	public string? Accent { get; set; }
	public CommentsDocument? Comments { get; set; }
	public string? Feed { get; set; }
	public int? PostsPerPage { get; set; }
	public int? FeedSize { get; set; }
}

internal sealed class AuthorDocument
{
	public string? Name { get; set; }
	public string? Avatar { get; set; }
	public string? Bio { get; set; }
}

internal sealed class NavDocument
{
	public string? Title { get; set; }
	public string? Href { get; set; }
	public string? Page { get; set; }
}

internal sealed class CommentsDocument
{
	public GiscusDocument? Giscus { get; set; }
}

internal sealed class GiscusDocument
{
	public string? Repo { get; set; }
	public string? RepoId { get; set; }
	public string? Category { get; set; }
	public string? CategoryId { get; set; }
	public string? Mapping { get; set; }
}

/// <summary>A post's or a page's front matter.</summary>
public sealed class FrontMatter
{
	public string? Title { get; set; }
	public string? Date { get; set; }
	public string? Description { get; set; }
	public List<string>? Tags { get; set; }
	public bool? Draft { get; set; }
	public string? Slug { get; set; }
	public string? Cover { get; set; }
	public string? CoverAlt { get; set; }
	public List<string>? Aliases { get; set; }
	public string? Canonical { get; set; }
	public string? Project { get; set; }
	public string? Campaign { get; set; }
}

/// <summary>The front matter <c>braggarr blog new</c> writes: the strings only.</summary>
internal sealed class NewPost
{
	public string? Title { get; set; }
	public string? Date { get; set; }
}

/// <summary>The front matter of a post Braggarr publishes: strings and lists only, so nothing unset is written.</summary>
internal sealed class PublishedFront
{
	public string? Title { get; set; }
	public string? Date { get; set; }
	public string? Description { get; set; }
	public List<string>? Tags { get; set; }
	public string? Slug { get; set; }
	public string? Cover { get; set; }
	public string? CoverAlt { get; set; }
	public string? Project { get; set; }
	public string? Campaign { get; set; }
}

/// <summary><c>_braggarr/profiles.yml</c>: the owner's profiles, written by Braggarr.</summary>
public sealed class ProfilesDocument
{
	public List<ProfileDocument>? Profiles { get; set; }
}

public sealed class ProfileDocument
{
	public string? Network { get; set; }
	public string? Name { get; set; }
	public string? Url { get; set; }
}

[YamlSerializable(typeof(ConfigDocument))]
[YamlSerializable(typeof(FrontMatter))]
[YamlSerializable(typeof(ProfilesDocument))]
[YamlSerializable(typeof(NewPost))]
[YamlSerializable(typeof(PublishedFront))]
[YamlSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = YamlIgnoreCondition.WhenWritingNull)]
internal sealed partial class SiteYamlContext : YamlSerializerContext;
