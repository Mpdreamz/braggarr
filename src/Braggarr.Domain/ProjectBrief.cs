using Nullean.Entities;

namespace Braggarr.Domain;

/// <summary>
/// What a project is, in the owner's words: the one line that sums it up, what sets it apart, who it is for, what never to
/// say about it, and the links posts should use. Drafting writes from it and the judge reads it; it lives in the posts repo
/// as <c>projects/&lt;slug&gt;.md</c>.
/// </summary>
public sealed record ProjectBrief(string Project, string Tldr, string Apart, string Audience)
{
	/// <summary>What never to say about it: overclaims, names it no longer has, comparisons the owner does not make.</summary>
	public ValueList<string> Avoid { get; init; } = [];

	/// <summary>The links posts use: docs, install, a demo. Labelled, so drafting picks the right one.</summary>
	public ValueList<BriefLink> Links { get; init; } = [];

	/// <summary>The file name the brief is kept under: the project's name in lowercase, anything but letters and digits a dash.</summary>
	public static string Slug(string project) =>
		string.Join('-', new string([.. project.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ')])
			.Split(' ', StringSplitOptions.RemoveEmptyEntries));

	public const int TldrLimit = 160;
}

public sealed record BriefLink(string Label, string Url)
{
	public bool IsWeb => Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
}

/// <summary>What a brief must say before it is kept.</summary>
[Constraints<ProjectBrief>]
public static partial class ProjectBriefRules
{
	public static readonly Constraint<ProjectBrief> NamesItsProject = Constraints<ProjectBrief>.Always(
		b => ProjectBrief.Slug(b.Project).Length > 0,
		"A brief belongs to a project with a name of letters or digits."
	);

	public static readonly Constraint<ProjectBrief> SumsItUp = Constraints<ProjectBrief>.Always(
		b => !string.IsNullOrWhiteSpace(b.Tldr) && b.Tldr.Trim().Length <= ProjectBrief.TldrLimit && !b.Tldr.Contains('\n'),
		$"The TL;DR is one line of at most {ProjectBrief.TldrLimit} characters."
	);

	public static readonly Constraint<ProjectBrief> SaysWhatSetsItApart = Constraints<ProjectBrief>.Always(
		b => !string.IsNullOrWhiteSpace(b.Apart),
		"Say what sets it apart: the one thing that makes it worth a look."
	);

	public static readonly Constraint<ProjectBrief> LinksAreLinks = Constraints<ProjectBrief>.Always(
		b => b.Links.All(l => !string.IsNullOrWhiteSpace(l.Label) && l.IsWeb),
		"Every link has a label and an http or https address."
	);
}
