using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;
using Braggarr.Web.Hosting;
using Braggarr.Web.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nullean.Entities;

namespace Braggarr.Web.Tests;

/// <summary>The loop every background host runs, on a manual clock: when it runs, when it does not, and what a failure leaves.</summary>
public class HostTests
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

	private sealed class Counting(SettingsStore settings, TimeProvider time) : PeriodicHost(TimeSpan.FromHours(1), settings, time, NullLogger.Instance)
	{
		public SemaphoreSlim Ran { get; } = new(0);

		public int Runs { get; private set; }

		public Queue<Exception> Throw { get; } = new();

		protected override string Failure => "The test's work failed.";

		protected override Task RunAsync(CancellationToken ct)
		{
			Runs++;
			_ = Ran.Release();
			return Throw.TryDequeue(out var e) ? Task.FromException(e) : Task.CompletedTask;
		}
	}

	private static SettingsStore Settings(bool setUp)
	{
		var settings = new SettingsStore(new MockFileSystem(), "/config");
		if (setUp)
		{
			_ = settings.Update(s => s with
			{
				PostsRepository = "owner/braggarr-posts",
				GitHubApp = new GitHubAppSettings { AppId = 1 },
				Owner = new OwnerSettings(42, "mpdreamz")
			});
		}
		return settings;
	}

	/// <summary>A run the test expects; fails if it does not come.</summary>
	private static async Task RunsAsync(Counting host) => (await host.Ran.WaitAsync(Patience)).Should().BeTrue("the host should have run");

	/// <summary>No run: the loop had every chance (it is waiting on the timer), and did not.</summary>
	private static async Task NoRunAsync(Counting host) => (await host.Ran.WaitAsync(TimeSpan.FromMilliseconds(200))).Should().BeFalse("the host ran early");

	/// <summary>The problem the host keeps once the failed run is over; the work signals before the host has recorded it.</summary>
	private static async Task<string?> KeptAsync(Counting host)
	{
		using var waited = new CancellationTokenSource(Patience);
		while (host.LastProblem is null && !waited.IsCancellationRequested)
		{
			await Task.Delay(10, CancellationToken.None);
		}
		return host.LastProblem;
	}

	[Test]
	public async Task It_runs_at_start_and_then_once_an_interval_by_the_apps_clock()
	{
		var time = new ManualTimeProvider(new DateTimeOffset(2030, 1, 7, 9, 0, 0, TimeSpan.Zero));
		using var host = new Counting(Settings(setUp: true), time);

		await host.StartAsync(CancellationToken.None);
		await RunsAsync(host);
		time.Advance(TimeSpan.FromMinutes(59));
		await NoRunAsync(host);
		time.Advance(TimeSpan.FromMinutes(1));
		await RunsAsync(host);
		time.Advance(TimeSpan.FromHours(1));
		await RunsAsync(host);
		await host.StopAsync(CancellationToken.None);

		host.Runs.Should().Be(3);
	}

	[Test]
	public async Task A_failure_is_kept_and_the_next_tick_tries_again()
	{
		var time = new ManualTimeProvider(new DateTimeOffset(2030, 1, 7, 9, 0, 0, TimeSpan.Zero));
		using var host = new Counting(Settings(setUp: true), time);
		host.Throw.Enqueue(new InvalidOperationException("GitHub is down."));

		await host.StartAsync(CancellationToken.None);
		await RunsAsync(host);
		var problem = await KeptAsync(host);
		time.Advance(TimeSpan.FromHours(1));
		await RunsAsync(host);
		await host.StopAsync(CancellationToken.None);

		problem.Should().Be("GitHub is down.");
		host.LastProblem.Should().BeNull("the next run went through");
	}

	[Test]
	public async Task Nothing_runs_until_braggarr_is_set_up()
	{
		var time = new ManualTimeProvider(new DateTimeOffset(2030, 1, 7, 9, 0, 0, TimeSpan.Zero));
		var settings = Settings(setUp: false);
		using var host = new Counting(settings, time);

		await host.StartAsync(CancellationToken.None);
		await NoRunAsync(host);
		time.Advance(TimeSpan.FromHours(1));
		await NoRunAsync(host);
		_ = settings.Update(s => s with { PostsRepository = "owner/braggarr-posts", GitHubApp = new GitHubAppSettings { AppId = 1 }, Owner = new OwnerSettings(42, "mpdreamz") });
		time.Advance(TimeSpan.FromHours(1));
		await RunsAsync(host);
		await host.StopAsync(CancellationToken.None);

		host.Runs.Should().Be(1);
	}

	[Test]
	public void Each_host_keeps_its_interval()
	{
		using var factory = new BraggarrFactory();
		var services = factory.Services;

		ActivatorUtilities.CreateInstance<SchedulerHost>(services).Interval.Should().Be(TimeSpan.FromSeconds(30));
		ActivatorUtilities.CreateInstance<ReleaseHost>(services).Interval.Should().Be(TimeSpan.FromHours(1));
		ActivatorUtilities.CreateInstance<RepeatHost>(services).Interval.Should().Be(TimeSpan.FromHours(6));
		ActivatorUtilities.CreateInstance<TokenHost>(services).Interval.Should().Be(TimeSpan.FromHours(6));
	}
}
