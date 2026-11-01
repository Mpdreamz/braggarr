using Braggarr.Domain;
using Microsoft.Extensions.Logging;

namespace Braggarr.Services;

/// <summary>
/// A blog's about page from what Braggarr knows: the owner's groups, the watched projects in them that GitHub says are public,
/// each with its brief's line and links, its latest release, its stars, language and pictures; an intro Claude drafts from the
/// briefs; the owner's profiles. It goes to the blog as one pull request, which the owner merges.
/// </summary>
public sealed class AboutService(
	IWatchedProjects watched,
	ICampaignRepository repository,
	IProjectFacts facts,
	IPictures pictures,
	IAboutSites sites,
	IAccounts accounts,
	ProfileService profiles,
	IDrafters drafters,
	IUsageLog usage,
	INotifier notifier,
	TimeProvider time,
	ILogger<AboutService> logger
)
{
	/// <summary>
	/// Proposes the about page to the blog the defaults choose. The intro the site has is kept, unless
	/// <paramref name="redraftIntro"/> asks Claude for a new one; a site without one gets one when Claude is set up.
	/// Done with null when the site already shows exactly this.
	/// </summary>
	public async Task<Outcome<AboutProposal?>> ProposeAsync(bool redraftIntro, CancellationToken ct)
	{
		if (accounts.Choose(Network.Blog, "") is not { } choice)
		{
			return new Outcome<AboutProposal?>.Refused("Connect a blog under Settings → Networks first: the about page goes to its site.");
		}
		var site = choice.Account;
		var briefs = (await repository.BriefsAsync(ct)).ToDictionary(b => ProjectBrief.Slug(b.Project), StringComparer.Ordinal);
		var groups = watched.Groups;
		var media = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		var laid = new List<(AboutProject, string?)>();
		foreach (var project in watched.All.Where(p => p.Group is not null && groups.Any(g => string.Equals(g.Title.Trim(), p.Group.Trim(), StringComparison.OrdinalIgnoreCase))))
		{
			var known = await facts.ReadAsync(project.Repository, ct);
			// A project GitHub does not call public is never shown, so nothing of it is fetched either.
			var shown = known.Visibility == ProjectVisibility.Public
				? await ShownAsync(project, known, briefs.GetValueOrDefault(ProjectBrief.Slug(project.Name)), media, ct)
				: new AboutProject(project.Name, project.Repository, known.Visibility);
			laid.Add((shown, project.Group));
		}
		var page = AboutPage.Lay(groups, laid);

		var intro = await sites.IntroAsync(site, ct);
		if ((intro is null || redraftIntro) && drafters.Current is { } drafter)
		{
			try
			{
				var about = page.Projects.Select(p => briefs.GetValueOrDefault(ProjectBrief.Slug(p.Name))).OfType<ProjectBrief>().ToList();
				var (drafted, cost) = await drafter.IntroAsync(new IntroRequest(about, await repository.VoiceAsync(ct)), ct);
				usage.Record(cost, "about intro", time.GetUtcNow());
				intro = drafted.Trim();
			}
			catch (Exception e) when (e is DraftException or HttpRequestException)
			{
				logger.LogWarning("Drafting the about page's intro failed: {Problem}", e.Message);
				if (redraftIntro)
				{
					return new Outcome<AboutProposal?>.Refused($"Claude did not write the intro: {e.Message}");
				}
			}
		}

		var linked = (await profiles.AllAsync(ct))
			.Where(v => v.Profile?.Url is { } url && url.StartsWith("https://", StringComparison.Ordinal))
			.Select(v => new AboutProfile(v.Network, v.Profile!.Url!) { Name = v.Profile.DisplayName });
		page = page with { Intro = string.IsNullOrWhiteSpace(intro) ? null : intro, Profiles = [.. linked] };

		var problems = AboutPageRules.All.Validate(page);
		if (problems.Count > 0)
		{
			return new Outcome<AboutProposal?>.Refused([.. problems.Select(p => p.Message)]);
		}
		var used = page.Projects.SelectMany(p => new[] { p.Icon, p.Image }).OfType<string>().ToHashSet(StringComparer.Ordinal);
		var proposal = await sites.ProposeAsync(site, page, media.Where(m => used.Contains(m.Key)).ToDictionary(), ct);
		if (proposal is not null)
		{
			await notifier.NotifyAsync(new Notice(NoticeLevel.Action, "The about page is ready to review", $"{site}: pull request #{proposal.Number}.", proposal.Url), ct);
		}
		return new Outcome<AboutProposal?>.Done(proposal);
	}

	/// <summary>A public project as the page shows it, with its pictures fetched into <paramref name="media"/>.</summary>
	private async Task<AboutProject> ShownAsync(WatchedProject project, ProjectFacts known, ProjectBrief? brief, Dictionary<string, byte[]> media, CancellationToken ct)
	{
		var web = brief?.Links.Where(l => l.IsWeb && l.Url.StartsWith("https://", StringComparison.Ordinal)).ToList() ?? [];
		var site = web.FirstOrDefault(l => l.Label.Equals("Website", StringComparison.OrdinalIgnoreCase))?.Url
			?? (known.Homepage is { } home && home.StartsWith("https://", StringComparison.Ordinal) ? home : null);
		var name = ProjectBrief.Slug(project.Name);
		return new AboutProject(project.Name, project.Repository, known.Visibility)
		{
			Tldr = brief?.Tldr,
			Url = site,
			// The repository is the name's link when there is no site, and the site is the name's link: neither is listed again.
			Links = [.. web.Where(l => !l.Label.Equals("Website", StringComparison.OrdinalIgnoreCase) && !l.Label.Equals("Repository", StringComparison.OrdinalIgnoreCase) && l.Url != site)],
			Release = known.Release,
			Stars = known.Stars,
			Language = known.Language,
			Icon = await SaveAsync(known.IconUrl, $"{name}-icon"),
			Image = await SaveAsync(known.ImageUrl, $"{name}-card")
		};

		async Task<string?> SaveAsync(string? url, string file)
		{
			if (url is null || await pictures.FetchAsync(url, ct) is not { } picture)
			{
				return null;
			}
			var path = $"{AboutProject.MediaFolder}{file}{picture.Extension}";
			media[path] = picture.Bytes;
			return path;
		}
	}
}
