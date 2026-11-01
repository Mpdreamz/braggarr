using AwesomeAssertions;
using Braggarr.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

/// <summary>Only the owner keeps a brief; Claude suggests one; drafting, talking points and the judge all read it.</summary>
public class BriefServiceTests
{
	private static readonly Actor Owner = Actor.Owner("mpdreamz");

	private static readonly ProjectBrief Mermaider = new("Mermaider", "Mermaid diagrams to SVG without a browser.", "No headless Chrome: one small binary.", "Docs authors")
	{
		Avoid = ["Calling it a Mermaid fork"],
		Links = [new BriefLink("Repository", "https://github.com/nullean/mermaider")]
	};

	private sealed class Repositories : IRepositories
	{
		public List<string> Read { get; } = [];

		public Task<RepositoryInfo> ReadAsync(string repository, string name, CancellationToken ct)
		{
			Read.Add(repository);
			return Task.FromResult(new RepositoryInfo(repository, name, "Diagrams to SVG.", null, [], "# Mermaider", []));
		}
	}

	private static (BriefService Service, FakeRepository Repository, FakeDrafter Drafter, FakeUsage Usage, Repositories Repositories) Create(bool claude = true)
	{
		var repository = new FakeRepository();
		var drafter = new FakeDrafter();
		var usage = new FakeUsage();
		var repositories = new Repositories();
		var service = new BriefService(repository, repositories, new FakeDrafters(claude ? drafter : null), usage,
			new ManualTimeProvider(SchedulerWorld.Start), NullLogger<BriefService>.Instance);
		return (service, repository, drafter, usage, repositories);
	}

	[Test]
	public async Task The_owner_keeps_a_brief_trimmed_and_an_agent_never_does()
	{
		var (service, repository, _, _, _) = Create();

		var refused = await service.SaveAsync(Mermaider, Actor.Agent("release-bot"), CancellationToken.None);
		var kept = await service.SaveAsync(Mermaider with { Tldr = "  " + Mermaider.Tldr + " ", Avoid = [.. Mermaider.Avoid, "  "] }, Owner, CancellationToken.None);

		refused.Should().BeOfType<Outcome<ProjectBrief>.Refused>().Which.Reasons.Single().Should().Contain("Only the owner");
		kept.Should().BeOfType<Outcome<ProjectBrief>.Done>();
		repository.Briefs.Should().ContainSingle().Which.Should().BeEquivalentTo(Mermaider);
		(await service.ForAsync("mermaider", CancellationToken.None)).Should().BeEquivalentTo(Mermaider);
	}

	[Test]
	public async Task A_brief_that_breaks_a_rule_is_refused_with_why()
	{
		var (service, repository, _, _, _) = Create();

		var outcome = await service.SaveAsync(Mermaider with { Apart = "", Links = [new BriefLink("Docs", "ftp://x")] }, Owner, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<ProjectBrief>.Refused>().Which.Reasons.Should().HaveCount(2);
		repository.Briefs.Should().BeEmpty();
	}

	[Test]
	public async Task Claude_suggests_from_the_repository_names_it_after_the_project_and_keeps_nothing()
	{
		var (service, repository, drafter, usage, repositories) = Create();
		drafter.Brief = Mermaider with { Project = "mermaider-repo-name" };

		var outcome = await service.SuggestAsync("nullean/mermaider", "Mermaider", CancellationToken.None);

		outcome.Should().BeOfType<Outcome<ProjectBrief>.Done>().Which.Value.Project.Should().Be("Mermaider");
		repositories.Read.Should().Equal("nullean/mermaider");
		usage.Calls.Should().ContainSingle().Which.Purpose.Should().Be("brief nullean/mermaider");
		repository.Briefs.Should().BeEmpty("a suggestion is the owner's to keep");
	}

	[Test]
	public async Task Without_claude_or_when_claude_fails_there_is_no_suggestion_and_it_says_why()
	{
		var (off, _, _, _, _) = Create(claude: false);
		var (failing, _, _, _, _) = Create();

		(await off.SuggestAsync("nullean/mermaider", "Mermaider", CancellationToken.None)).Should().BeOfType<Outcome<ProjectBrief>.Refused>()
			.Which.Reasons.Single().Should().Contain("Settings → Claude");
		(await failing.SuggestAsync("nullean/mermaider", "Mermaider", CancellationToken.None)).Should().BeOfType<Outcome<ProjectBrief>.Refused>()
			.Which.Reasons.Single().Should().Contain("No brief scripted");
	}

	[Test]
	public async Task Drafting_and_talking_points_get_the_brief_of_the_campaigns_project_and_no_other()
	{
		var drafter = new FakeDrafter();
		var repository = new FakeRepository();
		repository.Briefs.AddRange([Mermaider, new ProjectBrief("Curb", "A formatter.", "Small diffs.", "")]);
		var drafting = DraftingWorld.Service(drafter, repository);
		var draft = new CampaignDraft(null, "Mermaider 2.0", "mermaider", [Network.Bluesky], [], "Announce 2.0.",
			[new PostDraft("01-post", SchedulerWorld.Start.AddDays(1), new Dictionary<Network, string>())]);

		_ = await drafting.FillAsync(draft, null, CancellationToken.None);
		_ = await drafting.PointsAsync(new PointsRequest("Mermaider 2.0", "mermaider", "Announce 2.0.", []), CancellationToken.None);
		_ = await drafting.PointsAsync(new PointsRequest("Heed 1.0", "heed", "Announce heed.", []), CancellationToken.None);

		drafter.Requests.Should().NotBeEmpty().And.OnlyContain(r => r.About == Mermaider);
		drafter.PointsAsked.Select(p => p.About).Should().Equal(Mermaider, null);
	}

	[Test]
	public async Task The_judge_gets_the_brief_of_the_campaigns_project()
	{
		var time = new ManualTimeProvider(SchedulerWorld.Start);
		var world = new SchedulerWorld(time);
		world.Repository.Briefs.Add(Mermaider);
		world.Repository.Criteria.Add(new Criterion("honest", "Claims nothing the links do not show.", Blocks: true));
		var reviews = new FakeReviews();
		var judge = new FakeJudge();
		var judges = new FakeJudges(judge);
		var drafting = DraftingWorld.Service(null, world.Repository);
		var campaigns = new CampaignService(reviews, world.Repository, world.Service, drafting, world.Notifier, new OwnerIn(), time, world.Videos, judges, new FakeAccounts());
		var judging = new JudgingService(judges, world.Repository, reviews, world.Service, campaigns, drafting, world.Notifier, new FakeUsage(),
			new JudgingTests.Monitor(), new OwnerIn(), time, NullLogger<JudgingService>.Instance);
		_ = await campaigns.ProposeAsync(new CampaignDraft(null, "Mermaider 2.0", "mermaider", [Network.Bluesky], [], "Announce 2.0.",
			[new PostDraft("01-post", SchedulerWorld.Start.AddDays(1), new Dictionary<Network, string> { [Network.Bluesky] = "Mermaider 2.0." })]), Owner, CancellationToken.None);

		await judging.CheckAsync(CancellationToken.None);

		judge.Requests.Single().About.Should().Be(Mermaider);
	}
}
