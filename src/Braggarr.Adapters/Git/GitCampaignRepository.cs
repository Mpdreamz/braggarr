using System.IO.Abstractions;
using Braggarr.Adapters.Posts;
using Braggarr.Domain;
using Braggarr.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Braggarr.Adapters.Git;

/// <summary>
/// The posts repo as a local clone. Braggarr only ever writes <c>deliveries.yml</c> and the <c>held</c> line, commits each change
/// locally before returning (that commit is the durable record), and rebases onto <c>main</c> before pushing, so it never conflicts
/// with the owner's edits. One git operation runs at a time.
/// </summary>
public sealed class GitCampaignRepository(IOptionsMonitor<PostsRepositoryOptions> options, IGitHubToken token, IFileSystem fs, ILogger<GitCampaignRepository> logger)
	: ICampaignRepository, IDisposable
{
	private readonly SemaphoreSlim _lock = new(1, 1);
	private PostsRepositoryOptions Options => options.CurrentValue;

	private GitRunner Git => new(Options.Directory);

	private string Campaigns => fs.Path.Combine(Options.Directory, "campaigns");

	public async Task<RepositoryState> PullAsync(CancellationToken ct)
	{
		await _lock.WaitAsync(ct);
		try
		{
			await EnsureClonedAsync(ct);
			if (await HasMainAsync(ct))
			{
				_ = await Git.RunAsync(await token.GetAsync(ct), ct, "fetch", "--quiet", "origin", "main");
				await RebaseOntoMainAsync(ct);
			}
			return Read();
		}
		finally
		{
			_ = _lock.Release();
		}
	}

	public async Task RecordAsync(Delivery delivery, CancellationToken ct)
	{
		await _lock.WaitAsync(ct);
		try
		{
			var path = Writable(delivery.Key.Campaign, PostsFormat.DeliveriesFile);
			var existing = fs.File.Exists(path) ? PostsFormat.ReadDeliveries(delivery.Key.Campaign, await fs.File.ReadAllTextAsync(path, ct)) : [];
			var updated = existing.Where(d => d.Key != delivery.Key).Append(delivery);
			await fs.File.WriteAllTextAsync(path, PostsFormat.WriteDeliveries(updated), ct);
			await CommitAsync(path, $"{delivery.Key}: {delivery.State.ToString().ToLowerInvariant()}", ct);
		}
		finally
		{
			_ = _lock.Release();
		}
	}

	public async Task SetHeldAsync(string slug, bool held, Actor by, CancellationToken ct)
	{
		await _lock.WaitAsync(ct);
		try
		{
			var path = Writable(slug, PostsFormat.CampaignFile);
			await fs.File.WriteAllTextAsync(path, PostsFormat.SetHeld(await fs.File.ReadAllTextAsync(path, ct), held), ct);
			await CommitAsync(path, $"{(held ? "Hold" : "Resume")} {slug}\n\nBy {by}.", ct);
		}
		finally
		{
			_ = _lock.Release();
		}
		await PushAsync(ct);
	}

	public Task StampAsync(string slug, Approval approval, CancellationToken ct) =>
		EditCampaignAsync(
			slug,
			md => PostsFormat.SetApproval(md, approval),
			$"Approve {slug}\n\nBy {approval.By}{(approval.NotBefore is { } nb ? $", not before {nb:yyyy-MM-dd HH:mm zzz}" : "")}.",
			ct
		);

	public Task SetRepeatAsync(string slug, int months, Actor by, CancellationToken ct) =>
		EditCampaignAsync(slug, md => PostsFormat.SetField(md, "repeat_months", months > 0 ? months.ToString(System.Globalization.CultureInfo.InvariantCulture) : null),
			months > 0 ? $"Repeat {slug} every {months} months\n\nBy {by}." : $"Stop repeating {slug}\n\nBy {by}.", ct);

	public Task MarkRepeatedAsync(string slug, DateTimeOffset at, CancellationToken ct) =>
		EditCampaignAsync(slug, md => PostsFormat.SetField(md, "last_repeat", at.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture)),
			$"{slug} was proposed again", ct);

	private async Task EditCampaignAsync(string slug, Func<string, string> edit, string message, CancellationToken ct)
	{
		await _lock.WaitAsync(ct);
		try
		{
			var path = Writable(slug, PostsFormat.CampaignFile);
			await fs.File.WriteAllTextAsync(path, edit(await fs.File.ReadAllTextAsync(path, ct)), ct);
			await CommitAsync(path, message, ct);
		}
		finally
		{
			_ = _lock.Release();
		}
		await PushAsync(ct);
	}

	public async Task UpdatePostAsync(string slug, Post post, Actor by, CancellationToken ct)
	{
		await _lock.WaitAsync(ct);
		try
		{
			var path = Writable(slug, $"{post.Id}.md");
			await fs.File.WriteAllTextAsync(path, PostsFormat.WritePost(post), ct);
			await CommitAsync(path, $"Edit {slug}/{post.Id}\n\nBy {by}.", ct);
		}
		finally
		{
			_ = _lock.Release();
		}
		await PushAsync(ct);
	}

	public async Task<byte[]?> ReadMediaAsync(string slug, string path, CancellationToken ct)
	{
		// Only files the campaign folder's rules allow: a path from a post file never reaches anything else in /config.
		var folder = fs.Path.Combine(Campaigns, slug);
		var media = CampaignFolder.Of(fs, folder, path);
		return CampaignFolderRules.All.Validate(media).Count == 0 && media.Files[0].Exists ? await fs.File.ReadAllBytesAsync(media.Files[0].FullName, ct) : null;
	}

	/// <summary>The full path of a file Braggarr writes in a campaign's folder, once the folder's rules allow it.</summary>
	private string Writable(string slug, string file)
	{
		var target = CampaignFolder.Of(fs, fs.Path.Combine(Campaigns, slug), file);
		var problems = CampaignFolderRules.All.Validate(target);
		return problems.Count == 0 ? target.Files[0].FullName : throw new InvalidOperationException($"campaigns/{slug}/{file}: {string.Join(" ", problems.Select(p => p.Message))}");
	}

	public async Task<IReadOnlyList<Criterion>> CriteriaAsync(CancellationToken ct)
	{
		var folder = fs.Path.Combine(Options.Directory, PostsFormat.JudgesFolder);
		if (!fs.Directory.Exists(folder))
		{
			return [];
		}
		var criteria = new List<Criterion>();
		foreach (var file in fs.Directory.GetFiles(folder, "*.md").Where(f => !fs.Path.GetFileName(f).StartsWith('.')).Order(StringComparer.Ordinal))
		{
			try
			{
				criteria.Add(PostsFormat.ReadCriterion(fs.Path.GetFileName(file), await fs.File.ReadAllTextAsync(file, ct)));
			}
			catch (SharpYaml.YamlException e)
			{
				// A criterion that cannot be read is not silently dropped: judging stops until it is fixed, so nothing is judged by less than the owner wrote.
				throw new FormatException($"judges/{fs.Path.GetFileName(file)}: the front matter cannot be read: {e.Message}", e);
			}
		}
		if (criteria.GroupBy(c => c.Name).FirstOrDefault(g => g.Count() > 1) is { } twice)
		{
			throw new FormatException($"Two criteria in judges/ are named \"{twice.Key}\"; give each its own name.");
		}
		return criteria;
	}

	public async Task<IReadOnlyList<ProjectBrief>> BriefsAsync(CancellationToken ct)
	{
		var folder = fs.Path.Combine(Options.Directory, PostsFormat.ProjectsFolder);
		if (!fs.Directory.Exists(folder))
		{
			return [];
		}
		var briefs = new List<ProjectBrief>();
		foreach (var file in fs.Directory.GetFiles(folder, "*.md").Where(f => !fs.Path.GetFileName(f).StartsWith('.')).Order(StringComparer.Ordinal))
		{
			try
			{
				briefs.Add(PostsFormat.ReadBrief(fs.Path.GetFileName(file), await fs.File.ReadAllTextAsync(file, ct)));
			}
			catch (SharpYaml.YamlException e)
			{
				// One broken brief is the owner's to fix; it does not take the other projects' briefs with it.
				logger.LogWarning("projects/{File} cannot be read and is left out: {Problem}", fs.Path.GetFileName(file), e.Message);
			}
		}
		return briefs;
	}

	public async Task SetBriefAsync(ProjectBrief brief, Actor by, CancellationToken ct)
	{
		// The slug is letters, digits and dashes only, so the file stays in projects/.
		var slug = ProjectBrief.Slug(brief.Project);
		if (slug.Length == 0)
		{
			throw new InvalidOperationException($"\"{brief.Project}\" has no letters or digits to name its brief by.");
		}
		await _lock.WaitAsync(ct);
		try
		{
			var folder = fs.Path.Combine(Options.Directory, PostsFormat.ProjectsFolder);
			_ = fs.Directory.CreateDirectory(folder);
			var path = fs.Path.Combine(folder, slug + ".md");
			var text = PostsFormat.WriteBrief(brief);
			if (fs.File.Exists(path) && await fs.File.ReadAllTextAsync(path, ct) == text)
			{
				return;
			}
			await fs.File.WriteAllTextAsync(path, text, ct);
			await CommitAsync(path, $"Brief for {brief.Project}\n\nBy {by}.", ct);
		}
		finally
		{
			_ = _lock.Release();
		}
		await PushAsync(ct);
	}

	public async Task SetJudgementAsync(string slug, Judgement judgement, CancellationToken ct)
	{
		await _lock.WaitAsync(ct);
		try
		{
			var path = Writable(slug, PostsFormat.JudgementFile);
			await fs.File.WriteAllTextAsync(path, PostsFormat.WriteJudgement(judgement), ct);
			await CommitAsync(path, $"Judge {slug}: {(judgement.Blocked ? "blocked" : judgement.AllPassed ? "passed" : "warnings")}", ct);
		}
		finally
		{
			_ = _lock.Release();
		}
		await PushAsync(ct);
	}

	public async Task<string?> VoiceAsync(CancellationToken ct)
	{
		var path = fs.Path.Combine(Options.Directory, "voice.md");
		return fs.File.Exists(path) ? await fs.File.ReadAllTextAsync(path, ct) : null;
	}

	public async Task PushAsync(CancellationToken ct)
	{
		await _lock.WaitAsync(ct);
		try
		{
			if (!await HasMainAsync(ct) || await Git.RunAsync(null, ct, "rev-list", "--count", "origin/main..HEAD") == "0")
			{
				return;
			}
			var auth = await token.GetAsync(ct);
			try
			{
				_ = await Git.RunAsync(auth, ct, "push", "--quiet", "origin", "HEAD:main");
			}
			catch (GitException)
			{
				// Someone pushed in between: take their commits under ours, and try once more.
				_ = await Git.RunAsync(auth, ct, "fetch", "--quiet", "origin", "main");
				await RebaseOntoMainAsync(ct);
				_ = await Git.RunAsync(auth, ct, "push", "--quiet", "origin", "HEAD:main");
			}
		}
		finally
		{
			_ = _lock.Release();
		}
	}

	private RepositoryState Read()
	{
		if (!fs.Directory.Exists(Campaigns))
		{
			return new([], []);
		}
		var campaigns = new List<Campaign>();
		var deliveries = new List<Delivery>();
		foreach (var folder in fs.Directory.GetDirectories(Campaigns).Order(StringComparer.Ordinal))
		{
			var slug = fs.Path.GetFileName(folder);
			try
			{
				// A hidden file (.notes.md) is the owner's, not a post; the scoped file system would refuse to read it anyway.
				var files = fs.Directory.GetFiles(folder, "*.*")
					.Where(f => !fs.Path.GetFileName(f).StartsWith('.') && (f.EndsWith(".md", StringComparison.Ordinal) || f.EndsWith(".yml", StringComparison.Ordinal))).ToDictionary(f => fs.Path.GetFileName(f), fs.File.ReadAllText, StringComparer.Ordinal);
				if (!files.ContainsKey(PostsFormat.CampaignFile))
				{
					continue;
				}
				campaigns.Add(PostsFormat.ReadCampaign(new CampaignFiles(slug, files), CampaignStatus.Approved));
				deliveries.AddRange(PostsFormat.ReadDeliveries(slug, files.GetValueOrDefault(PostsFormat.DeliveriesFile)));
			}
			catch (Exception e) when (e is FormatException or SharpYaml.YamlException)
			{
				logger.LogWarning("campaigns/{Slug} cannot be read and is skipped: {Problem}", slug, e.Message);
			}
		}
		return new(campaigns, deliveries);
	}

	private async Task EnsureClonedAsync(CancellationToken ct)
	{
		if (fs.Directory.Exists(fs.Path.Combine(Options.Directory, ".git")))
		{
			return;
		}
		_ = fs.Directory.CreateDirectory(Options.Directory);
		_ = await Git.RunAsync(await token.GetAsync(ct), ct, "clone", "--quiet", Options.Url, ".");
	}

	private async Task<bool> HasMainAsync(CancellationToken ct)
	{
		try
		{
			_ = await Git.RunAsync(null, ct, "rev-parse", "--verify", "--quiet", "origin/main");
			return true;
		}
		catch (GitException)
		{
			// An empty repo: nothing on main yet. The first commit is pushed to create it.
			return false;
		}
	}

	private async Task RebaseOntoMainAsync(CancellationToken ct)
	{
		try
		{
			_ = await Git.RunAsync(null, ct, "rebase", "--quiet", "origin/main");
		}
		catch (GitException e)
		{
			_ = await Git.RunAsync(null, CancellationToken.None, "rebase", "--abort");
			logger.LogError("The posts repo could not be rebased onto main; Braggarr keeps its own commits and tries again later. {Problem}", e.Message);
		}
	}

	private async Task CommitAsync(string path, string message, CancellationToken ct)
	{
		_ = await Git.RunAsync(null, ct, "add", "--", fs.Path.GetRelativePath(Options.Directory, path));
		_ = await Git.RunAsync(null, ct, "commit", "--quiet", "-m", message);
	}

	public void Dispose() => _lock.Dispose();
}
