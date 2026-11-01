using System.Globalization;

namespace Braggarr.Blog;

/// <summary>A post as Braggarr puts it on a site: what goes in its front matter.</summary>
public sealed record PublishedPost(string Title, DateTimeOffset Date, string Description, IReadOnlyList<string> Tags, string Slug)
{
	public string? Cover { get; init; }

	public string? CoverAlt { get; init; }

	public string? Project { get; init; }

	public string? Campaign { get; init; }
}

/// <summary>
/// What Braggarr needs to put a post on a site: the site's config, the folder and file a post is written as, and the address
/// it will have once the site has built. The address is the builder's own permalink rule, so it is known before the commit.
/// </summary>
public static class Publishing
{
	public const string ConfigFile = SiteReader.ConfigFile;

	/// <summary>The site's <c>braggarr.yml</c>; null, with what is wrong, when it does not read.</summary>
	public static SiteConfig? ReadConfig(string yaml, out IReadOnlyList<Diagnostic> problems)
	{
		var diagnostics = new List<Diagnostic>();
		var config = SiteReader.ReadConfig(yaml, diagnostics);
		problems = diagnostics;
		return diagnostics.Any(d => d.Level == DiagnosticLevel.Error) ? null : config;
	}

	/// <summary>The post's folder among the site's posts, as <c>braggarr blog new</c> names one: its date, then its slug.</summary>
	public static string Folder(PublishedPost post) => $"{post.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}-{post.Slug}";

	/// <summary>Where the post will be, by the site's permalink rule, under <paramref name="root"/> (the site's url, unless overridden).</summary>
	public static string Address(SiteConfig config, PublishedPost post, Uri? root = null) =>
		new Uri(root ?? config.Url, config.Permalink.For(post.Slug, post.Date).TrimStart('/')).ToString();

	/// <summary>The post's <c>index.md</c>: its front matter, with the slug always written, then the markdown.</summary>
	public static string Write(PublishedPost post, string markdown)
	{
		var front = SiteYaml.Write(new PublishedFront
		{
			Title = post.Title,
			Date = post.Date.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
			Description = post.Description,
			Tags = post.Tags.Count == 0 ? null : [.. post.Tags],
			Slug = post.Slug,
			Cover = post.Cover,
			CoverAlt = post.CoverAlt,
			Project = post.Project,
			Campaign = post.Campaign
		});
		return $"---\n{front.TrimEnd('\n')}\n---\n\n{markdown.Trim('\n')}\n";
	}

	public const string ProjectsFile = SiteReader.ProjectsFile;

	public const string IntroFile = SiteReader.IntroFile;

	public const string ProfilesFile = SiteReader.ProfilesFile;

	/// <summary><c>_braggarr/projects.yml</c>: the groups in their order, each project as the site reads it back.</summary>
	public static string WriteProjects(IEnumerable<ProjectGroup> groups) => SiteYaml.Write(new ProjectsDocument
	{
		Groups =
		[
			.. groups.Select(g => new GroupDocument
			{
				Title = g.Title,
				Description = g.Description,
				Projects =
				[
					.. g.Projects.Select(p => new ProjectDocument
					{
						Name = p.Name,
						Repo = p.Repository,
						Url = p.Url,
						Tldr = p.Tldr,
						Links = p.Links.Count == 0 ? null : [.. p.Links.Select(l => new LinkDocument { Title = l.Title, Url = l.Url })],
						Release = p.Release is { } r
							? new ReleaseDocument { Tag = r.Tag, Date = r.Date.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), Url = r.Url }
							: null,
						Stars = p.Stars,
						Language = p.Language,
						Icon = p.Icon,
						Image = p.Image
					})
				]
			})
		]
	});

	/// <summary><c>_braggarr/profiles.yml</c>: the owner's profiles, linked from the author card.</summary>
	public static string WriteProfiles(IEnumerable<Profile> profiles) => SiteYaml.Write(new ProfilesDocument
	{
		Profiles = [.. profiles.Select(p => new ProfileDocument { Network = p.Network, Name = p.Name, Url = p.Url })]
	});
}
