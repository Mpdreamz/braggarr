using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Braggarr.Adapters.Posts;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Adapters.GitHub;

/// <summary>
/// Proposals as pull requests on the posts repo. A proposal is written straight through GitHub's Git Data API (a tree, a commit and
/// a branch <c>campaign/&lt;slug&gt;</c>), so it never touches the local clone the scheduler works in.
/// </summary>
public sealed class GitHubReviews(GitHubClient github) : IReviews
{
	public const string BranchPrefix = "campaign/";

	// A branch head is immutable, so what was read at a commit never needs reading again.
	private readonly ConcurrentDictionary<string, Campaign> _byHead = new();

	public async Task<Proposal> ProposeAsync(Campaign campaign, Actor by, CancellationToken ct)
	{
		var main = await github.GetAsync<Ref>(github.Repo("git/ref/heads/main"), ct);
		var parent = await github.GetAsync<Commit>(github.Repo($"git/commits/{main.Object.Sha}"), ct);
		var files = PostsFormat.WriteCampaign(campaign);
		var tree = await github.PostAsync<ShaRef>(
			github.Repo("git/trees"),
			new NewTree(parent.Tree.Sha, [.. files.Select(f => TreeChange.Text($"campaigns/{campaign.Slug}/{f.Key}", f.Value))]),
			ct
		);
		var commit = await github.PostAsync<ShaRef>(
			github.Repo("git/commits"),
			new NewCommit($"Propose {campaign.Slug}\n\nBy {by}.", tree.Sha, [main.Object.Sha]),
			ct
		);
		_ = await github.PostAsync<Ref>(github.Repo("git/refs"), new NewRef($"refs/heads/{BranchPrefix}{campaign.Slug}", commit.Sha), ct);
		var pull = await github.PostAsync<Pull>(
			github.Repo("pulls"),
			new NewPull($"Campaign: {campaign.Title}", $"{BranchPrefix}{campaign.Slug}", "main", Describe(campaign, by)),
			ct
		);
		return new Proposal(pull.Number, pull.HtmlUrl, campaign);
	}

	public async Task<IReadOnlyList<Proposal>> OpenAsync(CancellationToken ct)
	{
		var pulls = await github.GetAsync<Pull[]>(github.Repo("pulls?state=open&base=main&per_page=100"), ct);
		var proposals = new List<Proposal>();
		foreach (var pull in pulls.Where(p => p.Head.Ref.StartsWith(BranchPrefix, StringComparison.Ordinal)))
		{
			var slug = pull.Head.Ref[BranchPrefix.Length..];
			if (!_byHead.TryGetValue(pull.Head.Sha, out var campaign))
			{
				campaign = await ReadAsync(slug, pull.Head.Sha, ct);
				_byHead[pull.Head.Sha] = campaign;
			}
			proposals.Add(new Proposal(pull.Number, pull.HtmlUrl, campaign));
		}
		return proposals;
	}

	public async Task<Proposal> UpdateAsync(Proposal proposal, Campaign campaign, Actor by, CancellationToken ct, IReadOnlyDictionary<string, byte[]>? media = null)
	{
		var branch = $"{BranchPrefix}{campaign.Slug}";
		var head = await github.GetAsync<Ref>(github.Repo($"git/ref/heads/{branch}"), ct);
		var parent = await github.GetAsync<Commit>(github.Repo($"git/commits/{head.Object.Sha}"), ct);
		var folder = $"campaigns/{campaign.Slug}/";
		var current = await github.GetAsync<TreeListing>(github.Repo($"git/trees/{head.Object.Sha}?recursive=1"), ct);
		var files = PostsFormat.WriteCampaign(campaign);
		// Files of posts that are gone are deleted (a null sha); the rest are written over.
		var removed = current.Tree
			.Where(e => e.Type == "blob" && e.Path.StartsWith(folder, StringComparison.Ordinal) && e.Path.EndsWith(".md", StringComparison.Ordinal))
			.Where(e => !files.ContainsKey(e.Path[folder.Length..]))
			.Select(e => TreeChange.Blob(e.Path, null));
		var written = files.Select(f => TreeChange.Text(folder + f.Key, f.Value)).ToList();
		foreach (var (path, bytes) in media ?? new Dictionary<string, byte[]>())
		{
			// Binary files go up as blobs first; a tree entry's inline content is text only.
			var blob = await github.PostAsync<ShaRef>(github.Repo("git/blobs"), new NewBlob(Convert.ToBase64String(bytes), "base64"), ct);
			written.Add(TreeChange.Blob(folder + path, blob.Sha));
		}
		var tree = await github.PostAsync<ShaRef>(github.Repo("git/trees"), new NewTree(parent.Tree.Sha, [.. written.Concat(removed)]), ct);
		var commit = await github.PostAsync<ShaRef>(
			github.Repo("git/commits"),
			new NewCommit($"Update {campaign.Slug}\n\nBy {by}.", tree.Sha, [head.Object.Sha]),
			ct
		);
		_ = await github.PatchAsync<Ref>(github.Repo($"git/refs/heads/{branch}"), new MoveRef(commit.Sha), ct);
		_byHead[commit.Sha] = campaign;
		return proposal with { Campaign = campaign };
	}

	public async Task<byte[]?> ReadMediaAsync(Proposal proposal, string path, CancellationToken ct)
	{
		var branch = $"{BranchPrefix}{proposal.Campaign.Slug}";
		var head = await github.GetAsync<Ref>(github.Repo($"git/ref/heads/{branch}"), ct);
		var tree = await github.GetAsync<TreeListing>(github.Repo($"git/trees/{head.Object.Sha}?recursive=1"), ct);
		var entry = tree.Tree.FirstOrDefault(e => e.Type == "blob" && e.Path == $"campaigns/{proposal.Campaign.Slug}/{path}");
		if (entry is null)
		{
			return null;
		}
		var blob = await github.GetAsync<Blob>(github.Repo($"git/blobs/{entry.Sha}"), ct);
		return Convert.FromBase64String(blob.Content.Replace("\n", "", StringComparison.Ordinal));
	}

	public async Task MergeAsync(Proposal proposal, Actor by, CancellationToken ct)
	{
		_ = await github.PutAsync<MergeResult>(
			github.Repo($"pulls/{proposal.Number}/merge"),
			new Merge("squash", $"Approve {proposal.Campaign.Slug} (#{proposal.Number})", $"Approved by {by}."),
			ct
		);
		await github.DeleteAsync(github.Repo($"git/refs/heads/{BranchPrefix}{proposal.Campaign.Slug}"), ct);
	}

	public async Task CloseAsync(Proposal proposal, Actor by, CancellationToken ct)
	{
		_ = await github.PostAsync<Comment>(github.Repo($"issues/{proposal.Number}/comments"), new NewComment($"Rejected by {by}."), ct);
		_ = await github.PatchAsync<Pull>(github.Repo($"pulls/{proposal.Number}"), new PullState("closed"), ct);
		await github.DeleteAsync(github.Repo($"git/refs/heads/{BranchPrefix}{proposal.Campaign.Slug}"), ct);
	}

	private async Task<Campaign> ReadAsync(string slug, string head, CancellationToken ct)
	{
		var tree = await github.GetAsync<TreeListing>(github.Repo($"git/trees/{head}?recursive=1"), ct);
		var prefix = $"campaigns/{slug}/";
		var files = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var entry in tree.Tree.Where(e => e.Type == "blob" && e.Path.StartsWith(prefix, StringComparison.Ordinal)
			&& (e.Path.EndsWith(".md", StringComparison.Ordinal) || e.Path == prefix + PostsFormat.JudgementFile)))
		{
			var blob = await github.GetAsync<Blob>(github.Repo($"git/blobs/{entry.Sha}"), ct);
			files[entry.Path[prefix.Length..]] = Encoding.UTF8.GetString(Convert.FromBase64String(blob.Content.Replace("\n", "", StringComparison.Ordinal)));
		}
		return PostsFormat.ReadCampaign(new CampaignFiles(slug, files), CampaignStatus.Proposed);
	}

	private static string Describe(Campaign campaign, Actor by)
	{
		var body = new StringBuilder();
		_ = body.Append("Proposed by **").Append(by.Name).Append("** (").Append(by.Kind.ToString().ToLowerInvariant()).Append(").\n\n");
		_ = body.Append(campaign.Brief).Append("\n\n");
		foreach (var post in campaign.Posts)
		{
			_ = body.Append("### ").Append(post.Id).Append(" · ").Append(post.At.ToString("ddd d MMM HH:mm zzz", System.Globalization.CultureInfo.InvariantCulture)).Append("\n\n");
			foreach (var variant in post.Variants)
			{
				_ = body.Append("**").Append(variant.Network).Append("** (").Append(Lengths.Measure(variant.Network, variant.Text)).Append('/').Append(Lengths.Limit(variant.Network)).Append(")\n\n> ")
					.Append(variant.Text.Replace("\n", "\n> ", StringComparison.Ordinal)).Append("\n\n");
			}
		}
		_ = body.Append("Merging this pull request approves the campaign.");
		return body.ToString();
	}

	internal sealed record ShaRef(string Sha);

	internal sealed record Ref(ShaRef Object);

	internal sealed record Commit(ShaRef Tree);

	internal sealed record Head(string Ref, string Sha);

	internal sealed record Pull(int Number, string HtmlUrl, Head Head);

	internal sealed record MergeResult(bool Merged);

	internal sealed record Comment(long Id);

	internal sealed record TreeEntry(string Path, string Type, string Sha);

	internal sealed record TreeListing(TreeEntry[] Tree);

	internal sealed record Blob(string Content);

	internal sealed record NewTree(string BaseTree, IReadOnlyList<TreeChange> Tree);

	/// <summary>
	/// A file in a new tree: text written inline (<c>content</c>, no <c>sha</c>), or a blob by its sha, where a null sha deletes
	/// the file and is written as <c>"sha": null</c>. Written by hand, since which of the two a tree entry has is per entry.
	/// </summary>
	[JsonConverter(typeof(TreeChangeConverter))]
	internal sealed record TreeChange(string Path, string? Content, string? Sha, bool BySha)
	{
		public static TreeChange Text(string path, string content) => new(path, content, null, false);

		public static TreeChange Blob(string path, string? sha) => new(path, null, sha, true);
	}

	internal sealed class TreeChangeConverter : JsonConverter<TreeChange>
	{
		public override TreeChange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
			throw new NotSupportedException("Braggarr only writes tree entries.");

		public override void Write(Utf8JsonWriter writer, TreeChange value, JsonSerializerOptions options)
		{
			writer.WriteStartObject();
			writer.WriteString("path", value.Path);
			writer.WriteString("mode", "100644");
			writer.WriteString("type", "blob");
			if (value.BySha)
			{
				writer.WriteString("sha", value.Sha);
			}
			else
			{
				writer.WriteString("content", value.Content);
			}
			writer.WriteEndObject();
		}
	}

	internal sealed record NewBlob(string Content, string Encoding);

	internal sealed record NewCommit(string Message, string Tree, IReadOnlyList<string> Parents);

	internal sealed record NewRef(string Ref, string Sha);

	internal sealed record MoveRef(string Sha);

	internal sealed record NewPull(string Title, string Head, string Base, string Body);

	internal sealed record Merge(string MergeMethod, string CommitTitle, string CommitMessage);

	internal sealed record NewComment(string Body);

	internal sealed record PullState(string State);
}
