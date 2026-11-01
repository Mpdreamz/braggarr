using Braggarr.Services;
using Braggarr.Web.Settings;

namespace Braggarr.Web.Hosting;

/// <summary>Has the judge look at what changed every minute once Braggarr is set up. A failed check is logged and the next one tries again.</summary>
public sealed class JudgeHost(JudgingService judging, SettingsStore settings, TimeProvider time, ILogger<JudgeHost> logger)
	: PeriodicHost(TimeSpan.FromMinutes(1), settings, time, logger)
{
	protected override string Failure => "The judge's check failed; the next one tries again.";

	protected override Task RunAsync(CancellationToken ct) => judging.CheckAsync(ct);
}
