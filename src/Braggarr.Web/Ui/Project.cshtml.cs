using Braggarr.Domain;
using Braggarr.Services;
using Braggarr.Web.Settings;

namespace Braggarr.Web.Ui;

/// <summary>
/// One project: how its releases are watched, its brief (what drafting writes from and the judge reads, with Claude's
/// suggestion to start from), and its campaigns. A project nobody watches, named by a hand-made campaign, has a page too.
/// </summary>
public sealed partial class ProjectModel(BriefService briefs, DraftingService drafting, SettingsStore settings, CampaignService campaigns, SchedulingService scheduling)
	: UiPage
{
	public string Project { get; private set; } = "";

	public ProjectBrief Brief { get; private set; } = new("", "", "", "");

	/// <summary>The watched project's settings; null when its releases are not watched.</summary>
	public ProjectSettings? Watched => settings.Current.Projects.FirstOrDefault(p => ProjectBrief.Slug(p.Name) == ProjectBrief.Slug(Project));

	public bool CanSuggest => drafting.IsAvailable && Watched is not null;

	public bool IsSuggestion { get; private set; }

	public string? Message { get; private set; }

	public IReadOnlyList<string> Problems { get; private set; } = [];

	/// <summary>Its open proposals, then its campaigns on main, newest first.</summary>
	public IReadOnlyList<(Campaign Campaign, string Href)> Campaigns { get; private set; } = [];

	public async Task<IResult> OnGetAsync(string project, CancellationToken ct) =>
		await LoadAsync(project, ct) ? Page() : NotFound();

	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) => (method, handler) switch
	{
		("GET", null) => OnGetAsync(form.Text("project")!, ct),
		("POST", "savebrief") => OnPostSaveBriefAsync(form.Text("project")!, form.Text("tldr"), form.Text("apart"), form.Text("audience"), form.Text("avoid"), form.Text("links"), ct),
		("POST", "suggest") => OnPostSuggestAsync(form.Text("project")!, ct),
		("POST", "watching") => OnPostWatchingAsync(form.Text("project")!, form.EnumList<Network>("networks"), form.Bool("prereleases"), form.Bool("askClaude"), ct),
		("POST", "watch") => OnPostWatchAsync(form.Text("project")!, form.Text("repository"), ct),
		("POST", "unwatch") => Task.FromResult(OnPostUnwatch(form.Text("repository")!)),
		_ => null
	};

	protected override RazorSlices.RazorSlice Slice() => Braggarr.Web.Ui.Project.Create(this);

	private async Task<bool> LoadAsync(string project, CancellationToken ct, ProjectBrief? shown = null)
	{
		if (ProjectBrief.Slug(project ?? "").Length == 0)
		{
			return false;
		}
		// The watched project's own spelling of its name, when there is one.
		Project = settings.Current.Projects.FirstOrDefault(p => ProjectBrief.Slug(p.Name) == ProjectBrief.Slug(project!))?.Name ?? project!.Trim();
		Brief = shown ?? await briefs.ForAsync(Project, ct) ?? new ProjectBrief(Project, "", "", "");
		var slug = ProjectBrief.Slug(Project);
		Campaigns =
		[
			.. (await campaigns.ProposalsAsync(ct)).Where(p => ProjectBrief.Slug(p.Campaign.Project) == slug)
				.Select(p => (p.Campaign, $"/Campaigns/Proposal?number={p.Number}")),
			.. scheduling.Current.Campaigns.Where(c => ProjectBrief.Slug(c.Project) == slug)
				.OrderByDescending(c => c.Posts.Count == 0 ? DateTimeOffset.MinValue : c.Posts.Max(p => p.At))
				.Select(c => (c, $"/Campaigns/Detail?slug={c.Slug}"))
		];
		return true;
	}

	public async Task<IResult> OnPostSaveBriefAsync(string project, string? tldr, string? apart, string? audience, string? avoid, string? links, CancellationToken ct)
	{
		var typed = new ProjectBrief(project.Trim(), tldr ?? "", apart ?? "", audience ?? "") { Avoid = [.. Lines(avoid)], Links = [.. Lines(links).Select(ReadLink)] };
		switch (await briefs.SaveAsync(typed, Actor.Owner(User.Identity!.Name!), ct))
		{
			case Outcome<ProjectBrief>.Done(var kept):
				_ = await LoadAsync(project, ct, kept);
				Message = "Saved the brief to the posts repo. Drafting and the judge use it from now on.";
				break;
			case Outcome<ProjectBrief>.Refused(var reasons):
				_ = await LoadAsync(project, ct, typed);
				Problems = reasons;
				break;
		}
		return Page();
	}

	public async Task<IResult> OnPostSuggestAsync(string project, CancellationToken ct)
	{
		_ = await LoadAsync(project, ct);
		if (Watched is not { } watched)
		{
			Problems = ["Claude reads a project's repository: watch its releases first."];
			return Page();
		}
		switch (await briefs.SuggestAsync(watched.Repository, Project, ct))
		{
			case Outcome<ProjectBrief>.Done(var suggested):
				Brief = suggested;
				IsSuggestion = true;
				Message = "Claude's suggestion, from the repository. Nothing is saved yet: change what is wrong, then save.";
				break;
			case Outcome<ProjectBrief>.Refused(var reasons):
				Problems = reasons;
				break;
		}
		return Page();
	}

	public async Task<IResult> OnPostWatchingAsync(string project, List<Network>? networks, bool prereleases, bool askClaude, CancellationToken ct)
	{
		_ = await LoadAsync(project, ct);
		if (Watched is not { } watched)
		{
			return NotFound();
		}
		if (networks is not { Count: > 0 })
		{
			Problems = ["Choose at least one network to announce its releases on."];
			return Page();
		}
		var updated = watched with { Networks = [.. networks.Distinct().Select(n => n.Slug())], IncludePrereleases = prereleases, AskClaude = askClaude };
		_ = settings.Update(s => s with { Projects = [.. s.Projects.Select(p => p.Repository == watched.Repository ? updated : p)] });
		Message = "Saved. The next release is announced on these networks.";
		return Page();
	}

	public async Task<IResult> OnPostWatchAsync(string project, string? repository, CancellationToken ct)
	{
		_ = await LoadAsync(project, ct);
		var repo = (repository ?? "").Trim().Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
		if (!RepositoryName().IsMatch(repo))
		{
			Problems = ["Name the repository as owner/name, for example nullean/curb."];
			return Page();
		}
		// The blog is chosen, not given: a project's releases go to its blog only once the owner ticks it.
		var watched = new ProjectSettings(repo, Project, [.. Networks.All.Where(n => !n.IsManual() && n != Network.Blog).Select(n => n.Slug())]);
		_ = settings.Update(s => s with { Projects = [.. s.Projects.Where(p => p.Repository != repo), watched] });
		Message = $"Watching {repo}. Its current release is remembered on the first check; the next one becomes a proposal.";
		return Page();
	}

	public IResult OnPostUnwatch(string repository)
	{
		_ = settings.Update(s => s with { Projects = [.. s.Projects.Where(p => p.Repository != repository)] });
		return Redirect("/Projects");
	}

	[System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
	private static partial System.Text.RegularExpressions.Regex RepositoryName();

	public static string LinkLine(BriefLink link) => $"{link.Label}: {link.Url}";

	public static string Href(string project) => $"/Projects/{Uri.EscapeDataString(project)}";

	private static IEnumerable<string> Lines(string? text) =>
		(text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

	/// <summary>"Docs: https://…", or a bare address, labelled by its host.</summary>
	private static BriefLink ReadLink(string line)
	{
		var at = line.IndexOf("http", StringComparison.OrdinalIgnoreCase);
		if (at < 0)
		{
			return new BriefLink(line, "");
		}
		var url = line[at..].Trim();
		var label = line[..at].Trim().TrimEnd(':', '-', '=').Trim();
		return new BriefLink(label.Length > 0 ? label : Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url, url);
	}
}
