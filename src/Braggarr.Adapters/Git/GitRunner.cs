using System.Diagnostics;
using System.Text;

namespace Braggarr.Adapters.Git;

public sealed class GitException(string message) : Exception(message);

/// <summary>
/// Runs the git CLI in one directory. A token, when given, travels as an HTTP header through git's environment config, so it is
/// never written to <c>.git/config</c> and never appears in the process list.
/// </summary>
public sealed class GitRunner(string directory)
{
	public string Directory { get; } = directory;

	public async Task<string> RunAsync(string? token, CancellationToken ct, params string[] args)
	{
		var start = new ProcessStartInfo("git")
		{
			WorkingDirectory = Directory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		// Braggarr commits as itself, and has no key: a signing setup in the host's global git config is someone else's, never
		// applied to Braggarr's commits.
		foreach (var arg in (string[])["-c", "user.name=Braggarr", "-c", "user.email=braggarr@users.noreply.github.com", "-c", "commit.gpgsign=false", "-c", "core.fsync=committed", .. args])
		{
			start.ArgumentList.Add(arg);
		}
		start.Environment["GIT_TERMINAL_PROMPT"] = "0";
		if (token is not null)
		{
			var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{token}"));
			start.Environment["GIT_CONFIG_COUNT"] = "1";
			start.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
			start.Environment["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: basic {basic}";
		}

		using var process = Process.Start(start) ?? throw new GitException("git could not be started.");
		var stdout = process.StandardOutput.ReadToEndAsync(ct);
		var stderr = process.StandardError.ReadToEndAsync(ct);
		await process.WaitForExitAsync(ct);
		if (process.ExitCode != 0)
		{
			throw new GitException($"git {args.FirstOrDefault()} failed ({process.ExitCode}): {(await stderr).Trim()}");
		}
		return (await stdout).TrimEnd();
	}
}
