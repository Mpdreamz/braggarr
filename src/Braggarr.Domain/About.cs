using Nullean.Entities;

namespace Braggarr.Domain;

/// <summary>A group the owner sorts projects into on a blog's about page: a title, and a line on what the group is.</summary>
public sealed record AboutGroup(string Title)
{
	public string? Description { get; init; }
}

/// <summary>
/// A watched project as an about page shows it: its line from the brief, its links, its latest release, its stars and
/// language as GitHub says, and its pictures, saved beside the page (<c>media/curb-icon.png</c>).
/// </summary>
public sealed record AboutProject(string Name, string Repository, ProjectVisibility Visibility)
{
	public string? Tldr { get; init; }

	/// <summary>Where its name links: its website, else its repository.</summary>
	public string? Url { get; init; }

	public ValueList<BriefLink> Links { get; init; } = [];

	public AboutRelease? Release { get; init; }

	public int? Stars { get; init; }

	public string? Language { get; init; }

	public string? Icon { get; init; }

	public string? Image { get; init; }

	/// <summary>The folder its pictures are saved in, beside the page's files.</summary>
	public const string MediaFolder = "media/";
}

public sealed record AboutRelease(string Tag, DateTimeOffset Date, string Url);

/// <summary>A profile of the owner's, linked from the page with <c>rel="me"</c>.</summary>
public sealed record AboutProfile(Network Network, string Url)
{
	public string? Name { get; init; }
}

/// <summary>A group as the page shows it: the group, and its projects in the owner's order.</summary>
public sealed record AboutSection(AboutGroup Group, ValueList<AboutProject> Projects);

/// <summary>
/// What Braggarr puts on a blog's about page: the owner's groups with their public projects, an intro, and the owner's
/// profiles. It goes to the site as a pull request, which the owner merges.
/// </summary>
public sealed record AboutPage(ValueList<AboutSection> Sections)
{
	public string? Intro { get; init; }

	public ValueList<AboutProfile> Profiles { get; init; } = [];

	public IEnumerable<AboutProject> Projects => Sections.SelectMany(s => s.Projects);

	/// <summary>
	/// The page from the owner's groups, in their order, and the watched projects with the group each is in, in the order they
	/// are watched. Left out: a project that is not public as far as GitHub says (private, or it could not say), one in no
	/// group or in a group that is gone, a second project with the same repository, and a group left with no project.
	/// </summary>
	public static AboutPage Lay(IReadOnlyList<AboutGroup> groups, IReadOnlyList<(AboutProject Project, string? Group)> projects)
	{
		var shown = projects
			.Where(p => p.Project.Visibility == ProjectVisibility.Public && p.Group is not null)
			.DistinctBy(p => p.Project.Repository, StringComparer.OrdinalIgnoreCase)
			.ToList();
		var sections = groups
			.Where(g => !string.IsNullOrWhiteSpace(g.Title))
			.DistinctBy(g => g.Title.Trim(), StringComparer.OrdinalIgnoreCase)
			.Select(g => new AboutSection(g, [.. shown.Where(p => string.Equals(p.Group!.Trim(), g.Title.Trim(), StringComparison.OrdinalIgnoreCase)).Select(p => p.Project)]))
			.Where(s => s.Projects.Count > 0);
		return new AboutPage([.. sections]);
	}

	/// <summary>A picture's name among the page's files: <c>media/</c>, then one safe file name.</summary>
	public static bool IsMedia(string path) =>
		path.StartsWith(AboutProject.MediaFolder, StringComparison.Ordinal)
		&& path[AboutProject.MediaFolder.Length..] is { Length: > 0 and <= 100 } name
		&& name[0] != '.'
		&& name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

/// <summary>What an about page may say before it goes to a site.</summary>
[Constraints<AboutPage>]
public static partial class AboutPageRules
{
	/// <summary>Braggarr never mentions a project that is not public: the page is published.</summary>
	public static readonly Constraint<AboutPage> OnlyPublicProjects = Constraints<AboutPage>.Always(
		p => p.Projects.All(project => project.Visibility == ProjectVisibility.Public),
		"Only public projects are shown: GitHub says each is public."
	);

	public static readonly Constraint<AboutPage> EachProjectOnce = Constraints<AboutPage>.Always(
		p => p.Projects.Select(project => project.Repository).Distinct(StringComparer.OrdinalIgnoreCase).Count() == p.Projects.Count(),
		"Each project is shown once, in one group."
	);

	public static readonly Constraint<AboutPage> GroupsAreDistinct = Constraints<AboutPage>.Always(
		p => p.Sections.All(s => !string.IsNullOrWhiteSpace(s.Group.Title))
			&& p.Sections.Select(s => s.Group.Title.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == p.Sections.Count,
		"Each group has a title of its own."
	);

	public static readonly Constraint<AboutPage> NoEmptyGroups = Constraints<AboutPage>.Always(
		p => p.Sections.All(s => s.Projects.Count > 0),
		"A group without a project is left off."
	);

	public static readonly Constraint<AboutPage> PicturesAreSavedBesideThePage = Constraints<AboutPage>.Always(
		p => p.Projects.SelectMany(project => new[] { project.Icon, project.Image }).OfType<string>().All(AboutPage.IsMedia),
		"A project's pictures are files in media/, with plain names."
	);

	public static readonly Constraint<AboutPage> LinksAreHttps = Constraints<AboutPage>.Always(
		p => p.Profiles.All(profile => IsHttps(profile.Url))
			&& p.Projects.All(project => (project.Url is null || IsHttps(project.Url))
				&& project.Links.All(l => !string.IsNullOrWhiteSpace(l.Label) && IsHttps(l.Url))
				&& (project.Release is null || IsHttps(project.Release.Url))),
		"Profiles, project sites, links and releases are https addresses."
	);

	private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https";
}
