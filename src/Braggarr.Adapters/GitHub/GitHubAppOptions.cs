namespace Braggarr.Adapters.GitHub;

/// <summary>Braggarr's own GitHub App: it signs the owner in and is installed on the posts repo only.</summary>
public sealed class GitHubAppOptions
{
	public long AppId { get; set; }

	public string? Slug { get; set; }

	public string? ClientId { get; set; }

	public string? ClientSecret { get; set; }

	/// <summary>The app's private key, PEM. Stored encrypted in <c>/config</c>, never in the posts repo.</summary>
	public string? PrivateKeyPem { get; set; }

	public string ApiBase { get; set; } = "https://api.github.com";

	public bool IsConfigured => AppId > 0 && !string.IsNullOrEmpty(PrivateKeyPem);
}
