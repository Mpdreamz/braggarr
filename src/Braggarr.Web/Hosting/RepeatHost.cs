using Braggarr.Services;
using Braggarr.Web.Settings;

namespace Braggarr.Web.Hosting;

/// <summary>Looks for campaigns due to be proposed again, every six hours.</summary>
public sealed class RepeatHost(Repeater repeater, SettingsStore settings, TimeProvider time, ILogger<RepeatHost> logger)
	: PeriodicHost(TimeSpan.FromHours(6), settings, time, logger)
{
	protected override string Failure => "Checking for repeats failed; trying again in six hours.";

	protected override Task RunAsync(CancellationToken ct) => repeater.CheckAsync(ct);
}
