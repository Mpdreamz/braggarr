using Braggarr.Services;
using Braggarr.Web.Settings;

namespace Braggarr.Web.Hosting;

/// <summary>Checks the watched projects for new releases every hour.</summary>
public sealed class ReleaseHost(ReleaseWatcher watcher, SettingsStore settings, TimeProvider time, ILogger<ReleaseHost> logger)
	: PeriodicHost(TimeSpan.FromHours(1), settings, time, logger)
{
	protected override bool Ready(BraggarrSettings current) => current.IsSetUp && current.Projects.Count > 0;

	protected override string Failure => "Checking for releases failed; trying again in an hour.";

	protected override Task RunAsync(CancellationToken ct) => watcher.CheckAsync(ct);
}
