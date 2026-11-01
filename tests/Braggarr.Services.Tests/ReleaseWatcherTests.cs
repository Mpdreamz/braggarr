using AwesomeAssertions;
using Braggarr.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

public class ReleaseWatcherTests
{
	private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

	private sealed class Releases : IReleases
	{
		public Dictionary<string, List<Release>> ByRepository { get; } = [];

		public Task<IReadOnlyList<Release>> LatestAsync(string repository, CancellationToken ct) =>
			ByRepository.TryGetValue(repository, out var releases)
				? Task.FromResult<IReadOnlyList<Release>>([.. releases.OrderByDescending(r => r.PublishedAt)])
				: throw new HttpRequestException("404");
	}

	private sealed class Projects(params WatchedProject[] projects) : IWatchedProjects
	{
		public List<WatchedProject> List { get; } = [.. projects];

		public IReadOnlyList<WatchedProject> All => List;

		public TimeZoneInfo Zone => Amsterdam;

		public List<(string Repository, string Tag, string Reason)> Skipped { get; } = [];

		public void RecordSkipped(string repository, string tag, string reason) => Skipped.Add((repository, tag, reason));

		public void MarkSeen(string repository, string tag)
		{
			var i = List.FindIndex(p => p.Repository == repository);
			List[i] = List[i] with { LastSeenTag = tag };
		}
	}

	private static Release R(string tag, int day, bool prerelease = false) =>
		new(tag, tag, $"https://github.com/nullean/curb/releases/tag/{tag}", $"Notes for {tag}.", new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeSpan.Zero), prerelease);

	private static (ReleaseWatcher Watcher, Releases Releases, Projects Projects, FakeReviews Reviews) Create(params WatchedProject[] watched) =>
		Create(new FakeDrafter(), watched);

	private static (ReleaseWatcher Watcher, Releases Releases, Projects Projects, FakeReviews Reviews) Create(FakeDrafter drafter, params WatchedProject[] watched)
	{
		var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 28, 15, 0, 0, TimeSpan.Zero));
		var world = new SchedulerWorld(time);
		var reviews = new FakeReviews();
		var drafting = DraftingWorld.Service(drafter, world.Repository);
		var campaigns = new CampaignService(reviews, world.Repository, world.Service, drafting, world.Notifier, new OwnerIn(), world.Time, world.Videos, new FakeJudges(null), new FakeAccounts());
		var releases = new Releases();
		var projects = new Projects(watched);
		return (new ReleaseWatcher(releases, projects, campaigns, drafting, time, NullLogger<ReleaseWatcher>.Instance), releases, projects, reviews);
	}

	private static WatchedProject Curb(string? seen) => new("nullean/curb", "Curb", [Network.Bluesky, Network.Mastodon], seen);

	[Test]
	public async Task The_first_check_remembers_the_latest_release_and_proposes_nothing()
	{
		var (watcher, releases, projects, reviews) = Create(Curb(seen: null));
		releases.ByRepository["nullean/curb"] = [R("0.9.0", 1), R("1.0.0", 20)];

		(await watcher.CheckAsync(CancellationToken.None)).Should().BeEmpty();

		projects.List.Single().LastSeenTag.Should().Be("1.0.0");
		reviews.Open.Should().BeEmpty();
	}

	[Test]
	public async Task Hacker_news_and_reddit_see_major_versions_only()
	{
		var watched = new WatchedProject("nullean/curb", "Curb", [Network.Bluesky, Network.HackerNews], "1.0.0");
		var (watcher, releases, _, reviews) = Create(watched);
		releases.ByRepository["nullean/curb"] = [R("1.0.0", 20), R("1.1.0", 27)];

		_ = await watcher.CheckAsync(CancellationToken.None);
		releases.ByRepository["nullean/curb"] = [R("1.1.0", 27), R("2.0.0", 28)];
		_ = await watcher.CheckAsync(CancellationToken.None);

		reviews.Open.Select(p => string.Join(",", p.Campaign.Networks)).Should().Equal("Bluesky", "Bluesky,HackerNews");
	}

	[Test]
	public async Task A_new_release_becomes_a_drafted_proposal_for_ten_the_next_morning_and_is_proposed_once()
	{
		var (watcher, releases, projects, reviews) = Create(Curb(seen: "1.0.0"));
		releases.ByRepository["nullean/curb"] = [R("1.0.0", 20), R("1.1.0", 27)];

		var proposed = await watcher.CheckAsync(CancellationToken.None);
		projects.List.Single().LastSeenTag.Should().Be("1.1.0", "a proposed release is remembered at once");
		(await watcher.CheckAsync(CancellationToken.None)).Should().BeEmpty();

		var campaign = proposed.Should().ContainSingle().Which.Campaign;
		reviews.Open.Should().ContainSingle();
		campaign.Slug.Should().Be("curb-1-1-0");
		campaign.Title.Should().Be("Curb 1.1.0");
		campaign.CreatedBy.Should().Be("agent:release-watcher");
		campaign.Status.Should().Be(CampaignStatus.Proposed);
		campaign.Links.Should().Equal("https://github.com/nullean/curb/releases/tag/1.1.0", "https://github.com/nullean/curb");
		campaign.Brief.Should().Contain("Notes for 1.1.0.");
		campaign.IsComplete.Should().BeTrue("the variants were drafted");
		campaign.Posts.Single().At.Should().Be(new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.FromHours(1)));
		projects.List.Single().LastSeenTag.Should().Be("1.1.0");
	}

	[Test]
	public async Task Prereleases_are_skipped_unless_asked_for()
	{
		var (watcher, releases, _, reviews) = Create(Curb(seen: "1.0.0"));
		releases.ByRepository["nullean/curb"] = [R("1.0.0", 20), R("1.1.0-beta.1", 27, prerelease: true)];

		_ = await watcher.CheckAsync(CancellationToken.None);

		reviews.Open.Should().BeEmpty();
	}

	[Test]
	public async Task A_repository_that_cannot_be_read_does_not_stop_the_others()
	{
		var (watcher, releases, _, reviews) = Create(new WatchedProject("gone/away", "Gone", [Network.Bluesky], "1"), Curb(seen: "1.0.0"));
		releases.ByRepository["nullean/curb"] = [R("1.0.0", 20), R("1.1.0", 27)];

		_ = await watcher.CheckAsync(CancellationToken.None);

		reviews.Open.Should().ContainSingle();
	}

	[Test]
	public async Task A_release_claude_judges_not_worth_a_post_is_skipped_with_its_reason_and_can_be_proposed_anyway()
	{
		var drafter = new FakeDrafter();
		drafter.Verdicts.Enqueue(new ReleaseVerdict(false, "Only dependency bumps.", null));
		var (watcher, releases, projects, reviews) = Create(drafter, Curb(seen: "1.0.0"));
		releases.ByRepository["nullean/curb"] = [R("1.0.0", 20), R("1.0.1", 27)];

		(await watcher.CheckAsync(CancellationToken.None)).Should().BeEmpty();

		reviews.Open.Should().BeEmpty();
		projects.Skipped.Should().Equal(("nullean/curb", "1.0.1", "Only dependency bumps."));
		projects.List.Single().LastSeenTag.Should().Be("1.0.1");

		var anyway = await watcher.ProposeAnywayAsync("nullean/curb", "1.0.1", CancellationToken.None);
		anyway.Should().BeOfType<Outcome<Proposal>.Done>().Which.Value.Campaign.Slug.Should().Be("curb-1-0-1");
	}

	[Test]
	public async Task A_worthy_release_is_proposed_with_claudes_angle_in_the_brief()
	{
		var drafter = new FakeDrafter();
		drafter.Verdicts.Enqueue(new ReleaseVerdict(true, "A big speed-up.", "Formatting is now twice as fast."));
		var (watcher, releases, _, _) = Create(drafter, Curb(seen: "1.0.0"));
		releases.ByRepository["nullean/curb"] = [R("1.0.0", 20), R("1.1.0", 27)];

		var proposed = await watcher.CheckAsync(CancellationToken.None);

		proposed.Single().Campaign.Brief.Should().Contain("The angle to take: Formatting is now twice as fast.");
	}

	[Test]
	public async Task Without_asking_claude_every_release_is_proposed()
	{
		var drafter = new FakeDrafter();
		drafter.Verdicts.Enqueue(new ReleaseVerdict(false, "Would have been skipped.", null));
		var (watcher, releases, _, reviews) = Create(drafter, Curb(seen: "1.0.0") with { AskClaude = false });
		releases.ByRepository["nullean/curb"] = [R("1.0.0", 20), R("1.0.1", 27)];

		_ = await watcher.CheckAsync(CancellationToken.None);

		reviews.Open.Should().ContainSingle();
		drafter.Judged.Should().BeEmpty();
	}
}
