using Braggarr.Services;
using Braggarr.Web.Settings;

namespace Braggarr.Web.Hosting;

/// <summary>Ticks the scheduler every 30 seconds once Braggarr is set up. A failed tick is logged and the next one tries again.</summary>
public sealed class SchedulerHost(SchedulingService scheduler, SettingsStore settings, TimeProvider time, ILogger<SchedulerHost> logger)
	: PeriodicHost(TimeSpan.FromSeconds(30), settings, time, logger)
{
	public bool Started { get; private set; }

	protected override string Failure => "The scheduler's tick failed; the next one tries again.";

	protected override async Task RunAsync(CancellationToken ct)
	{
		if (!Started)
		{
			await scheduler.StartAsync(ct);
			Started = true;
		}
		await scheduler.TickAsync(ct);
	}
}
