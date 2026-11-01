using System.IO.Abstractions;
using System.Text.Json;
using Braggarr.Services;

namespace Braggarr.Adapters.Claude;

/// <summary>Every model call, one JSON line each, in <c>/config/usage.jsonl</c>, and what they cost in dollars.</summary>
public sealed class UsageLog(IFileSystem fs, string path) : IUsageLog
{
	private readonly Lock _lock = new();

	public sealed record Entry(DateTimeOffset At, string Purpose, Usage Usage)
	{
		public decimal Dollars
		{
			get
			{
				var (input, output, cacheRead) = ClaudeOptions.Price(Usage.Model);
				// Writing the cache costs 1.25 times input; reading it the cache-read price.
				return ((Usage.InputTokens * input) + (Usage.CacheWriteTokens * input * 1.25m) + (Usage.CacheReadTokens * cacheRead) + (Usage.OutputTokens * output)) / 1_000_000m;
			}
		}
	}

	public void Record(Usage usage, string purpose, DateTimeOffset at)
	{
		lock (_lock)
		{
			_ = fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(path)!);
			fs.File.AppendAllText(path, JsonSerializer.Serialize(new Entry(at, purpose, usage), StoredJson.Default.Entry) + "\n");
		}
	}

	public IReadOnlyList<Entry> Since(DateTimeOffset from)
	{
		lock (_lock)
		{
			if (!fs.File.Exists(path))
			{
				return [];
			}
			return [.. fs.File.ReadLines(path).Select(l => JsonSerializer.Deserialize(l, StoredJson.Default.Entry)).OfType<Entry>().Where(e => e.At >= from)];
		}
	}
}
