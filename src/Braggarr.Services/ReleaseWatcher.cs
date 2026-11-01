using Braggarr.Domain;
using Microsoft.Extensions.Logging;

namespace Braggarr.Services;

/// <summary>
/// Turns a new release of a watched project into a proposed campaign, through the same path an agent's proposal takes: drafted
/// when a model is set up, reviewed as a pull request, never approved by itself. A project's first check only remembers the
/// latest release, so adding a project never proposes its history.
/// </summary>
public sealed class ReleaseWatcher(IReleases releases, IWatchedProjects projects, CampaignService campaigns, DraftingService drafting, TimeProvider time, ILogger<ReleaseWatcher> logger)
{
	public static readonly Actor Watcher = Actor.Agent("release-watcher");

	/// <summary>The hour, in the owner's time zone, a release is announced: the next morning at ten.</summary>
	public const int Hour = 10;

	public async Task<IReadOnlyList<Proposal>> CheckAsync(CancellationToken ct)
	{
		var proposed = new List<Proposal>();
		// A snapshot: marking a release seen changes the list.
		foreach (var project in projects.All.ToList())
		{
			try
			{
				var candidates = (await releases.LatestAsync(project.Repository, ct)).Where(r => project.IncludePrereleases || !r.Prerelease).ToList();
				if (candidates.Count == 0)
				{
					continue;
				}
				if (project.LastSeenTag is null)
				{
					projects.MarkSeen(project.Repository, candidates[0].Tag);
					continue;
				}
				// Newest first: everything before the last one seen is new. Only the newest is announced; a burst of releases is one campaign.
				var seen = candidates.FindIndex(r => r.Tag == project.LastSeenTag);
				if (seen == 0)
				{
					continue;
				}
				var release = candidates[0];
				var verdict = project.AskClaude ? await drafting.JudgeAsync(project.Name, release, ct) : null;
				if (verdict is { Worthy: false })
				{
					projects.RecordSkipped(project.Repository, release.Tag, verdict.Reason);
					projects.MarkSeen(project.Repository, release.Tag);
					continue;
				}
				if (await ProposeAsync(project, release, verdict?.Angle, ct) is { } proposal)
				{
					proposed.Add(proposal);
				}
			}
			catch (Exception e) when (e is not OperationCanceledException)
			{
				// One repository that cannot be read does not stop the others.
				logger.LogWarning(e, "Could not check the releases of {Repository}.", project.Repository);
			}
		}
		return proposed;
	}

	/// <summary>Announces a release whatever the model thought of it: the owner's "propose anyway".</summary>
	public async Task<Outcome<Proposal>> ProposeAnywayAsync(string repository, string tag, CancellationToken ct)
	{
		var project = projects.All.FirstOrDefault(p => p.Repository == repository);
		var release = project is null ? null : (await releases.LatestAsync(repository, ct)).FirstOrDefault(r => r.Tag == tag);
		if (project is null || release is null)
		{
			return new Outcome<Proposal>.Refused($"There is no release {tag} of {repository} to propose.");
		}
		return await ProposeAsync(project, release, null, ct) is { } proposal
			? new Outcome<Proposal>.Done(proposal)
			: new Outcome<Proposal>.Refused($"{repository} {tag} could not be proposed; the log says why.");
	}

	private async Task<Proposal?> ProposeAsync(WatchedProject project, Release release, string? angle, CancellationToken ct)
	{
		projects.MarkSeen(project.Repository, release.Tag);
		switch (await campaigns.ProposeAsync(Draft(project, release, angle), Watcher, ct))
		{
			case Outcome<Proposal>.Done(var proposal):
				return proposal;
			case Outcome<Proposal>.Refused(var reasons):
				logger.LogWarning("The release {Tag} of {Repository} was not proposed: {Reasons}", release.Tag, project.Repository, string.Join(" ", reasons));
				return null;
			default:
				return null;
		}
	}

	private CampaignDraft Draft(WatchedProject project, Release release, string? angle)
	{
		var title = string.IsNullOrWhiteSpace(release.Name) || release.Name == release.Tag ? $"{project.Name} {release.Tag}" : $"{project.Name} {release.Name}";
		var notes = release.Notes.Length > 4000 ? release.Notes[..4000] + "…" : release.Notes;
		var lead = angle is { Length: > 0 } ? $"The angle to take: {angle}" : $"Lead with what matters to someone who has never heard of {project.Name}.";
		var brief = $"{project.Name} {release.Tag} was released. Announce what is new, from the release notes below. {lead}\n\nRelease notes:\n{notes}";
		return new CampaignDraft(
			Slugs.From($"{project.Name} {release.Tag}"),
			title,
			project.Name,
			// Hacker News and Reddit see a project's launch and its major versions, not every release.
			IsMajor(release.Tag) ? project.Networks : [.. project.Networks.Where(n => !n.IsManual())],
			[release.Url, $"https://github.com/{project.Repository}"],
			brief,
			[]
		)
		{ Start = NextMorning() };
	}

	/// <summary>A first or major version: 1.0, v2.0.0, 3. Not 0.4.0, 1.2 or 2.0.1.</summary>
	public static bool IsMajor(string tag)
	{
		var version = tag.TrimStart('v', 'V').Split('-', '+')[0].Split('.');
		return version.Length > 0
			&& int.TryParse(version[0], out var major) && major > 0
			&& version.Skip(1).All(p => int.TryParse(p, out var n) && n == 0);
	}

	/// <summary>Ten tomorrow morning in the owner's time zone, so there is a night to review it.</summary>
	private DateTimeOffset NextMorning()
	{
		var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), projects.Zone);
		var day = local.Date.AddDays(1).AddHours(Hour);
		return new DateTimeOffset(day, projects.Zone.GetUtcOffset(day));
	}
}
