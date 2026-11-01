using System.Diagnostics;
using Braggarr.Adapters.Git;
using Microsoft.Extensions.Options;

namespace Braggarr.Web.Tests;

/// <summary>A real git on disk: a bare repo standing for GitHub, and the owner's commands on their own clone.</summary>
public static class GitFixture
{
	/// <summary>The owner's commit and push. Unsigned: these are throwaway repos, and the host's signing key is not the test's to use.</summary>
	public static void Commit(string directory, string message)
	{
		Git(directory, "add", "-A");
		Git(directory, "-c", "user.name=Owner", "-c", "user.email=owner@example.com", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", message);
		Git(directory, "push", "--quiet", "origin", "HEAD:main");
	}

	public static void Git(string directory, params string[] args)
	{
		var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardError = true, RedirectStandardOutput = true };
		foreach (var arg in args)
		{
			start.ArgumentList.Add(arg);
		}
		using var process = Process.Start(start)!;
		process.WaitForExit();
		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
		}
	}

	/// <summary>A local bare repo needs no token.</summary>
	public sealed class NoToken : IGitHubToken
	{
		public Task<string?> GetAsync(CancellationToken ct) => Task.FromResult<string?>(null);
	}

	public sealed class FixedOptions(PostsRepositoryOptions value) : IOptionsMonitor<PostsRepositoryOptions>
	{
		public PostsRepositoryOptions CurrentValue => value;

		public PostsRepositoryOptions Get(string? name) => value;

		public IDisposable? OnChange(Action<PostsRepositoryOptions, string?> listener) => null;
	}
}
