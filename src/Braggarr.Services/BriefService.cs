using Braggarr.Domain;
using Microsoft.Extensions.Logging;

namespace Braggarr.Services;

/// <summary>
/// The projects' briefs: kept by the owner only, since drafting and the judge take a brief as the owner's word, and
/// suggested by Claude from a project's repository for the owner to check before keeping.
/// </summary>
public sealed class BriefService(ICampaignRepository repository, IRepositories repositories, IDrafters drafters, IUsageLog usage, TimeProvider time, ILogger<BriefService> logger)
{
	public Task<IReadOnlyList<ProjectBrief>> AllAsync(CancellationToken ct) => repository.BriefsAsync(ct);

	public async Task<ProjectBrief?> ForAsync(string project, CancellationToken ct)
	{
		var slug = ProjectBrief.Slug(project);
		return (await repository.BriefsAsync(ct)).FirstOrDefault(b => ProjectBrief.Slug(b.Project) == slug);
	}

	public async Task<Outcome<ProjectBrief>> SaveAsync(ProjectBrief brief, Actor by, CancellationToken ct)
	{
		if (by.Kind != ActorKind.Owner)
		{
			return new Outcome<ProjectBrief>.Refused("Only the owner writes a project's brief: drafting and the judge take it as the owner's word.");
		}
		var kept = brief with
		{
			Tldr = brief.Tldr.Trim(),
			Apart = brief.Apart.Trim(),
			Audience = brief.Audience.Trim(),
			Avoid = [.. brief.Avoid.Select(a => a.Trim()).Where(a => a.Length > 0)],
			Links = [.. brief.Links.Select(l => new BriefLink(l.Label.Trim(), l.Url.Trim()))]
		};
		var problems = ProjectBriefRules.All.Validate(kept).Select(v => v.Message).ToList();
		if (problems.Count > 0)
		{
			return new Outcome<ProjectBrief>.Refused(problems);
		}
		await repository.SetBriefAsync(kept, by, ct);
		return new Outcome<ProjectBrief>.Done(kept);
	}

	/// <summary>Claude's brief for a project from its repository, not kept: the owner reads it, changes what is wrong, then saves.</summary>
	public async Task<Outcome<ProjectBrief>> SuggestAsync(string repositoryName, string project, CancellationToken ct)
	{
		if (drafters.Current is not { } drafter)
		{
			return new Outcome<ProjectBrief>.Refused("Claude is not set up: add a key under Settings → Claude.");
		}
		try
		{
			var info = await repositories.ReadAsync(repositoryName, project, ct);
			var (brief, cost) = await drafter.BriefAsync(info, ct);
			usage.Record(cost, $"brief {repositoryName}", time.GetUtcNow());
			return new Outcome<ProjectBrief>.Done(brief with { Project = project });
		}
		catch (DraftException e)
		{
			logger.LogWarning("Suggesting a brief for {Repository} failed: {Problem}", repositoryName, e.Message);
			return new Outcome<ProjectBrief>.Refused($"Claude could not write a brief: {e.Message}");
		}
		catch (HttpRequestException e)
		{
			logger.LogWarning("Reading {Repository} for its brief failed: {Problem}", repositoryName, e.Message);
			return new Outcome<ProjectBrief>.Refused($"{repositoryName} could not be read from GitHub.");
		}
	}
}
