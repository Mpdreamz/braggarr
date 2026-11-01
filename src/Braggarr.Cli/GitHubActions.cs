using Braggarr.Blog;

namespace Braggarr.Cli;

/// <summary>
/// Running as the GitHub Action in the repository's root: the action's inputs arrive as <c>INPUT_*</c> variables, not as
/// arguments, and what a build finds is reported as annotations on the files.
/// </summary>
internal static class GitHubActions
{
	public static bool IsRunning(Func<string, string?> env) => env("GITHUB_ACTIONS") == "true";

	/// <summary>The arguments the action's inputs stand for: always a blog build, with the options that were given.</summary>
	public static string[] Args(Func<string, string?> env)
	{
		var args = new List<string> { "blog", "build" };
		Value("--path", "INPUT_PATH");
		Value("--output", "INPUT_OUTPUT");
		Value("--url", "INPUT_URL");
		Flag("--strict", "INPUT_STRICT");
		Flag("--drafts", "INPUT_DRAFTS");
		return [.. args];

		void Value(string option, string name)
		{
			if (env(name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value))
			{
				args.Add(option);
				args.Add(value.Trim());
			}
		}

		void Flag(string option, string name)
		{
			if (string.Equals(env(name)?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
			{
				args.Add(option);
			}
		}
	}

	/// <summary>A diagnostic as a workflow command, so it shows on the file in a pull request.</summary>
	public static string Annotation(Diagnostic diagnostic, string sitePath)
	{
		var file = Path.Join(sitePath, diagnostic.File).Replace('\\', '/');
		file = file.StartsWith("./", StringComparison.Ordinal) ? file[2..] : file;
		var level = diagnostic.Level == DiagnosticLevel.Error ? "error" : "warning";
		return $"::{level} file={Property(file)},title=braggarr::{Data(diagnostic.Message)}";
	}

	private static string Data(string value) =>
		value.Replace("%", "%25", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);

	private static string Property(string value) =>
		Data(value).Replace(":", "%3A", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal);
}
