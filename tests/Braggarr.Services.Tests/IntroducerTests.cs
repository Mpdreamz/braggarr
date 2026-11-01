using AwesomeAssertions;
using Braggarr.Domain;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

public class IntroducerTests
{
	private sealed class Repositories : IRepositories
	{
		public Task<RepositoryInfo> ReadAsync(string repository, string name, CancellationToken ct) =>
			Task.FromResult(new RepositoryInfo(repository, name, "A C# formatter that keeps diffs small.", "https://curb.example", ["csharp"], "# Curb\nFormats C#.", []));
	}

	private sealed class Amsterdam : IOwnerTime
	{
		public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
	}

	private static (Introducer Introducer, FakeReviews Reviews, FakeDrafter Drafter) Create(bool withModel = true)
	{
		var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 28, 15, 0, 0, TimeSpan.Zero));
		var world = new SchedulerWorld(time);
		var reviews = new FakeReviews();
		var drafter = new FakeDrafter();
		var drafting = DraftingWorld.Service(withModel ? drafter : null, world.Repository);
		var campaigns = new CampaignService(reviews, world.Repository, world.Service, drafting, world.Notifier, new OwnerIn(), world.Time, world.Videos, new FakeJudges(null), new FakeAccounts());
		return (new Introducer(new Repositories(), drafting, campaigns, new Amsterdam(), time), reviews, drafter);
	}

	[Test]
	public async Task A_planned_introduction_is_laid_out_from_its_ranked_points_and_drafted()
	{
		var (introducer, reviews, drafter) = Create();
		drafter.Plan = new CampaignPlan("Introducing Curb", "Curb formats C# and keeps diffs small.",
		[
			new TalkingPoint("What it is", [new WantedMedia(MediaKind.Image, "A terminal where one line changes")]),
			new TalkingPoint("The CLI and the GitHub Action"),
			new TalkingPoint("  ")
		]);

		var outcome = await introducer.ProposeAsync("nullean/curb", "Curb", [Network.Bluesky, Network.Mastodon, Network.LinkedIn], CancellationToken.None);

		var campaign = outcome.Should().BeOfType<Outcome<Proposal>.Done>().Which.Value.Campaign;
		reviews.Open.Should().ContainSingle();
		campaign.Slug.Should().Be("curb-introduction");
		campaign.CreatedBy.Should().Be("agent:introducer");
		campaign.Links.Should().Equal("https://curb.example", "https://github.com/nullean/curb");
		campaign.Points.Select(p => p.Text).Should().Equal("What it is", "The CLI and the GitHub Action");
		campaign.Posts.Select(p => (p.Id, p.Networks!.Count, p.Points.Count)).Should().Equal(
			("00-overview", 1, 2),
			("01-what-it-is", 2, 1),
			("02-the-cli-and-the-github-action", 2, 1));
		campaign.Posts[1].At.Should().Be(new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.FromHours(1)));
		campaign.Posts[2].At.Should().Be(new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.FromHours(1)));
		campaign.Posts[1].For(Network.Bluesky)!.Media.Should().Equal(new MediaRef("wanted:image", "A terminal where one line changes"));
		campaign.Posts[2].For(Network.Bluesky)!.Media.Should().BeEmpty();
		campaign.IsComplete.Should().BeTrue();
		campaign.IsCovered.Should().BeTrue();
		drafter.Requests.Single().Points.Should().HaveCount(2);
	}

	[Test]
	public async Task Without_a_model_it_is_one_launch_post_from_the_description()
	{
		var (introducer, reviews, _) = Create(withModel: false);

		_ = await introducer.ProposeAsync("nullean/curb", "Curb", [Network.Bluesky], CancellationToken.None);

		var campaign = reviews.Open.Single().Campaign;
		campaign.Title.Should().Be("Introducing Curb");
		campaign.Brief.Should().StartWith("A C# formatter that keeps diffs small.");
		campaign.Posts.Should().ContainSingle();
	}
}
