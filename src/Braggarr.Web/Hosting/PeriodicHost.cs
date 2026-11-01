using Braggarr.Web.Settings;

namespace Braggarr.Web.Hosting;

/// <summary>
/// Runs some work at start and then every <see cref="Interval"/> by the app's clock, once Braggarr is set up. A failure is
/// logged and kept as <see cref="LastProblem"/>; the next tick tries again. The one loop every background host shares, so its
/// timing is tested once, on a manual clock.
/// </summary>
public abstract class PeriodicHost(TimeSpan interval, SettingsStore settings, TimeProvider time, ILogger logger) : BackgroundService
{
	public TimeSpan Interval => interval;

	public string? LastProblem { get; private set; }

	/// <summary>Whether to run this tick; set up by default.</summary>
	protected virtual bool Ready(BraggarrSettings current) => current.IsSetUp;

	protected abstract Task RunAsync(CancellationToken ct);

	/// <summary>What the log says when a tick fails.</summary>
	protected abstract string Failure { get; }

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using var timer = new PeriodicTimer(interval, time);
		do
		{
			if (!Ready(settings.Current))
			{
				continue;
			}
			try
			{
				await RunAsync(stoppingToken);
				LastProblem = null;
			}
			catch (Exception e) when (e is not OperationCanceledException)
			{
				LastProblem = e.Message;
#pragma warning disable CA2254 // The message is a constant per host.
				logger.LogError(e, Failure);
#pragma warning restore CA2254
			}
		}
		while (await timer.WaitForNextTickAsync(stoppingToken));
	}
}
