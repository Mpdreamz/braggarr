using AwesomeAssertions;
using Braggarr.Domain;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

/// <summary>Only a public project gets a blog post: asked of GitHub when a campaign is proposed.</summary>
public class BlogProposalTests
{
	private static readonly Actor Agent = Actor.Agent("release-bot");

	private static CampaignService Service(SchedulerWorld world, IProjectVisibility? visibility) =>
		new(new FakeReviews(), world.Repository, world.Service, DraftingWorld.Service(null, world.Repository), world.Notifier,
			new OwnerIn(), world.Time, world.Videos, new FakeJudges(null), world.Accounts, visibility);

	private static CampaignDraft Draft() =>
		new(null, "Curb 2.0", "curb", [Network.Bluesky, Network.Blog], ["https://github.com/nullean/curb"], "Announce 2.0.",
		[
			new PostDraft("00-blog", SchedulerWorld.Start.AddDays(1), new Dictionary<Network, string> { [Network.Blog] = "# Curb 2.0\n\nSmaller diffs." })
			{
				Networks = [Network.Blog],
				Article = new Article("Curb 2.0", "Smaller diffs.", ["dotnet"])
			},
			new PostDraft("01-launch", SchedulerWorld.Start.AddDays(1) + Blogging.DeployMargin, new Dictionary<Network, string> { [Network.Bluesky] = $"Curb 2.0: {Blogging.Placeholder}" })
			{
				Networks = [Network.Bluesky]
			}
		]);

	[Test]
	[Arguments(ProjectVisibility.Private)]
	[Arguments(ProjectVisibility.Unknown)]
	public async Task A_project_github_does_not_call_public_gets_no_blog_post(ProjectVisibility visibility)
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));

		var outcome = await Service(world, new FakeVisibility { Visibility = visibility }).ProposeAsync(Draft(), Agent, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Proposal>.Refused>().Which.Reasons.Should().Contain(r => r.StartsWith("Only a public project gets a blog post", StringComparison.Ordinal));
	}

	[Test]
	public async Task Without_a_way_to_ask_github_no_project_gets_a_blog_post()
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));

		var outcome = await Service(world, null).ProposeAsync(Draft(), Agent, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<Proposal>.Refused>();
	}

	[Test]
	public async Task A_public_projects_blog_post_is_proposed_with_its_article()
	{
		var world = new SchedulerWorld(new ManualTimeProvider(SchedulerWorld.Start));

		var outcome = await Service(world, new FakeVisibility()).ProposeAsync(Draft(), Agent, CancellationToken.None);

		var proposal = outcome.Should().BeOfType<Outcome<Proposal>.Done>().Which.Value;
		proposal.Campaign.Posts.Single(p => p.Id == "00-blog").For(Network.Blog)!.Article!.Title.Should().Be("Curb 2.0");
	}
}
