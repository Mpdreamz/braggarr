namespace Braggarr.Web;

/// <summary>
/// The console log, with every secret Braggarr holds replaced by <c>[redacted]</c> wherever it appears in a message or exception,
/// so a token that ends up in an error from some library never reaches the container log.
/// </summary>
public sealed class RedactingConsoleProvider(Func<IReadOnlyCollection<string>> secrets, TextWriter? output = null) : ILoggerProvider
{
	private readonly TextWriter _output = output ?? Console.Out;
	private readonly Lock _lock = new();

	public ILogger CreateLogger(string categoryName) => new RedactingLogger(this, categoryName);

	public void Dispose() { }

	internal string Redact(string text)
	{
		foreach (var secret in secrets())
		{
			if (secret.Length >= 6)
			{
				text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
			}
		}
		return text;
	}

	private void Write(string line)
	{
		lock (_lock)
		{
			_output.WriteLine(line);
		}
	}

	private sealed class RedactingLogger(RedactingConsoleProvider provider, string category) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information || (logLevel >= LogLevel.Debug && category.StartsWith("Braggarr", StringComparison.Ordinal));

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (!IsEnabled(logLevel) || (logLevel < LogLevel.Warning && category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)))
			{
				return;
			}
			var level = logLevel switch
			{
				LogLevel.Critical => "crit",
				LogLevel.Error => "fail",
				LogLevel.Warning => "warn",
				LogLevel.Information => "info",
				_ => "dbug"
			};
			// Wall-clock time on purpose, the one place: a log line says when it was written, whatever the app's clock says.
#pragma warning disable RS0030
			var line = $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} {level} {category}: {formatter(state, exception)}";
#pragma warning restore RS0030
			if (exception is not null)
			{
				line += Environment.NewLine + exception;
			}
			provider.Write(provider.Redact(line));
		}
	}
}

/// <summary>
/// The values to redact. It has no dependencies on purpose: the log provider is built before anything else, so it cannot ask the
/// secret store (which needs Data Protection, which logs) without a cycle. The secret store fills it instead.
/// </summary>
public sealed class RedactionList
{
	private volatile string[] _values = [];

	public IReadOnlyCollection<string> Values => _values;

	public void Update(IEnumerable<string> values) => _values = [.. values.Where(v => v.Length >= 6).OrderByDescending(v => v.Length)];
}

public static class RedactingConsoleExtensions
{
	public static ILoggingBuilder AddRedactingConsole(this ILoggingBuilder logging, RedactionList redactions)
	{
		_ = logging.ClearProviders();
		_ = logging.AddProvider(new RedactingConsoleProvider(() => redactions.Values));
		return logging;
	}
}
