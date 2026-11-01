using Braggarr.Domain;
using Braggarr.Services;
using Braggarr.Web.Hosting;
using Braggarr.Web.Settings;

namespace Braggarr.Web.Ui;

/// <summary>The projects Braggarr watches for releases, and adding one (with an introduction campaign, if wanted).</summary>
public sealed partial class ProjectsModel(
	SettingsStore settings, ReleaseWatcher releases, Introducer introducer, BackgroundWork work, Clock clock, BriefService briefs, AboutService about, INotifier notifier
) : UiPage
{
	public IReadOnlyList<ProjectSettings> Projects => settings.Current.Projects;

	/// <summary>The about page's groups, in the order the page shows them.</summary>
	public IReadOnlyList<ProjectGroupSettings> Groups => settings.Current.ProjectGroups;

	public const int GroupTitleLimit = 60;

	public Clock Clock => clock;

	/// <summary>The briefs in the posts repo, for each project's TL;DR.</summary>
	public Task<IReadOnlyList<ProjectBrief>> BriefsAsync() => briefs.AllAsync(HttpContext.RequestAborted);

	public string? Message { get; private set; }

	public string? Problem { get; private set; }

	public IResult OnPostWatch(string? repository, string? name, List<Network>? networks, bool prereleases, bool askClaude, bool introduce)
	{
		var repo = (repository ?? "").Trim().Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
		if (!RepositoryName().IsMatch(repo))
		{
			Problem = "Name the repository as owner/name, for example nullean/curb.";
			return Page();
		}
		if (networks is not { Count: > 0 })
		{
			Problem = "Choose at least one network to announce its releases on.";
			return Page();
		}
		var project = new ProjectSettings(repo, string.IsNullOrWhiteSpace(name) ? repo[(repo.IndexOf('/') + 1)..] : name.Trim(), [.. networks.Select(n => n.Slug())], null, prereleases, askClaude);
		_ = settings.Update(s => s with { Projects = [.. s.Projects.Where(p => p.Repository != repo), project] });
		Message = $"Watching {repo}. Its current release is remembered on the first check; the next one becomes a proposal.";
		if (introduce)
		{
			var on = networks.ToList();
			work.Enqueue($"Introducing {repo}", ct => introducer.ProposeAsync(repo, project.Name, on, ct));
			Message += " An introduction campaign is being planned; you get a notice when it is ready to review.";
		}
		return Page();
	}

	public IResult OnPostUnwatch(string repository)
	{
		_ = settings.Update(s => s with { Projects = [.. s.Projects.Where(p => p.Repository != repository)] });
		Message = $"No longer watching {repository}.";
		return Page();
	}

	public async Task<IResult> OnPostProposeAnywayAsync(string repository, string tag, CancellationToken ct)
	{
		switch (await releases.ProposeAnywayAsync(repository, tag, ct))
		{
			case Outcome<Proposal>.Done(var proposal):
				_ = settings.Update(s => s with { Projects = [.. s.Projects.Select(p => p.Repository == repository ? p with { LastSkipped = null } : p)] });
				return Redirect($"/Campaigns/Proposal?number={proposal.Number}");
			case Outcome<Proposal>.Refused(var reasons):
				Problem = string.Join(" ", reasons);
				break;
		}
		return Page();
	}

	public async Task<IResult> OnPostCheckAsync(CancellationToken ct)
	{
		var proposed = await releases.CheckAsync(ct);
		Message = proposed.Count == 0 ? "No new releases." : $"Proposed: {string.Join(", ", proposed.Select(p => p.Campaign.Title))}.";
		return Page();
	}

	/// <summary>Adds a group at the end, or changes the description of the group with that title.</summary>
	public IResult OnPostGroup(string? title, string? description)
	{
		var name = (title ?? "").Trim();
		if (name.Length is 0 or > GroupTitleLimit)
		{
			Problem = $"Give the group a title of at most {GroupTitleLimit} characters.";
			return Page();
		}
		var line = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
		var known = settings.Current.ProjectGroups.Any(g => Same(g.Title, name));
		_ = settings.Update(s => s with
		{
			ProjectGroups = known
				? [.. s.ProjectGroups.Select(g => Same(g.Title, name) ? g with { Description = line } : g)]
				: [.. s.ProjectGroups, new ProjectGroupSettings(name, line)]
		});
		Message = known ? $"{name} is described anew." : $"{name} is added: put projects in it below.";
		return Page();
	}

	/// <summary>Moves a group one place up or down on the about page.</summary>
	public IResult OnPostMoveGroup(string title, string? direction)
	{
		var groups = settings.Current.ProjectGroups.ToList();
		var at = groups.FindIndex(g => Same(g.Title, title));
		var to = direction == "up" ? at - 1 : at + 1;
		if (at >= 0 && to >= 0 && to < groups.Count)
		{
			(groups[at], groups[to]) = (groups[to], groups[at]);
			_ = settings.Update(s => s with { ProjectGroups = groups });
		}
		return Page();
	}

	/// <summary>Removes a group; its projects are in none, so the about page leaves them off until they are put in another.</summary>
	public IResult OnPostRemoveGroup(string title)
	{
		_ = settings.Update(s => s with
		{
			ProjectGroups = [.. s.ProjectGroups.Where(g => !Same(g.Title, title))],
			Projects = [.. s.Projects.Select(p => p.Group is { } g && Same(g, title) ? p with { Group = null } : p)]
		});
		Message = $"{title} is removed. Its projects are in no group now.";
		return Page();
	}

	/// <summary>Puts a project in a group, or in none.</summary>
	public IResult OnPostAssign(string repository, string? group)
	{
		var into = string.IsNullOrWhiteSpace(group) ? null : settings.Current.ProjectGroups.FirstOrDefault(g => Same(g.Title, group))?.Title;
		if (!string.IsNullOrWhiteSpace(group) && into is null)
		{
			Problem = $"There is no group {group}.";
			return Page();
		}
		_ = settings.Update(s => s with { Projects = [.. s.Projects.Select(p => p.Repository == repository ? p with { Group = into } : p)] });
		return Page();
	}

	/// <summary>Puts the about page together in the background and proposes it to the blog; a notice links the pull request.</summary>
	public IResult OnPostAbout(bool redraft)
	{
		work.Enqueue("Proposing the about page", async ct =>
		{
			switch (await about.ProposeAsync(redraft, ct))
			{
				case Outcome<AboutProposal?>.Refused(var reasons):
					await notifier.NotifyAsync(new Notice(NoticeLevel.Problem, "The about page was not proposed", string.Join(" ", reasons)), ct);
					break;
				case Outcome<AboutProposal?>.Done(null):
					await notifier.NotifyAsync(new Notice(NoticeLevel.Info, "The about page is up to date", "The blog already shows exactly this."), ct);
					break;
			}
		});
		Message = "The about page is being put together. A notice links its pull request on the blog when it is ready.";
		return Page();
	}

	private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

	[System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
	private static partial System.Text.RegularExpressions.Regex RepositoryName();

	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) => (method, handler) switch
	{
		("GET", null) => Shown(),
		("POST", "watch") => Task.FromResult(OnPostWatch(form.Text("repository"), form.Text("name"), form.EnumList<Network>("networks"), form.Bool("prereleases"), form.Bool("askClaude"), form.Bool("introduce"))),
		("POST", "unwatch") => Task.FromResult(OnPostUnwatch(form.Text("repository")!)),
		("POST", "proposeanyway") => OnPostProposeAnywayAsync(form.Text("repository")!, form.Text("tag")!, ct),
		("POST", "check") => OnPostCheckAsync(ct),
		("POST", "group") => Task.FromResult(OnPostGroup(form.Text("title"), form.Text("description"))),
		("POST", "movegroup") => Task.FromResult(OnPostMoveGroup(form.Text("title") ?? "", form.Text("direction"))),
		("POST", "removegroup") => Task.FromResult(OnPostRemoveGroup(form.Text("title") ?? "")),
		("POST", "assign") => Task.FromResult(OnPostAssign(form.Text("repository") ?? "", form.Text("group"))),
		("POST", "about") => Task.FromResult(OnPostAbout(form.Bool("redraft"))),
		_ => null
	};

	protected override RazorSlices.RazorSlice Slice() => Braggarr.Web.Ui.Projects.Create(this);
}
