using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Web.Settings;

/// <summary>The watched projects as <c>config.json</c> keeps them.</summary>
public sealed class SettingsProjects(SettingsStore settings, Clock clock, TimeProvider time) : IWatchedProjects
{
	public IReadOnlyList<WatchedProject> All =>
	[
		.. settings.Current.Projects.Select(p => new WatchedProject(
			p.Repository,
			p.Name,
			[.. p.Networks.Select(n => Networks.TryParse(n, out var network) ? network : (Network?)null).OfType<Network>()],
			p.LastSeenTag,
			p.IncludePrereleases,
			p.AskClaude,
			p.Group
		))
	];

	public IReadOnlyList<AboutGroup> Groups => [.. settings.Current.ProjectGroups.Select(g => new AboutGroup(g.Title) { Description = g.Description })];

	public TimeZoneInfo Zone => clock.Zone;

	public void RecordSkipped(string repository, string tag, string reason) =>
		settings.Update(s => s with
		{
			Projects = [.. s.Projects.Select(p => p.Repository == repository ? p with { LastSkipped = new SkippedRelease(tag, reason, time.GetUtcNow()) } : p)]
		});

	public void MarkSeen(string repository, string tag) =>
		settings.Update(s => s with { Projects = [.. s.Projects.Select(p => p.Repository == repository ? p with { LastSeenTag = tag } : p)] });
}
