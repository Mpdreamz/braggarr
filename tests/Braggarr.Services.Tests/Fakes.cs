using Braggarr.Domain;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

/// <summary>Thrown by the fake repository to stand for the process dying at that moment.</summary>
public sealed class CrashException : Exception;

/// <summary>The posts repo in memory. What it holds survives a "crash": only the service instance is thrown away.</summary>
public sealed class FakeRepository : ICampaignRepository
{
	public List<Campaign> Campaigns { get; } = [];

	public Dictionary<DeliveryKey, Delivery> Deliveries { get; } = [];

	/// <summary>Dies right after the next "sending" record is committed, before the network call.</summary>
	public bool CrashAfterNextSending { get; set; }

	public int Pushes { get; private set; }

	public Task<RepositoryState> PullAsync(CancellationToken ct) =>
		Task.FromResult(new RepositoryState([.. Campaigns], [.. Deliveries.Values]));

	public Task RecordAsync(Delivery delivery, CancellationToken ct)
	{
		Deliveries[delivery.Key] = delivery;
		if (delivery.State == DeliveryState.Sending && CrashAfterNextSending)
		{
			CrashAfterNextSending = false;
			throw new CrashException();
		}
		return Task.CompletedTask;
	}

	public Task PushAsync(CancellationToken ct)
	{
		Pushes++;
		return Task.CompletedTask;
	}

	public string? Voice { get; set; }

	public Dictionary<(string Slug, string Path), byte[]> Media { get; } = [];

	public Task<byte[]?> ReadMediaAsync(string slug, string path, CancellationToken ct) => Task.FromResult(Media.GetValueOrDefault((slug, path)));

	public Task<string?> VoiceAsync(CancellationToken ct) => Task.FromResult(Voice);

	/// <summary>The briefs in <c>projects/</c> on main.</summary>
	public List<ProjectBrief> Briefs { get; } = [];

	public Task<IReadOnlyList<ProjectBrief>> BriefsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProjectBrief>>([.. Briefs]);

	public Task SetBriefAsync(ProjectBrief brief, Actor by, CancellationToken ct)
	{
		_ = Briefs.RemoveAll(b => ProjectBrief.Slug(b.Project) == ProjectBrief.Slug(brief.Project));
		Briefs.Add(brief);
		return Task.CompletedTask;
	}

	/// <summary>The criteria in <c>judges/</c> on main.</summary>
	public List<Criterion> Criteria { get; } = [];

	/// <summary>Set: reading the criteria fails with this, as a file the owner broke would.</summary>
	public string? CriteriaProblem { get; set; }

	public Task<IReadOnlyList<Criterion>> CriteriaAsync(CancellationToken ct) =>
		CriteriaProblem is { } problem ? throw new FormatException(problem) : Task.FromResult<IReadOnlyList<Criterion>>([.. Criteria]);

	public Task SetJudgementAsync(string slug, Judgement judgement, CancellationToken ct)
	{
		var i = Campaigns.FindIndex(c => c.Slug == slug);
		Campaigns[i] = Campaigns[i] with { Judgement = judgement };
		return Task.CompletedTask;
	}

	public List<(string Slug, Post Post)> PostEdits { get; } = [];

	public Task UpdatePostAsync(string slug, Post post, Actor by, CancellationToken ct)
	{
		PostEdits.Add((slug, post));
		var i = Campaigns.FindIndex(c => c.Slug == slug);
		Campaigns[i] = Campaigns[i] with { Posts = Campaigns[i].Posts.Replace(p => p.Id == post.Id, post) };
		return Task.CompletedTask;
	}

	public Task SetRepeatAsync(string slug, int months, Actor by, CancellationToken ct)
	{
		var i = Campaigns.FindIndex(c => c.Slug == slug);
		Campaigns[i] = Campaigns[i] with { RepeatMonths = months };
		return Task.CompletedTask;
	}

	public Task MarkRepeatedAsync(string slug, DateTimeOffset at, CancellationToken ct)
	{
		var i = Campaigns.FindIndex(c => c.Slug == slug);
		Campaigns[i] = Campaigns[i] with { LastRepeat = at };
		return Task.CompletedTask;
	}

	public List<(string Slug, Approval Approval)> Stamps { get; } = [];

	public Task StampAsync(string slug, Approval approval, CancellationToken ct)
	{
		Stamps.Add((slug, approval));
		var i = Campaigns.FindIndex(c => c.Slug == slug);
		Campaigns[i] = Campaigns[i] with { Approval = approval };
		return Task.CompletedTask;
	}

	public Task SetHeldAsync(string slug, bool held, Actor by, CancellationToken ct)
	{
		var i = Campaigns.FindIndex(c => c.Slug == slug);
		Campaigns[i] = Campaigns[i] with { Status = held ? CampaignStatus.Held : CampaignStatus.Approved };
		return Task.CompletedTask;
	}
}

public enum Answer
{
	Posted,
	Refused,
	Silent,
	Hangs
}

public sealed class FakePublisher(Network network, string account = "me") : IPublisher
{
	public string Account { get; } = account;

	private int _posted;

	public Network Network { get; } = network;

	public Queue<Answer> Next { get; } = new();

	public List<Variant> Posted { get; } = [];

	public List<PublishRequest> Requests { get; } = [];

	public async Task<Receipt> PublishAsync(PublishRequest request, CancellationToken ct)
	{
		Requests.Add(request);
		var variant = request.Variant;
		switch (Next.Count > 0 ? Next.Dequeue() : Answer.Posted)
		{
			case Answer.Refused:
				throw new PublishException("400: the network refused the post.");
			case Answer.Silent:
				Posted.Add(variant); // it may well have gone out
				throw new HttpRequestException("The connection was reset.");
			case Answer.Hangs:
				await Task.Delay(Timeout.InfiniteTimeSpan, ct);
				break;
		}
		Posted.Add(variant);
		var id = Interlocked.Increment(ref _posted);
		return new Receipt($"{id}", $"https://{Network.Slug()}.example/{id}");
	}
}

/// <summary>Each publisher posts as its <see cref="FakePublisher.Account"/>: a post for another account finds none.</summary>
public sealed class FakePublishers(params FakePublisher[] publishers) : IPublishers
{
	public IPublisher? Find(Network network, string account) => publishers.FirstOrDefault(p => p.Network == network && p.Account == account);
}

/// <summary>
/// One account per network, <c>me</c>, unless a test connects more; the defaults pick by project, then org, then globally,
/// like the real ones, from what the test sets.
/// </summary>
public sealed class FakeAccounts : IAccounts
{
	public Dictionary<Network, List<string>> Accounts { get; } = Networks.All.Where(n => !n.IsManual()).ToDictionary(n => n, _ => new List<string> { "me" });

	public Dictionary<(Network, string), string> ProjectDefaults { get; } = [];

	public IReadOnlyList<string> Connected(Network network) => Accounts.GetValueOrDefault(network) ?? [];

	public AccountChoice? Choose(Network network, string project) =>
		ProjectDefaults.TryGetValue((network, project), out var chosen) ? new AccountChoice(network, chosen, $"{project}'s default")
		: Connected(network) is [var first, ..] ? new AccountChoice(network, first, "the global default")
		: null;
}

public sealed class FakeManualPosts : IManualPosts
{
	private int _next = 100;

	public HashSet<int> Open { get; } = [];

	public HashSet<int> Closed { get; } = [];

	public Task<ManualPost> OpenAsync(Campaign campaign, Post post, Variant variant, CancellationToken ct)
	{
		var issue = _next++;
		_ = Open.Add(issue);
		return Task.FromResult(new ManualPost(issue, $"https://github.com/me/posts/issues/{issue}", [new SubmitLink("Submit to Reddit", "https://www.reddit.com/submit?url=x")]));
	}

	public Task CloseAsync(int issue, CancellationToken ct)
	{
		_ = Open.Remove(issue);
		_ = Closed.Add(issue);
		return Task.CompletedTask;
	}

	public void CloseAll()
	{
		Closed.UnionWith(Open);
		Open.Clear();
	}

	public Task<IReadOnlySet<int>> ClosedAsync(IReadOnlyCollection<int> issues, CancellationToken ct) =>
		Task.FromResult<IReadOnlySet<int>>(issues.Where(Closed.Contains).ToHashSet());
}

public sealed class FakeReviews : IReviews
{
	public List<Proposal> Open { get; } = [];

	public List<(Proposal Proposal, Actor By)> Merged { get; } = [];

	public List<(Proposal Proposal, Actor By)> Closed { get; } = [];

	public Task<Proposal> ProposeAsync(Campaign campaign, Actor by, CancellationToken ct)
	{
		var proposal = new Proposal(Open.Count + 1, $"https://github.com/me/posts/pull/{Open.Count + 1}", campaign);
		Open.Add(proposal);
		return Task.FromResult(proposal);
	}

	public Task<IReadOnlyList<Proposal>> OpenAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Proposal>>([.. Open]);

	public Dictionary<(int Number, string Path), byte[]> Media { get; } = [];

	public Task<byte[]?> ReadMediaAsync(Proposal proposal, string path, CancellationToken ct) => Task.FromResult(Media.GetValueOrDefault((proposal.Number, path)));

	public Task<Proposal> UpdateAsync(Proposal proposal, Campaign campaign, Actor by, CancellationToken ct, IReadOnlyDictionary<string, byte[]>? media = null)
	{
		foreach (var (path, bytes) in media ?? new Dictionary<string, byte[]>())
		{
			Media[(proposal.Number, path)] = bytes;
		}
		var updated = proposal with { Campaign = campaign };
		Open[Open.IndexOf(proposal)] = updated;
		return Task.FromResult(updated);
	}

	public Task MergeAsync(Proposal proposal, Actor by, CancellationToken ct)
	{
		_ = Open.Remove(proposal);
		Merged.Add((proposal, by));
		return Task.CompletedTask;
	}

	public Task CloseAsync(Proposal proposal, Actor by, CancellationToken ct)
	{
		_ = Open.Remove(proposal);
		Closed.Add((proposal, by));
		return Task.CompletedTask;
	}
}

public sealed class FakeNotifier : INotifier
{
	public List<Notice> Sent { get; } = [];

	public Task NotifyAsync(Notice notice, CancellationToken ct)
	{
		Sent.Add(notice);
		return Task.CompletedTask;
	}
}

public sealed class FakeJournal : IDeliveryJournal
{
	public List<Timestamped<DeliveryEvent>> Events { get; } = [];

	public void Record(DeliveryEvent change, DateTimeOffset at) => Events.Add(new(at, change));
}

/// <summary>Videos in memory, named by hash as the file store names them.</summary>
public sealed class FakeVideoStore : IVideoStore
{
	public Dictionary<string, byte[]> Files { get; } = [];

	public Task<MediaRef> SaveAsync(byte[] bytes, string alt, CancellationToken ct)
	{
		var reference = MediaRef.Video(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), alt);
		Files[reference.Path] = bytes;
		return Task.FromResult(reference);
	}

	public Task<byte[]?> ReadAsync(string path, CancellationToken ct) => Task.FromResult(Files.GetValueOrDefault(path));
}

/// <summary>Profiles kept in memory: what each account shows, and every change applied, in order.</summary>
public sealed class FakeProfiles : IProfiles
{
	public Dictionary<(Network, string), AccountProfile> Shown { get; } = [];

	public List<(Network Network, string Account, ProfileChange Change)> Applied { get; } = [];

	/// <summary>Set: the network refuses with this.</summary>
	public string? Refuses { get; set; }

	public Task<AccountProfile> ReadAsync(Network network, string account, CancellationToken ct) =>
		Shown.TryGetValue((network, account), out var profile) ? Task.FromResult(profile) : throw new PublishException($"{network} would not say.");

	public Task ApplyAsync(Network network, string account, ProfileChange change, CancellationToken ct)
	{
		if (Refuses is { } refusal)
		{
			throw new PublishException(refusal);
		}
		Applied.Add((network, account, change));
		var now = Shown[(network, account)];
		Shown[(network, account)] = now with { DisplayName = change.DisplayName ?? now.DisplayName, Bio = change.Bio ?? now.Bio, Link = change.Link ?? now.Link };
		return Task.CompletedTask;
	}
}

/// <summary>Blog addresses that answer: none until a test says so.</summary>
public sealed class FakeSites : ISites
{
	public HashSet<string> Answering { get; } = [];

	public Task<bool> AnswersAsync(string url, CancellationToken ct) => Task.FromResult(Answering.Contains(url));
}

/// <summary>Every project public, unless a test says otherwise.</summary>
public sealed class FakeVisibility : IProjectVisibility
{
	public ProjectVisibility Visibility { get; set; } = ProjectVisibility.Public;

	public Task<ProjectVisibility> OfAsync(Campaign campaign, CancellationToken ct) => Task.FromResult(Visibility);
}
