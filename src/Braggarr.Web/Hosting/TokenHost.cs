using Braggarr.Adapters;
using Braggarr.Web.Settings;

namespace Braggarr.Web.Hosting;

/// <summary>Runs the token keeper every six hours: Threads tokens refreshed, LinkedIn's expiry announced in time.</summary>
public sealed class TokenHost(TokenKeeper keeper, SettingsStore settings, TimeProvider time, ILogger<TokenHost> logger)
	: PeriodicHost(TimeSpan.FromHours(6), settings, time, logger)
{
	protected override string Failure => "Checking the network tokens failed; trying again in six hours.";

	protected override Task RunAsync(CancellationToken ct) => keeper.CheckAsync(ct);
}
