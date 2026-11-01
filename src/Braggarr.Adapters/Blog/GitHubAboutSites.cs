using System.Security.Cryptography;
using System.Text;
using Braggarr.Adapters.GitHub;
using Braggarr.Adapters.Social;
using Braggarr.Blog;
using Braggarr.Domain;
using Braggarr.Services;
using static Braggarr.Adapters.GitHub.GitHubReviews;

namespace Braggarr.Adapters.Blog;

/// <summary>
/// A blog site's about page in its GitHub repository: <c>_braggarr/projects.yml</c>, <c>intro.md</c>, <c>profiles.yml</c> and
/// the pictures in <c>_braggarr/media/</c>, proposed on the branch <see cref="Branch"/> as one pull request, kept up to date
/// while it is open. Pictures the page no longer shows are removed with it.
/// </summary>
public sealed class GitHubAboutSites(GitHubClient github, AccountStore accounts) : IAboutSites
{
	public const string Branch = "braggarr/about";

	private const string Folder = "_braggarr/";

	public async Task<string?> IntroAsync(string site, CancellationToken ct)
	{
		var blog = Site(site);
		try
		{
			var file = await github.GetAsync<BlogPublisher.Contents>($"repos/{blog.Repository}/contents/{Publishing.IntroFile}?ref={Uri.EscapeDataString(blog.Branch)}", ct);
			return Encoding.UTF8.GetString(Convert.FromBase64String(file.Content.Replace("\n", "", StringComparison.Ordinal)));
		}
		catch (GitHubException e) when (e.Status == System.Net.HttpStatusCode.NotFound)
		{
			return null;
		}
	}

	public async Task<AboutProposal?> ProposeAsync(string site, AboutPage page, IReadOnlyDictionary<string, byte[]> media, CancellationToken ct)
	{
		var blog = Site(site);
		string Repo(string path) => $"repos/{blog.Repository}/{path}";
		try
		{
			var head = await github.GetAsync<Ref>(Repo($"git/ref/heads/{Uri.EscapeDataString(blog.Branch)}"), ct);
			var parent = await github.GetAsync<Commit>(Repo($"git/commits/{head.Object.Sha}"), ct);
			var listing = await github.GetAsync<TreeListing>(Repo($"git/trees/{head.Object.Sha}?recursive=1"), ct);
			var there = listing.Tree.Where(e => e.Type == "blob").ToDictionary(e => e.Path, e => e.Sha, StringComparer.Ordinal);

			var wanted = Files(page, media);
			var changed = wanted.Where(f => !there.TryGetValue(f.Key, out var sha) || sha != BlobSha(f.Value)).ToList();
			var stale = there.Keys.Where(p => p.StartsWith(Folder + AboutProject.MediaFolder, StringComparison.Ordinal) && !wanted.ContainsKey(p)).ToList();
			if (changed.Count == 0 && stale.Count == 0)
			{
				return null;
			}

			var tree = new List<TreeChange>();
			foreach (var (path, bytes) in changed)
			{
				if (path.StartsWith(Folder + AboutProject.MediaFolder, StringComparison.Ordinal))
				{
					var blob = await github.PostAsync<ShaRef>(Repo("git/blobs"), new NewBlob(Convert.ToBase64String(bytes), "base64"), ct);
					tree.Add(TreeChange.Blob(path, blob.Sha));
				}
				else
				{
					tree.Add(TreeChange.Text(path, Encoding.UTF8.GetString(bytes)));
				}
			}
			tree.AddRange(stale.Select(p => TreeChange.Blob(p, null)));
			var newTree = await github.PostAsync<ShaRef>(Repo("git/trees"), new NewTree(parent.Tree.Sha, tree), ct);
			var commit = await github.PostAsync<ShaRef>(Repo("git/commits"), new NewCommit(
				"The about page from Braggarr: projects, intro and profiles", newTree.Sha, [head.Object.Sha]), ct);

			// The branch is Braggarr's own: it is moved to the new commit, which starts from the site's branch as it is now.
			try
			{
				_ = await github.PatchAsync<Ref>(Repo($"git/refs/heads/{Branch}"), new ForceRef(commit.Sha, true), ct);
			}
			catch (GitHubException e) when (e.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.UnprocessableEntity)
			{
				_ = await github.PostAsync<Ref>(Repo("git/refs"), new NewRef($"refs/heads/{Branch}", commit.Sha), ct);
			}

			var owner = blog.Repository[..blog.Repository.IndexOf('/')];
			var open = await github.GetAsync<Pull[]>(Repo($"pulls?state=open&head={Uri.EscapeDataString($"{owner}:{Branch}")}"), ct);
			var pull = open.FirstOrDefault()
				?? await github.PostAsync<Pull>(Repo("pulls"), new NewPull("The about page from Braggarr", Branch, blog.Branch, Describe(page)), ct);
			return new AboutProposal(pull.Number, pull.HtmlUrl);
		}
		catch (GitHubException e)
		{
			throw new PublishException($"The about page could not be proposed to {blog.Repository}: {e.Message}", e);
		}
	}

	/// <summary>Every file the page is, by its path in the site, as bytes: the YAML and markdown as UTF-8.</summary>
	internal static Dictionary<string, byte[]> Files(AboutPage page, IReadOnlyDictionary<string, byte[]> media)
	{
		var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
		{
			[Publishing.ProjectsFile] = Encoding.UTF8.GetBytes(Publishing.WriteProjects(Groups(page)))
		};
		if (page.Intro is { } intro)
		{
			files[Publishing.IntroFile] = Encoding.UTF8.GetBytes(intro.Trim() + "\n");
		}
		if (page.Profiles.Count > 0)
		{
			files[Publishing.ProfilesFile] = Encoding.UTF8.GetBytes(Publishing.WriteProfiles(page.Profiles.Select(p => new Profile(p.Network.Slug(), p.Url) { Name = p.Name })));
		}
		foreach (var (path, bytes) in media)
		{
			files[Folder + path] = bytes;
		}
		return files;
	}

	/// <summary>The page's groups as the site's builder reads them.</summary>
	internal static IEnumerable<ProjectGroup> Groups(AboutPage page) =>
		page.Sections.Select(s => new ProjectGroup(s.Group.Title, [.. s.Projects.Select(p => new Project(p.Name, p.Repository)
		{
			Tldr = p.Tldr,
			Url = p.Url,
			Links = [.. p.Links.Select(l => new ProjectLink(l.Label, l.Url))],
			Release = p.Release is { } r ? new Braggarr.Blog.Release(r.Tag, r.Date, r.Url) : null,
			Stars = p.Stars,
			Language = p.Language,
			Icon = p.Icon,
			Image = p.Image
		})])
		{ Description = s.Group.Description });

	/// <summary>The sha git gives a file's content, so what the site has already is not written again.</summary>
	internal static string BlobSha(byte[] content)
	{
		var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
#pragma warning disable CA5350 // git names blobs by SHA-1; this compares, it does not protect anything
		return Convert.ToHexStringLower(SHA1.HashData([.. header, .. content]));
#pragma warning restore CA5350
	}

	private static string Describe(AboutPage page)
	{
		var body = new StringBuilder("The about page as Braggarr knows it: the groups and their public projects, the intro and the profiles. ")
			.Append("Merging this pull request publishes it; Braggarr updates this pull request while it is open.\n\n");
		foreach (var section in page.Sections)
		{
			_ = body.Append("**").Append(section.Group.Title).Append("**: ").Append(string.Join(", ", section.Projects.Select(p => p.Name))).Append('\n');
		}
		return body.ToString();
	}

	private BlogSite Site(string site) =>
		accounts.Get<BlogSite>(Network.Blog, site) ?? throw new PublishException($"{site} is not a connected blog: connect it under Settings → Networks.");

	internal sealed record ForceRef(string Sha, bool Force);
}
