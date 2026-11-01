using Braggarr.Adapters;
using Braggarr.Adapters.Git;
using Braggarr.Adapters.Posts;
using Braggarr.Domain;
using Braggarr.Services;
using Braggarr.Services.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nullean.Entities;

namespace Braggarr.Web.Tests;

/// <summary>
/// The scheduler on what runs in production: the real <see cref="GitCampaignRepository"/>, on a real clone of a bare repo that
/// stands for GitHub, through the scoped file system, on a manual clock. Only the networks are fakes. A crash throws away the
/// service and the repository instance, so what survives it is exactly what git committed to the clone.
/// </summary>
public sealed partial class GitSchedulerWorld : IDisposable
{
	private readonly TimeProvider _time;
	private readonly string _root = Directory.CreateTempSubdirectory("braggarr-world-").FullName;
	private CrashingRepository _repository;

	/// <summary>
	/// The repos for a start time, built once: a bare remote with two approved campaigns, the owner's clone and Braggarr's.
	/// Each world copies them and points the clones at its own remote, so no two worlds share a repository.
	/// </summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<DateTimeOffset, Lazy<string>> Templates = new();

	public GitSchedulerWorld(TimeProvider time)
	{
		_time = time;
		var template = Templates.GetOrAdd(time.GetUtcNow(), start => new Lazy<string>(() => Build(start))).Value;
		Copy(template, _root);
		GitFixture.Git(Owner, "remote", "set-url", "origin", Remote);
		GitFixture.Git(Path.Combine(_root, "braggarr"), "remote", "set-url", "origin", Remote);
		_repository = Open();
		Service = Create();
		Service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
	}

	private static string Build(DateTimeOffset start)
	{
		var root = Directory.CreateTempSubdirectory("braggarr-template-").FullName;
		var remote = Path.Combine(root, "remote.git");
		var owner = Path.Combine(root, "owner");
		GitFixture.Git(root, "init", "--quiet", "--bare", "--initial-branch=main", remote);
		GitFixture.Git(root, "clone", "--quiet", remote, owner);
		foreach (var campaign in new[] { Campaign(start, "curb-1-0", hoursFromNow: 0), Campaign(start, "heed-0-20", hoursFromNow: 2) })
		{
			var folder = Directory.CreateDirectory(Path.Combine(owner, "campaigns", campaign.Slug)).FullName;
			foreach (var (name, content) in PostsFormat.WriteCampaign(campaign))
			{
				File.WriteAllText(Path.Combine(folder, name), content);
			}
		}
		GitFixture.Commit(owner, "Approve two campaigns");
		GitFixture.Git(root, "clone", "--quiet", remote, Path.Combine(root, "braggarr"));
		AppDomain.CurrentDomain.ProcessExit += (_, _) =>
		{
			try
			{
				Directory.Delete(root, recursive: true);
			}
			catch (IOException)
			{
				// Left for the OS to clean, like any temporary folder.
			}
		};
		return root;
	}

	private static void Copy(string from, string to)
	{
		foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
		{
			_ = Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
		}
		foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
		{
			File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
		}
	}

	private string Remote => Path.Combine(_root, "remote.git");

	private string Owner => Path.Combine(_root, "owner");

	public FakePublisher Bluesky { get; } = new(Network.Bluesky);

	public FakePublisher Mastodon { get; } = new(Network.Mastodon);

	public FakeManualPosts Manual { get; } = new();

	public FakeNotifier Notifier { get; } = new();

	public FakeJournal Journal { get; } = new();

	public FakeSites Sites { get; } = new();

	public SchedulingService Service { get; private set; }

	public int Restarts { get; private set; }

	public int OwnerEdits { get; private set; }

	public IEnumerable<Timestamped<DeliveryEvent>> Log => Journal.Events;

	/// <summary>Dies right after the next "sending" is committed to the clone, before the network is called.</summary>
	public void CrashWhileSending() => _repository.CrashAfterNextSending = true;

	public async Task TickAsync()
	{
		try
		{
			await Service.TickAsync(CancellationToken.None);
		}
		catch (CrashException)
		{
			await RestartAsync();
		}
	}

	/// <summary>A new process: a new service and a new repository instance, with nothing but the clone on disk to go on.</summary>
	public async Task RestartAsync()
	{
		Service.Dispose();
		_repository.Dispose();
		_repository = Open();
		Service = Create();
		Restarts++;
		await Service.StartAsync(CancellationToken.None);
	}

	/// <summary>The owner pushes an edit to a post on main while Braggarr is recording deliveries: a rebase must never conflict.</summary>
	public void OwnerEditsAPost()
	{
		GitFixture.Git(Owner, "pull", "--quiet", "--rebase", "origin", "main");
		var path = Path.Combine(Owner, "campaigns", "curb-1-0", "02-follow-up.md");
		var edited = FollowUp().Replace(File.ReadAllText(path), $"A day in, edit {++OwnerEdits}.");
		File.WriteAllText(path, edited);
		GitFixture.Commit(Owner, $"Edit curb-1-0 ({OwnerEdits})");
	}

	/// <summary>The owner approves again what an edit on main changed, through the real repository, as the UI's button does.</summary>
	public async Task OwnerApprovesAgainAsync()
	{
		// A tick first, so the service has read the edit; through the world, so a crash planted for it restarts as any other.
		await TickAsync();
		foreach (var campaign in Service.Current.Campaigns.Where(c => c.NeedsApproval))
		{
			await _repository.StampAsync(campaign.Slug, new Approval(Actor.Owner("mpdreamz").ToString(), campaign.Digest, _time.GetUtcNow()), CancellationToken.None);
		}
		Service.RequestSync();
	}

	private CrashingRepository Open() =>
		new(new GitCampaignRepository(
			new GitFixture.FixedOptions(new PostsRepositoryOptions { Repository = "me/posts", Directory = Path.Combine(_root, "braggarr"), CloneUrl = Remote }),
			new GitFixture.NoToken(),
			ConfigFileSystem.Create(_root),
			NullLogger<GitCampaignRepository>.Instance
		));

	private SchedulingService Create() =>
		new(
			_repository,
			new FakePublishers(Bluesky, Mastodon),
			new FakeAccounts(),
			Manual,
			Notifier,
			Journal,
			new FakeVideoStore(),
			Sites,
			new FakeVisibility(),
			_time,
			Options.Create(new SchedulingOptions { SyncInterval = TimeSpan.FromMinutes(5), PublishTimeout = TimeSpan.FromSeconds(60) }),
			NullLogger<SchedulingService>.Instance
		);

	private static Campaign Campaign(DateTimeOffset start, string slug, int hoursFromNow) =>
		new(
			slug, slug, "curb", CampaignStatus.Approved, [Network.Bluesky, Network.Mastodon, Network.Reddit], [], "", "agent:release-bot",
			[
				new Post("01-launch", start.AddHours(hoursFromNow), [
					new Variant(Network.Bluesky, $"{slug} is out."),
					new Variant(Network.Mastodon, $"{slug} is out."),
					new Variant(Network.Reddit, $"{slug} is out\nhttps://github.com/nullean/curb")
				]),
				new Post("02-follow-up", start.AddHours(hoursFromNow + 24), [
					new Variant(Network.Bluesky, "A day in."),
					new Variant(Network.Mastodon, "A day in."),
					new Variant(Network.Reddit, "A day in")
				])
			]
		);


	public void Dispose()
	{
		Service.Dispose();
		_repository.Dispose();
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// A git process may still hold a file for a moment; the temporary folder is cleaned by the OS.
		}
	}

	[System.Text.RegularExpressions.GeneratedRegex(@"A day in(, edit \d+)?\.")]
	private static partial System.Text.RegularExpressions.Regex FollowUp();

	/// <summary>The real repository, with one way to die: right after a "sending" record is committed.</summary>
	private sealed class CrashingRepository(GitCampaignRepository inner) : ICampaignRepository, IDisposable
	{
		public bool CrashAfterNextSending { get; set; }

		public async Task RecordAsync(Delivery delivery, CancellationToken ct)
		{
			await inner.RecordAsync(delivery, ct);
			if (delivery.State == DeliveryState.Sending && CrashAfterNextSending)
			{
				CrashAfterNextSending = false;
				throw new CrashException();
			}
		}

		public Task<RepositoryState> PullAsync(CancellationToken ct) => inner.PullAsync(ct);

		public Task PushAsync(CancellationToken ct) => inner.PushAsync(ct);

		public Task<string?> VoiceAsync(CancellationToken ct) => inner.VoiceAsync(ct);

		public Task<IReadOnlyList<Criterion>> CriteriaAsync(CancellationToken ct) => inner.CriteriaAsync(ct);

		public Task<IReadOnlyList<ProjectBrief>> BriefsAsync(CancellationToken ct) => inner.BriefsAsync(ct);

		public Task SetBriefAsync(ProjectBrief brief, Actor by, CancellationToken ct) => inner.SetBriefAsync(brief, by, ct);

		public Task SetJudgementAsync(string slug, Judgement judgement, CancellationToken ct) => inner.SetJudgementAsync(slug, judgement, ct);

		public Task<byte[]?> ReadMediaAsync(string slug, string path, CancellationToken ct) => inner.ReadMediaAsync(slug, path, ct);

		public Task UpdatePostAsync(string slug, Post post, Actor by, CancellationToken ct) => inner.UpdatePostAsync(slug, post, by, ct);

		public Task SetRepeatAsync(string slug, int months, Actor by, CancellationToken ct) => inner.SetRepeatAsync(slug, months, by, ct);

		public Task MarkRepeatedAsync(string slug, DateTimeOffset at, CancellationToken ct) => inner.MarkRepeatedAsync(slug, at, ct);

		public Task SetHeldAsync(string slug, bool held, Actor by, CancellationToken ct) => inner.SetHeldAsync(slug, held, by, ct);

		public Task StampAsync(string slug, Approval approval, CancellationToken ct) => inner.StampAsync(slug, approval, ct);

		public void Dispose() => inner.Dispose();
	}
}
