using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Braggarr.Blog.Theme;

/// <summary>What every page of the theme knows: the site, the build, and how this page presents itself to cards and search.</summary>
public sealed record LayoutModel(SitePlan Plan, string Title, string Path)
{
	public SiteConfig Config => Plan.Site.Config;

	public Site Site => Plan.Site;

	public string? Description { get; init; }

	/// <summary>A picture for cards: a full address.</summary>
	public string? Image { get; init; }

	public string? ImageAlt { get; init; }

	/// <summary><c>article</c> for a post, <c>website</c> for the rest.</summary>
	public string Kind { get; init; } = "website";

	/// <summary>Structured data for search engines, as JSON.</summary>
	public string? JsonLd { get; init; }

	/// <summary>Where the page says it really lives, when that is not here.</summary>
	public string? Canonical { get; init; }

	/// <summary>The theme's stylesheet, named by its content so a changed theme is never served from a stale cache.</summary>
	public string Stylesheet { get; init; } = "/_theme/style.css";

	/// <summary>Whether the site has <c>static/custom.css</c>, which then comes after the theme's.</summary>
	public bool HasCustomCss => Site.Files.Contains("custom.css");

	/// <summary>A path on the site as a link, under the site's own path when it does not live at its domain's root.</summary>
	public string Href(string path) => Config.Url.AbsolutePath.TrimEnd('/') + path;

	public string Absolute(string path) => Config.Absolute(path.TrimStart('/'));

	/// <summary>A file in the site (the avatar, a cover) as a link: as it is when it is a full address.</summary>
	public string FileHref(string file) => SiteConfigRules.IsHttps(file) ? file : Href("/" + file);

	public string TitleOfPage => Path == "/" ? Config.Title : $"{Title} · {Config.Title}";

	public static string Day(DateTimeOffset date) => date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

	public static string Iso(DateTimeOffset date) => date.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}

public sealed record PostModel(LayoutModel Layout, BlogPost Post, Rendered Body, BlogPost? Newer, BlogPost? Older)
{
	public string PostHref(BlogPost post) => Layout.Href(post.Permalink(Layout.Config));

	/// <summary>The cover as a link: beside the post when it is relative, else as written.</summary>
	public string? CoverHref => Post.Cover is { } cover ? SiteConfigRules.IsHttps(cover) ? cover : cover.StartsWith('/') ? Layout.Href(cover) : PostHref(Post) + cover : null;
}

/// <summary>A post in a list: the index, a tag's page.</summary>
public sealed record CardModel(BlogPost Post, string Href);

public sealed record IndexModel(LayoutModel Layout, ValueList<BlogPost> Posts, int Number, int Pages)
{
	public string PostHref(BlogPost post) => Layout.Href(post.Permalink(Layout.Config));
}

public sealed record TagModel(LayoutModel Layout, TagPage Tag)
{
	public string PostHref(BlogPost post) => Layout.Href(post.Permalink(Layout.Config));
}

public sealed record ArchiveModel(LayoutModel Layout)
{
	public string PostHref(BlogPost post) => Layout.Href(post.Permalink(Layout.Config));
}

public sealed record PageModel(LayoutModel Layout, Page Page, Rendered Body);

/// <summary>A page at an old path that sends readers on to <paramref name="To"/>, a full address.</summary>
public sealed record RedirectModel(string To, string Title);

/// <summary>Structured data (schema.org) for a post, written with <see cref="Utf8JsonWriter"/> so it needs no reflection.</summary>
internal static class JsonLd
{
	public static string For(BlogPost post, SiteConfig config, string description, string? image)
	{
		using var stream = new MemoryStream();
		using (var json = new Utf8JsonWriter(stream))
		{
			json.WriteStartObject();
			json.WriteString("@context", "https://schema.org");
			json.WriteString("@type", "BlogPosting");
			json.WriteString("headline", post.Title);
			json.WriteString("description", description);
			json.WriteString("datePublished", LayoutModel.Iso(post.Date));
			json.WriteString("url", config.Absolute(post.Permalink(config).TrimStart('/')));
			if (image is not null)
			{
				json.WriteString("image", image);
			}
			json.WritePropertyName("author");
			json.WriteStartObject();
			json.WriteString("@type", "Person");
			json.WriteString("name", config.Author.Name);
			json.WriteEndObject();
			if (post.Tags.Count > 0)
			{
				json.WriteString("keywords", string.Join(", ", post.Tags));
			}
			json.WriteEndObject();
		}
		// The writer's default encoder escapes < and >, so a title can never close the <script> it goes in.
		return Encoding.UTF8.GetString(stream.ToArray());
	}
}
