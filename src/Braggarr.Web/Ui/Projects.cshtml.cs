using Braggarr.Domain;
using Braggarr.Services;
using Braggarr.Web.Hosting;
using Braggarr.Web.Settings;

namespace Braggarr.Web.Ui;

/// <summary>The projects Braggarr watches for releases, and adding one (with an introduction campaign, if wanted).</summary>
public sealed partial class ProjectsModel(SettingsStore settings, ReleaseWatcher releases, Introducer introducer, BackgroundWork work, Clock clock, BriefService briefs) : UiPage
{
	public IReadOnlyList<ProjectSettings> Projects => settings.Current.Projects;

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

	[System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
	private static partial System.Text.RegularExpressions.Regex RepositoryName();

	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) => (method, handler) switch
	{
		("GET", null) => Shown(),
		("POST", "watch") => Task.FromResult(OnPostWatch(form.Text("repository"), form.Text("name"), form.EnumList<Network>("networks"), form.Bool("prereleases"), form.Bool("askClaude"), form.Bool("introduce"))),
		("POST", "unwatch") => Task.FromResult(OnPostUnwatch(form.Text("repository")!)),
		("POST", "proposeanyway") => OnPostProposeAnywayAsync(form.Text("repository")!, form.Text("tag")!, ct),
		("POST", "check") => OnPostCheckAsync(ct),
		_ => null
	};

	protected override RazorSlices.RazorSlice Slice() => Braggarr.Web.Ui.Projects.Create(this);
}
