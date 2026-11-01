using System.IO.Abstractions;
using Braggarr.Blog;
using Nullean.Argh;
using Nullean.ScopedFileSystem;

namespace Braggarr.Cli;

/// <summary>Build and serve a blog: markdown posts, one theme, a feed.</summary>
public sealed class BlogCommands(TimeProvider time)
{
	public BlogCommands() : this(TimeProvider.System) { }

	/// <summary>Start a site in a folder: braggarr.yml, an about page and a first post.</summary>
	/// <param name="path">-p, The site's folder. Defaults to the current one.</param>
	/// <param name="ct">Stops the command: Ctrl+C.</param>
	[CommandName("init")]
	public async Task<int> Init(string path = ".", CancellationToken ct = default)
	{
		var (fs, root) = Scoped(path);
		if (fs.File.Exists(fs.Path.Combine(root, SiteReader.ConfigFile)))
		{
			await Console.Error.WriteLineAsync($"{SiteReader.ConfigFile} is already there: this folder is a site.");
			return 1;
		}
		foreach (var (relative, text) in Scaffold.Site(time.GetLocalNow()))
		{
			var target = fs.Path.Combine(root, relative);
			fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(target)!);
			await fs.File.WriteAllTextAsync(target, text, ct);
			Console.WriteLine($"  {relative}");
		}
		Console.WriteLine("Edit braggarr.yml, then run `braggarr blog serve`.");
		return 0;
	}

	/// <summary>Write a new post, as a draft dated now.</summary>
	/// <param name="title">The post's title. Its slug and folder name come from it.</param>
	/// <param name="path">-p, The site's folder. Defaults to the current one.</param>
	/// <param name="ct">Stops the command: Ctrl+C.</param>
	[CommandName("new")]
	public async Task<int> New([Argument] string title, string path = ".", CancellationToken ct = default)
	{
		var (fs, root) = Scoped(path);
		var (relative, text) = Scaffold.Post(title, time.GetLocalNow());
		var target = fs.Path.Combine(root, relative);
		if (fs.File.Exists(target))
		{
			await Console.Error.WriteLineAsync($"{relative} is already there.");
			return 1;
		}
		fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(target)!);
		await fs.File.WriteAllTextAsync(target, text, ct);
		Console.WriteLine(relative);
		return 0;
	}

	/// <summary>Build the site into static files.</summary>
	/// <param name="path">-p, The site's folder. Defaults to the current one.</param>
	/// <param name="output">-o, Where the site is written. Defaults to .artifacts/site in the site's folder. What an earlier build left there is removed.</param>
	/// <param name="strict">Fail on warnings too: a link that leads nowhere, an image without alt text, a date without an offset.</param>
	/// <param name="drafts">Publish drafts and posts dated later, as a preview does.</param>
	/// <param name="url">The site's address for this build, instead of the url in braggarr.yml.</param>
	/// <param name="ct">Stops the command: Ctrl+C.</param>
	[CommandName("build")]
	public async Task<int> Build(
		string path = ".", string? output = null, bool strict = false, bool drafts = false, Uri? url = null, CancellationToken ct = default)
	{
		var (fs, root, target) = Scoped(path, output);
		var read = await SiteReader.ReadAsync(fs, root, ct);
		var diagnostics = read.Diagnostics.ToList();
		if (read.Site is { } site && diagnostics.All(d => d.Level != DiagnosticLevel.Error))
		{
			var built = await SiteBuilder.RenderAsync(fs, root, site, new BuildOptions(time.GetUtcNow()) { Drafts = drafts, Url = url }, ct);
			diagnostics.AddRange(built.Diagnostics);
			if (Failed(diagnostics, strict) == 0)
			{
				diagnostics.AddRange(await SiteBuilder.WriteAsync(built, fs, target, ct));
				if (Failed(diagnostics, strict) == 0)
				{
					var shown = fs.Path.GetRelativePath(fs.Directory.GetCurrentDirectory(), target);
					Console.WriteLine($"Built {built.Files.Count} files into {(shown.StartsWith("..", StringComparison.Ordinal) ? target : shown)}.");
				}
			}
		}
		await Report(diagnostics, path);
		return Failed(diagnostics, strict);
	}

	/// <summary>Serve the site at http://localhost:3000 with drafts shown, rebuilding and reloading the browser when a file changes.</summary>
	/// <param name="path">-p, The site's folder. Defaults to the current one.</param>
	/// <param name="port">The port to listen on.</param>
	/// <param name="ct">Stops the command: Ctrl+C.</param>
	[CommandName("serve")]
	public async Task<int> Serve(string path = ".", int port = 3000, CancellationToken ct = default)
	{
		var (fs, root) = Scoped(path);
		await using var server = new PreviewServer(fs, root, port, time);
		await server.RunAsync(ct);
		return 0;
	}

	/// <summary>Each diagnostic on its own line; in a GitHub workflow, also as an annotation on its file.</summary>
	internal static async Task Report(IEnumerable<Diagnostic> diagnostics, string sitePath)
	{
		var annotate = GitHubActions.IsRunning(Environment.GetEnvironmentVariable);
		foreach (var diagnostic in diagnostics.OrderBy(d => d.Level).ThenBy(d => d.File, StringComparer.Ordinal))
		{
			await Console.Error.WriteLineAsync(diagnostic.ToString());
			if (annotate)
			{
				Console.WriteLine(GitHubActions.Annotation(diagnostic, sitePath));
			}
		}
	}

	private static int Failed(IEnumerable<Diagnostic> diagnostics, bool strict) =>
		diagnostics.Any(d => d.Level == DiagnosticLevel.Error || (strict && d.Level == DiagnosticLevel.Warning)) ? 1 : 0;

	/// <summary>The real disk, scoped to the site's folder: nothing outside it, through a link, or hidden is read or written.</summary>
	private static (IFileSystem Fs, string Root) Scoped(string path)
	{
		var (fs, root, _) = Scoped(path, null);
		return (fs, root);
	}

	/// <summary>The real disk, scoped to the site's folder and the output folder. The default output, <c>.artifacts</c>, is the one hidden name allowed.</summary>
	private static (IFileSystem Fs, string Root, string Output) Scoped(string path, string? output)
	{
		var disk = new FileSystem();
		var root = disk.Path.GetFullPath(path);
		var target = disk.Path.GetFullPath(output ?? disk.Path.Combine(root, ".artifacts", "site"));
		// Roots may not nest: an output inside the site is covered by the site's root.
		var inside = target.StartsWith(root.TrimEnd(disk.Path.DirectorySeparatorChar) + disk.Path.DirectorySeparatorChar, StringComparison.Ordinal);
		var scoped = new ScopedFileSystem(disk, new ScopedFileSystemOptions(inside ? [root] : [root, target]) { AllowedHiddenFolderNames = [".artifacts"] });
		return (scoped, root, target);
	}
}
