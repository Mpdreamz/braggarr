using Nullean.Entities;

namespace Braggarr.Blog;

public enum DiagnosticLevel
{
	/// <summary>The site cannot be built as written.</summary>
	Error,

	/// <summary>The site builds, but something is likely wrong. <c>--strict</c> treats it as an error.</summary>
	Warning
}

/// <summary>Something a build found, in the file it found it in (relative to the site).</summary>
public sealed record Diagnostic(DiagnosticLevel Level, string File, string Message)
{
	public static Diagnostic Error(string file, string message) => new(DiagnosticLevel.Error, file, message);

	public static Diagnostic Warning(string file, string message) => new(DiagnosticLevel.Warning, file, message);

	internal static IEnumerable<Diagnostic> From(string file, IEnumerable<Violation> violations) =>
		violations.Select(v => Error(file, v.Message));

	public override string ToString() => $"{File}: {(Level == DiagnosticLevel.Error ? "error" : "warning")}: {Message}";
}
