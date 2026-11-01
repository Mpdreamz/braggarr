namespace Braggarr.Adapters.Git;

public sealed class PostsRepositoryOptions
{
	/// <summary>The posts repo as <c>owner/name</c>.</summary>
	public string Repository { get; set; } = "";

	/// <summary>Where the clone lives, under <c>/config</c>.</summary>
	public string Directory { get; set; } = "/config/posts";

	/// <summary>The clone URL; GitHub's from <see cref="Repository"/> unless set (tests point it at a bare repo on disk).</summary>
	public string? CloneUrl { get; set; }

	public string Url => CloneUrl ?? $"https://github.com/{Repository}.git";
}

/// <summary>Hands out a token for git and the GitHub API. Null where none is needed (a local test repo).</summary>
public interface IGitHubToken
{
	Task<string?> GetAsync(CancellationToken ct);
}
