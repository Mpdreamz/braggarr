using System.Net;
using System.Text;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Web.Tests;

/// <summary>An HTTP server in a delegate: records every request with its body, answers with what the test says.</summary>
public sealed class StubHttp(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
{
	public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

	public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
		new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
		lock (Requests)
		{
			Requests.Add((request, body));
		}
		return answer(request, body);
	}
}

public sealed class MemoryRepository : ICampaignRepository
{
	public List<Campaign> Campaigns { get; } = [];

	public Dictionary<DeliveryKey, Delivery> Deliveries { get; } = [];

	public Task<RepositoryState> PullAsync(CancellationToken ct) => Task.FromResult(new RepositoryState([.. Campaigns], [.. Deliveries.Values]));

	public Task RecordAsync(Delivery delivery, CancellationToken ct)
	{
		Deliveries[delivery.Key] = delivery;
		return Task.CompletedTask;
	}

	public Task PushAsync(CancellationToken ct) => Task.CompletedTask;

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

	public Task<IReadOnlyList<Criterion>> CriteriaAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Criterion>>([.. Criteria]);

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
		if (i >= 0)
		{
			Campaigns[i] = Campaigns[i] with { Posts = [.. Campaigns[i].Posts.Select(p => p.Id == post.Id ? post : p)] };
		}
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

	public Task SetHeldAsync(string slug, bool held, Actor by, CancellationToken ct) => Task.CompletedTask;

	public Task StampAsync(string slug, Approval approval, CancellationToken ct)
	{
		var i = Campaigns.FindIndex(c => c.Slug == slug);
		if (i >= 0)
		{
			Campaigns[i] = Campaigns[i] with { Approval = approval };
		}
		return Task.CompletedTask;
	}
}

public sealed class MemoryReviews : IReviews
{
	public List<Proposal> Open { get; } = [];

	public List<Actor> Merged { get; } = [];

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
		Merged.Add(by);
		return Task.CompletedTask;
	}

	public Task CloseAsync(Proposal proposal, Actor by, CancellationToken ct)
	{
		_ = Open.Remove(proposal);
		return Task.CompletedTask;
	}
}

public sealed class NoManualPosts : IManualPosts
{
	public Task<ManualPost> OpenAsync(Campaign campaign, Post post, Variant variant, CancellationToken ct) =>
		Task.FromResult(new ManualPost(1, "https://github.com/me/posts/issues/1", []));

	public Task CloseAsync(int issue, CancellationToken ct) => Task.CompletedTask;

	public Task<IReadOnlySet<int>> ClosedAsync(IReadOnlyCollection<int> issues, CancellationToken ct) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
}

/// <summary>Writes "&lt;title&gt; on &lt;network&gt;." for every target, so drafting can be seen without a model.</summary>
public sealed class EchoDrafter : IDrafter, IDrafters
{
	public List<DraftRequest> Requests { get; } = [];

	public IDrafter? Current => this;

	public Task<DraftResult> DraftAsync(DraftRequest request, CancellationToken ct)
	{
		Requests.Add(request);
		return Task.FromResult(new DraftResult(
			[.. request.Targets.Select(t => new DraftedVariant(t.Post, new Variant(t.Network, request.Note is null ? $"{request.Title} on {t.Network}." : $"{request.Note} ({t.Network})")))],
			new Usage("claude-opus-5-5", 100, 10, 0, 0)
		));
	}

	public Task<(ProfileSuggestion Suggestion, Usage Usage)> ProfileAsync(ProfileRequest request, CancellationToken ct) =>
		Task.FromResult((new ProfileSuggestion(request.Current.DisplayName ?? request.Current.Account, $"I make {string.Join(", ", request.About.Select(a => a.Project))}.",
			request.About.SelectMany(a => a.Links).Select(l => l.Url).FirstOrDefault(), "Says what you make."), new Usage("claude-opus-5-5", 1, 1, 0, 0)));

	public Task<(ProjectBrief Brief, Usage Usage)> BriefAsync(RepositoryInfo repository, CancellationToken ct) =>
		Task.FromResult((new ProjectBrief(repository.Name, $"{repository.Name}: {repository.Description}", "It keeps diffs small.", "C# teams")
		{
			Links = [new BriefLink("Repository", $"https://github.com/{repository.Repository}")]
		}, new Usage("claude-opus-5-5", 1, 1, 0, 0)));

	public Task<(CampaignPlan Plan, Usage Usage)> PlanAsync(RepositoryInfo repository, CancellationToken ct) =>
		Task.FromResult((new CampaignPlan($"Introducing {repository.Name}", repository.Description, [new TalkingPoint("What it is"), new TalkingPoint("The CLI")]),
			new Usage("claude-opus-5-5", 1, 1, 0, 0)));

	public Task<(IReadOnlyList<TalkingPoint> Points, Usage Usage)> PointsAsync(PointsRequest request, CancellationToken ct) =>
		Task.FromResult<(IReadOnlyList<TalkingPoint>, Usage)>(([new TalkingPoint(request.Title, [new WantedMedia(MediaKind.Image, "A screenshot of it working")]), new TalkingPoint("A detail")],
			new Usage("claude-opus-5-5", 1, 1, 0, 0)));

	public Task<(ReleaseVerdict Verdict, Usage Usage)> JudgeAsync(string project, Release release, CancellationToken ct) =>
		Task.FromResult((new ReleaseVerdict(true, "A feature.", null), new Usage("claude-opus-5-5", 1, 1, 0, 0)));

	public Task<(string Alt, Usage Usage)> DescribeAsync(MediaFile image, string context, CancellationToken ct) =>
		Task.FromResult(("A screenshot of the project.", new Usage("claude-opus-5-5", 100, 10, 0, 0)));
}

/// <summary>
/// A clock for code that waits: every timer fires at once, so a poll loop runs without sleeping, and each wait asked for is
/// recorded, so a test can say how long the code meant to wait. The time it reads moves on by each wait.
/// </summary>
public sealed class InstantTime(DateTimeOffset start) : TimeProvider
{
	private long _ticks = start.UtcTicks;

	public List<TimeSpan> Waits { get; } = [];

	public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

	public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
	{
		if (dueTime != Timeout.InfiniteTimeSpan && dueTime > TimeSpan.Zero)
		{
			lock (Waits)
			{
				Waits.Add(dueTime);
			}
			_ = Interlocked.Add(ref _ticks, dueTime.Ticks);
		}
		return System.CreateTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.Zero, period);
	}
}

/// <summary>No judge until a test sets one; the set judge passes everything it is asked about unless told otherwise.</summary>
public sealed class StubJudges : IJudges, IJudge
{
	public bool On { get; set; }

	public IJudge? Current => On ? this : null;

	public string Model => "stub-judge";

	public Func<JudgeRequest, IReadOnlyList<Finding>> Says { get; set; } = r =>
		[.. Criteria.Expected(r.Campaign, r.Criteria).Select(e => new Finding(e.Criterion.Name, e.Post, e.Network, Passed: true, Blocks: false, "Fine."))];

	public Task<(IReadOnlyList<Finding> Findings, Usage Usage)> JudgeAsync(JudgeRequest request, CancellationToken ct) =>
		Task.FromResult((Says(request), new Usage(Model, 100, 10, 0, 0)));
}

/// <summary>Profiles in memory for the web tests: what each account shows, and what was applied.</summary>
public sealed class MemoryProfiles : IProfiles
{
	public Dictionary<(Network, string), AccountProfile> Shown { get; } = [];

	public List<(Network Network, string Account, ProfileChange Change)> Applied { get; } = [];

	public Task<AccountProfile> ReadAsync(Network network, string account, CancellationToken ct) =>
		Shown.TryGetValue((network, account), out var profile) ? Task.FromResult(profile) : throw new PublishException($"{network} would not say.");

	public Task ApplyAsync(Network network, string account, ProfileChange change, CancellationToken ct)
	{
		Applied.Add((network, account, change));
		var now = Shown[(network, account)];
		Shown[(network, account)] = now with { DisplayName = change.DisplayName ?? now.DisplayName, Bio = change.Bio ?? now.Bio, Link = change.Link ?? now.Link };
		return Task.CompletedTask;
	}
}
