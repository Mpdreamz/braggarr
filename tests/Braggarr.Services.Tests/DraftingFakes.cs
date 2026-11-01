using Braggarr.Domain;
using Nullean.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Braggarr.Services.Tests;

/// <summary>A drafter that answers from a script: for each target, the text the test chose, or a default that fits.</summary>
public sealed class FakeDrafter : IDrafter
{
	public List<DraftRequest> Requests { get; } = [];

	/// <summary>Answers by (post, network), consumed in order: a long one first, then a short one, and so on.</summary>
	public Dictionary<(string Post, Network Network), Queue<string>> Script { get; } = [];

	/// <summary>The blog articles' fields to answer with, in order; a title and description that fit once these run out.</summary>
	public Queue<Article> Articles { get; } = new();

	public Task<DraftResult> DraftAsync(DraftRequest request, CancellationToken ct)
	{
		Requests.Add(request);
		var variants = request.Targets.Select(t =>
			new DraftedVariant(
				t.Post,
				new Variant(t.Network, Script.TryGetValue((t.Post, t.Network), out var queue) && queue.Count > 0 ? queue.Dequeue() : $"{request.Title} on {t.Network}.")
				{
					Article = t.Network != Network.Blog ? null : Articles.Count > 0 ? Articles.Dequeue() : new Article(request.Title, $"What is new in {request.Title}.", ["release"])
				}
			)
		).ToList();
		return Task.FromResult(new DraftResult(variants, new Usage("claude-opus-5-5", 1000, 200, 800, 0)));
	}

	public CampaignPlan? Plan { get; set; }

	public List<RepositoryInfo> Planned { get; } = [];

	/// <summary>The profile to answer with; a refusal when not set.</summary>
	public ProfileSuggestion? Profile { get; set; }

	public List<ProfileRequest> ProfilesAsked { get; } = [];

	public Task<(ProfileSuggestion Suggestion, Usage Usage)> ProfileAsync(ProfileRequest request, CancellationToken ct)
	{
		ProfilesAsked.Add(request);
		return Profile is { } profile ? Task.FromResult((profile, new Usage("claude-opus-5-5", 1500, 200, 0, 0))) : throw new DraftException("No profile scripted.");
	}

	/// <summary>The brief to answer with; a refusal when not set.</summary>
	public ProjectBrief? Brief { get; set; }

	public Task<(ProjectBrief Brief, Usage Usage)> BriefAsync(RepositoryInfo repository, CancellationToken ct) =>
		Brief is { } brief ? Task.FromResult((brief, new Usage("claude-opus-5-5", 2000, 300, 0, 0))) : throw new DraftException("No brief scripted.");

	public Task<(CampaignPlan Plan, Usage Usage)> PlanAsync(RepositoryInfo repository, CancellationToken ct)
	{
		Planned.Add(repository);
		return Plan is { } plan ? Task.FromResult((plan, new Usage("claude-opus-5-5", 3000, 400, 0, 0))) : throw new DraftException("No plan scripted.");
	}

	/// <summary>The points to answer with; the title as the one point when not set.</summary>
	public IReadOnlyList<TalkingPoint>? Points { get; set; }

	public List<PointsRequest> PointsAsked { get; } = [];

	public Task<(IReadOnlyList<TalkingPoint> Points, Usage Usage)> PointsAsync(PointsRequest request, CancellationToken ct)
	{
		PointsAsked.Add(request);
		return Task.FromResult((Points ?? [new TalkingPoint(request.Title)], new Usage("claude-opus-5-5", 800, 120, 0, 0)));
	}

	public Queue<ReleaseVerdict> Verdicts { get; } = new();

	public List<Release> Judged { get; } = [];

	public Task<(ReleaseVerdict Verdict, Usage Usage)> JudgeAsync(string project, Release release, CancellationToken ct)
	{
		Judged.Add(release);
		return Task.FromResult((Verdicts.Count > 0 ? Verdicts.Dequeue() : new ReleaseVerdict(true, "A feature.", null), new Usage("claude-opus-5-5", 500, 40, 0, 0)));
	}

	public List<MediaFile> Described { get; } = [];

	public Task<(string Alt, Usage Usage)> DescribeAsync(MediaFile image, string context, CancellationToken ct)
	{
		Described.Add(image);
		return Task.FromResult(("A terminal showing a diff that changed one line.", new Usage("claude-opus-5-5", 1500, 30, 0, 0)));
	}
}

/// <summary>The owner's time zone, Amsterdam unless a test says otherwise.</summary>
public sealed class OwnerIn(string zone = "Europe/Amsterdam") : IOwnerTime
{
	public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(zone);
}

public sealed class FakeDrafters(IDrafter? drafter) : IDrafters
{
	public IDrafter? Current { get; } = drafter;
}

public sealed class FakeUsage : IUsageLog
{
	public List<(Usage Usage, string Purpose)> Calls { get; } = [];

	public void Record(Usage usage, string purpose, DateTimeOffset at) => Calls.Add((usage, purpose));
}

public static class DraftingWorld
{
	public static DraftingService Service(IDrafter? drafter, ICampaignRepository repository, IUsageLog? usage = null) =>
		new(new FakeDrafters(drafter), repository, usage ?? new FakeUsage(), new ManualTimeProvider(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero)), NullLogger<DraftingService>.Instance);
}

/// <summary>A judge that says what it is told to: by default every finding asked for passes.</summary>
public sealed class FakeJudge : IJudge
{
	public string Model => "fake-judge";

	public List<JudgeRequest> Requests { get; } = [];

	/// <summary>What the judge says, given the findings it was asked for. Throws <see cref="DraftException"/> to fail.</summary>
	public Func<JudgeRequest, IReadOnlyList<Finding>> Says { get; set; } = r =>
		[.. Criteria.Expected(r.Campaign, r.Criteria).Select(e => new Finding(e.Criterion.Name, e.Post, e.Network, Passed: true, Blocks: false, "Fine."))];

	public Task<(IReadOnlyList<Finding> Findings, Usage Usage)> JudgeAsync(JudgeRequest request, CancellationToken ct)
	{
		Requests.Add(request);
		return Task.FromResult((Says(request), new Usage(Model, 100, 10, 0, 0)));
	}
}

public sealed class FakeJudges(IJudge? judge) : IJudges
{
	public IJudge? Current => judge;
}
