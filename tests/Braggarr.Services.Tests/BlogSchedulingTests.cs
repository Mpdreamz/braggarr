using AwesomeAssertions;
using Braggarr.Domain;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

/// <summary>A blog post's life in the scheduler: committed, live once its site answers, and only then linked.</summary>
public class BlogSchedulingTests
{
	private static readonly DeliveryKey BlogKey = new("hunch-0-3", "00-blog", Network.Blog);
	private static readonly DeliveryKey LinkKey = new("hunch-0-3", "01-launch", Network.Bluesky);

	/// <summary>A blog post an hour ago, before anything else is due, and a Bluesky post half an hour after it that links it.</summary>
	private static Campaign Blogged(TimeProvider time) =>
		new Campaign(
			"hunch-0-3", "Hunch 0.3", "hunch", CampaignStatus.Proposed, [Network.Bluesky, Network.Blog], [], "", "agent:release-bot",
			[
				new Post("00-blog", time.GetUtcNow().AddHours(-1), [new Variant(Network.Blog, "# Hunch 0.3\n\nA faster index.") { Article = new Article("Hunch 0.3", "A faster index.", ["dotnet"]) }])
				{
					Networks = [Network.Blog]
				},
				new Post("01-launch", time.GetUtcNow().AddHours(-1) + Blogging.DeployMargin, [new Variant(Network.Bluesky, $"Hunch 0.3 is out: {Blogging.Placeholder}")])
				{
					Networks = [Network.Bluesky]
				}
			]
		).Approve(new(Actor.Owner("mpdreamz"), time.GetUtcNow()));

	private static SchedulerWorld World(ManualTimeProvider time)
	{
		var world = new SchedulerWorld(time);
		world.Repository.Campaigns.Add(Blogged(time));
		world.Service.RequestSync();
		return world;
	}

	private static Delivery Find(SchedulerWorld world, DeliveryKey key) => world.Repository.Deliveries[key];

	[Test]
	public async Task A_blog_post_is_committed_then_live_and_only_then_the_post_that_links_it_goes_out_with_its_address()
	{
		var time = new ManualTimeProvider(SchedulerWorld.Start);
		var world = World(time);

		await world.TickAsync();

		world.Blog.Posted.Should().ContainSingle();
		Find(world, BlogKey).Should().Match<Delivery>(d => d.State == DeliveryState.Committed && d.Receipt!.Url == "https://blog.example/1");
		world.Bluesky.Posted.Should().NotContain(v => v.Text.StartsWith("Hunch", StringComparison.Ordinal), "the blog post is not live yet");

		time.Advance(TimeSpan.FromMinutes(5));
		await world.TickAsync();
		world.Bluesky.Posted.Should().NotContain(v => v.Text.StartsWith("Hunch", StringComparison.Ordinal), "its site has not answered yet");

		world.Sites.Answering.Add("https://blog.example/1");
		time.Advance(TimeSpan.FromMinutes(5));
		await world.TickAsync();

		Find(world, BlogKey).State.Should().Be(DeliveryState.Published);
		world.Bluesky.Posted.Should().ContainSingle(v => v.Text.StartsWith("Hunch", StringComparison.Ordinal))
			.Which.Text.Should().Be("Hunch 0.3 is out: https://blog.example/1");
		world.Blog.Posted.Should().ContainSingle("live is not a second commit");
		world.Journal.Events.Where(e => e.Event.Key == BlogKey).Select(e => e.Event.GetType().Name).Should().Equal("Started", "Committed", "WentLive");
	}

	[Test]
	public async Task A_blog_post_that_never_answers_is_the_owners_to_look_at_and_what_links_it_waits()
	{
		var time = new ManualTimeProvider(SchedulerWorld.Start);
		var world = World(time);
		await world.TickAsync();

		time.Advance(Blogging.LiveWithin);
		await world.TickAsync();
		time.Advance(TimeSpan.FromDays(1));
		await world.TickAsync();

		Find(world, BlogKey).Should().Match<Delivery>(d => d.State == DeliveryState.Unknown && d.Receipt == null);
		world.Notifier.Sent.Should().ContainSingle(n => n.Title == "Hunch 0.3: the blog post did not appear");
		world.Service.Current.Find(LinkKey)!.State.Should().Be(DeliveryState.Pending);
		world.Blog.Posted.Should().ContainSingle("an unknown post is never committed again by itself");
	}

	[Test]
	public async Task A_restart_while_committing_puts_the_blog_post_back_to_wait_without_asking()
	{
		var time = new ManualTimeProvider(SchedulerWorld.Start);
		var world = World(time);
		world.Repository.CrashAfterNextSending = true;

		await world.TickAsync();

		world.Restarts.Should().Be(1);
		Find(world, BlogKey).State.Should().Be(DeliveryState.Pending);
		world.Notifier.Sent.Should().NotContain(n => n.Title == "Did this post go out?");
		world.Journal.Events.Select(e => e.Event).Should().Contain(new DeliveryEvent.Requeued(BlogKey));

		await world.TickAsync();
		Find(world, BlogKey).State.Should().Be(DeliveryState.Committed);
	}

	[Test]
	public async Task A_private_projects_blog_post_never_goes_up()
	{
		var time = new ManualTimeProvider(SchedulerWorld.Start);
		var world = World(time);
		world.Visibility.Visibility = ProjectVisibility.Private;

		await world.TickAsync();

		world.Blog.Posted.Should().BeEmpty();
		Find(world, BlogKey).Should().Match<Delivery>(d => d.State == DeliveryState.Failed && d.Error!.StartsWith("Only a public project", StringComparison.Ordinal));
		world.Notifier.Sent.Should().Contain(n => n.Title == "Hunch 0.3: the blog post did not go out");
	}
}
