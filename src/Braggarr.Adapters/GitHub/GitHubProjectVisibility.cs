using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Adapters.GitHub;

/// <summary>
/// Whether a campaign's project is public, from its repository on GitHub. A repository Braggarr cannot find, or GitHub will not
/// show it, is unknown: never public.
/// </summary>
public sealed partial class GitHubProjectVisibility(GitHubClient github, Func<Campaign, string?> repositoryOf) : IProjectVisibility
{
	public async Task<ProjectVisibility> OfAsync(Campaign campaign, CancellationToken ct)
	{
		if (repositoryOf(campaign) is not { } repository)
		{
			return ProjectVisibility.Unknown;
		}
		try
		{
			var repo = await github.GetAsync<GitHubRepositories.Repo>($"repos/{repository}", ct);
			return repo.Private ? ProjectVisibility.Private : ProjectVisibility.Public;
		}
		catch (GitHubException)
		{
			return ProjectVisibility.Unknown;
		}
	}

	/// <summary>The first GitHub repository among a campaign's links, as <c>owner/name</c>.</summary>
	public static string? FromLinks(IEnumerable<string> links) =>
		links.Select(l => Repository().Match(l)).FirstOrDefault(m => m.Success) is { } match ? $"{match.Groups["owner"].Value}/{match.Groups["name"].Value}" : null;

	[System.Text.RegularExpressions.GeneratedRegex(@"^https?://github\.com/(?<owner>[A-Za-z0-9-]+)/(?<name>[A-Za-z0-9._-]+?)(\.git)?/?(?:[?#].*)?$")]
	private static partial System.Text.RegularExpressions.Regex Repository();
}
