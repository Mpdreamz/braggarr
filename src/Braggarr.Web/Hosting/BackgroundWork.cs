using System.Threading.Channels;

namespace Braggarr.Web.Hosting;

/// <summary>
/// Work a page or the API starts but should not wait for, such as planning and drafting an introduction campaign, which can take
/// minutes. One job runs at a time; a failure is logged and the next job runs.
/// </summary>
public sealed class BackgroundWork(ILogger<BackgroundWork> logger) : BackgroundService
{
	private readonly Channel<(string Name, Func<CancellationToken, Task> Job)> _jobs = Channel.CreateUnbounded<(string, Func<CancellationToken, Task>)>();

	public void Enqueue(string name, Func<CancellationToken, Task> job) => _jobs.Writer.TryWrite((name, job));

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await foreach (var (name, job) in _jobs.Reader.ReadAllAsync(stoppingToken))
		{
			try
			{
				await job(stoppingToken);
			}
			catch (Exception e) when (e is not OperationCanceledException)
			{
				logger.LogError(e, "{Job} failed.", name);
			}
		}
	}
}
