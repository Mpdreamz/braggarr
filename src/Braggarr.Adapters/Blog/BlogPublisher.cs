using System.Text;
using Braggarr.Adapters.GitHub;
using Braggarr.Blog;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Adapters.Blog;

/// <summary>
/// A blog: a site built from a GitHub repository by braggarr. The account is the repository, the branch Braggarr commits to, the
/// folder its posts are in, and, when the site is served elsewhere than its <c>braggarr.yml</c> says, the address it is served from.
/// </summary>
public sealed record BlogSite(string Repository, string Branch = "main", string Folder = "posts", string? BaseUrl = null);

/// <summary>
/// Puts a post on a blog by committing it to the site's repository: its <c>index.md</c> and its images, in one commit on the
/// site's branch, which builds the site. The address it will have comes from the site's own permalink rule, before the commit.
/// Committing again finds the post already there as it would be written, and changes nothing, so a retry cannot post twice.
/// </summary>
public sealed class BlogPublisher(GitHubClient github, BlogSite site) : IPublisher
{
	public Network Network => Network.Blog;

	public async Task<Receipt> PublishAsync(PublishRequest request, CancellationToken ct)
	{
		var article = request.Variant.Article ?? throw new PublishException("A blog post needs its title and description, and this one has none.");
		var config = await ConfigAsync(ct);
		var post = new PublishedPost(article.Title, request.At, article.Description, [.. article.Tags], article.Path)
		{
			Cover = article.Cover?.Path,
			CoverAlt = article.Cover?.Alt,
			Campaign = request.Key.Campaign
		};
		var folder = $"{site.Folder.Trim('/')}/{Publishing.Folder(post)}";
		var index = Publishing.Write(post, request.Variant.Text);
		var receipt = new Receipt(folder, Publishing.Address(config, post, site.BaseUrl is { } root ? new Uri(root) : null));
		try
		{
			if (await ReadAsync($"{folder}/index.md", ct) == index)
			{
				// Already in as it would be written: a commit that got in before an interruption. Nothing to do.
				return receipt;
			}
			var head = await github.GetAsync<GitHubReviews.Ref>(Repo($"git/ref/heads/{site.Branch}"), ct);
			var parent = await github.GetAsync<GitHubReviews.Commit>(Repo($"git/commits/{head.Object.Sha}"), ct);
			var changes = new List<GitHubReviews.TreeChange> { GitHubReviews.TreeChange.Text($"{folder}/index.md", index) };
			foreach (var image in request.Media)
			{
				// Beside the post, at the path the article names it by (media/…), so its links work as written.
				var blob = await github.PostAsync<GitHubReviews.ShaRef>(Repo("git/blobs"), new GitHubReviews.NewBlob(Convert.ToBase64String(image.Bytes), "base64"), ct);
				changes.Add(GitHubReviews.TreeChange.Blob($"{folder}/{image.Path}", blob.Sha));
			}
			var tree = await github.PostAsync<GitHubReviews.ShaRef>(Repo("git/trees"), new GitHubReviews.NewTree(parent.Tree.Sha, changes), ct);
			var commit = await github.PostAsync<GitHubReviews.ShaRef>(
				Repo("git/commits"),
				new GitHubReviews.NewCommit($"Post \"{article.Title}\"\n\nFrom Braggarr, for the campaign {request.Key.Campaign}.", tree.Sha, [head.Object.Sha]),
				ct
			);
			_ = await github.PatchAsync<GitHubReviews.Ref>(Repo($"git/refs/heads/{site.Branch}"), new GitHubReviews.MoveRef(commit.Sha), ct);
			return receipt;
		}
		catch (GitHubException e)
		{
			// GitHub said no before the branch moved: nothing was committed, so trying again cannot post twice.
			throw new PublishException($"GitHub refused the commit to {site.Repository} ({(int?)e.Status}). Is Braggarr's GitHub App installed on it, and may it write to {site.Branch}?");
		}
	}

	/// <summary>Checks the connection: the site's title, from its <c>braggarr.yml</c> on the branch.</summary>
	public async Task<string> TestAsync(CancellationToken ct)
	{
		try
		{
			return (await ConfigAsync(ct)).Title;
		}
		catch (GitHubException e)
		{
			throw new PublishException($"GitHub refused to show {site.Repository} ({(int?)e.Status}). Install Braggarr's GitHub App on it, then try again.");
		}
	}

	private async Task<SiteConfig> ConfigAsync(CancellationToken ct)
	{
		var yaml = await ReadAsync(Publishing.ConfigFile, ct)
			?? throw new PublishException($"{site.Repository} has no {Publishing.ConfigFile} on {site.Branch}: it is not a braggarr site yet (braggarr blog init).");
		return Publishing.ReadConfig(yaml, out var problems)
			?? throw new PublishException($"{site.Repository}'s {Publishing.ConfigFile} does not read: {string.Join(" ", problems.Select(p => p.Message))}");
	}

	/// <summary>A text file on the site's branch, or null when it is not there.</summary>
	private async Task<string?> ReadAsync(string path, CancellationToken ct)
	{
		try
		{
			var file = await github.GetAsync<Contents>(Repo($"contents/{path}?ref={Uri.EscapeDataString(site.Branch)}"), ct);
			return Encoding.UTF8.GetString(Convert.FromBase64String(file.Content.Replace("\n", "", StringComparison.Ordinal)));
		}
		catch (GitHubException e) when (e.Status == System.Net.HttpStatusCode.NotFound)
		{
			return null;
		}
	}

	private string Repo(string path) => $"repos/{site.Repository}/{path}";

	internal sealed record Contents(string Content);
}
