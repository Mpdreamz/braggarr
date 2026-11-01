using Braggarr.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

/// <summary>
/// The scheduler with everything around it faked, and a way to kill it: a crash throws the service instance away and starts a new
/// one over the same repo, as a container restart would. The journal outlives the restart, so the rules see the whole history.
/// </summary>
public sealed class SchedulerWorld
{
	public static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1));

	private readonly TimeProvider _time;

	public SchedulerWorld(TimeProvider time)
	{
		_time = time;
		Repository.Campaigns.AddRange(Campaign("curb-1-0", hoursFromNow: 0), Campaign("heed-0-20", hoursFromNow: 2));
		Service = Create();
		Service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
	}

	public TimeProvider Time => _time;

	/// <summary>An approved campaign changed on main, approved again as it now is: what the owner does after such an edit.</summary>
	public static Campaign Approved(Campaign changed) => changed with { Approval = changed.Approval! with { Digest = changed.Digest } };

	public FakeRepository Repository { get; } = new();

	public FakeVideoStore Videos { get; } = new();

	public FakePublisher Bluesky { get; } = new(Network.Bluesky);

	public FakePublisher Mastodon { get; } = new(Network.Mastodon);

	public FakePublisher Blog { get; } = new(Network.Blog);

	/// <summary>A second Bluesky account, for campaigns that post as it.</summary>
	public FakePublisher BlueskyNullean { get; } = new(Network.Bluesky, "nullean");

	public FakeAccounts Accounts { get; } = new();

	public FakeManualPosts Manual { get; } = new();

	public FakeNotifier Notifier { get; } = new();

	public FakeJournal Journal { get; } = new();

	public FakeSites Sites { get; } = new();

	public FakeVisibility Visibility { get; } = new();

	public SchedulingService Service { get; private set; }

	public int Restarts { get; private set; }

	public IEnumerable<Timestamped<DeliveryEvent>> Log => Journal.Events;

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

	public async Task RestartAsync()
	{
		Service.Dispose();
		Service = Create();
		Restarts++;
		await Service.StartAsync(CancellationToken.None);
	}

	public SchedulingService Create() =>
		new(
			Repository,
			new FakePublishers(Bluesky, Mastodon, BlueskyNullean, Blog),
			Accounts,
			Manual,
			Notifier,
			Journal,
			Videos,
			Sites,
			Visibility,
			_time,
			Options.Create(new SchedulingOptions { SyncInterval = TimeSpan.FromMinutes(5), PublishTimeout = TimeSpan.FromSeconds(60) }),
			NullLogger<SchedulingService>.Instance
		);

	public Campaign Campaign(string slug, int hoursFromNow) =>
		new Campaign(
			slug, slug, "curb", CampaignStatus.Proposed, [Network.Bluesky, Network.Mastodon, Network.Reddit], [], "", "agent:release-bot",
			[
				new Post(
					"01-launch",
					_time.GetUtcNow().AddHours(hoursFromNow),
					[
						new Variant(Network.Bluesky, $"{slug} is out."),
						new Variant(Network.Mastodon, $"{slug} is out."),
						new Variant(Network.Reddit, $"{slug} is out\nhttps://github.com/nullean/curb")
					]
				),
				new Post("02-follow-up", _time.GetUtcNow().AddHours(hoursFromNow + 24), [
					new Variant(Network.Bluesky, "A day in."),
					new Variant(Network.Mastodon, "A day in."),
					new Variant(Network.Reddit, "A day in")
				])
			]
		).Approve(new(Actor.Owner("mpdreamz"), _time.GetUtcNow()));
}
