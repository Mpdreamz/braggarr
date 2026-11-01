using Braggarr.Services;

namespace Braggarr.Adapters.GitHub;

/// <summary>Releases through GitHub's REST API, with the app's token: public repositories are readable with it.</summary>
public sealed class GitHubReleases(GitHubClient github) : IReleases
{
	public async Task<IReadOnlyList<Release>> LatestAsync(string repository, CancellationToken ct)
	{
		var releases = await github.GetAsync<Item[]>($"repos/{repository}/releases?per_page=10", ct);
		return
		[
			.. releases
				.Where(r => !r.Draft && r.PublishedAt is not null)
				.OrderByDescending(r => r.PublishedAt)
				.Select(r => new Release(r.TagName, r.Name ?? r.TagName, r.HtmlUrl, r.Body ?? "", r.PublishedAt!.Value, r.Prerelease))
		];
	}

	internal sealed record Item(string TagName, string? Name, string HtmlUrl, string? Body, DateTimeOffset? PublishedAt, bool Prerelease, bool Draft);
}
