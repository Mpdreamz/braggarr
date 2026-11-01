using System.Text;
using Braggarr.Services;

namespace Braggarr.Adapters.GitHub;

/// <summary>A repository's description, topics, README and recent releases, through GitHub's REST API.</summary>
public sealed class GitHubRepositories(GitHubClient github, IReleases releases) : IRepositories
{
	public const int ReadmeLimit = 12_000;

	public async Task<RepositoryInfo> ReadAsync(string repository, string name, CancellationToken ct)
	{
		var repo = await github.GetAsync<Repo>($"repos/{repository}", ct);
		string readme;
		try
		{
			var file = await github.GetAsync<Readme>($"repos/{repository}/readme", ct);
			readme = Encoding.UTF8.GetString(Convert.FromBase64String(file.Content.Replace("\n", "", StringComparison.Ordinal)));
		}
		catch (GitHubException e) when (e.Status == System.Net.HttpStatusCode.NotFound)
		{
			readme = "";
		}
		var recent = await releases.LatestAsync(repository, ct);
		return new RepositoryInfo(
			repository, name, repo.Description ?? "", string.IsNullOrWhiteSpace(repo.Homepage) ? null : repo.Homepage, repo.Topics ?? [],
			readme.Length > ReadmeLimit ? readme[..ReadmeLimit] : readme, [.. recent.Take(5)]
		);
	}

	internal sealed record Repo(string? Description, string? Homepage, string[]? Topics, bool Private = false);

	internal sealed record Readme(string Content);
}
