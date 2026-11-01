using System.IO.Abstractions;
using System.Text;
using Braggarr.Blog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Braggarr.Cli;

/// <summary>
/// Serves a site from memory with drafts shown. A change in the site's folder rebuilds it, and every open page reloads itself
/// through a server-sent event; the script that listens is added to pages here, never by the theme.
/// </summary>
internal sealed class PreviewServer : IAsyncDisposable
{
	private const string ReloadPath = "/_braggarr/reload";

	private static readonly byte[] ReloadScript = Encoding.UTF8.GetBytes(
		$"<script>new EventSource('{ReloadPath}').onmessage = () => location.reload();</script>");

	private static readonly FileExtensionContentTypeProvider ContentTypes = new();

	private readonly IFileSystem _fs;
	private readonly string _root;
	private readonly int _port;
	private readonly TimeProvider _time;
	private readonly SemaphoreSlim _building = new(1, 1);
	private readonly IFileSystemWatcher _watcher;
	private BuiltSite _site = new(new Dictionary<string, byte[]>(), []);
	private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private CancellationTokenSource? _debounce;

	public PreviewServer(IFileSystem fs, string root, int port, TimeProvider time)
	{
		_fs = fs;
		_root = root;
		_port = port;
		_time = time;
		_watcher = fs.FileSystemWatcher.New(root);
		_watcher.IncludeSubdirectories = true;
		_watcher.Changed += (_, e) => Touched(e.FullPath);
		_watcher.Created += (_, e) => Touched(e.FullPath);
		_watcher.Deleted += (_, e) => Touched(e.FullPath);
		_watcher.Renamed += (_, e) => Touched(e.FullPath);
	}

	public async Task RunAsync(CancellationToken ct)
	{
		await RebuildAsync(ct);
		_watcher.EnableRaisingEvents = true;

		var builder = WebApplication.CreateSlimBuilder();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.WebHost.UseUrls($"http://localhost:{_port}");
		var app = builder.Build();
		app.MapGet(ReloadPath, ReloadAsync);
		// Every other path, files included: MapFallback's pattern would skip any that looks like a file.
		app.Run(ServeAsync);

		await app.StartAsync(ct);
		Console.WriteLine($"Serving http://localhost:{_port}/ with drafts. Ctrl+C stops.");
		await app.WaitForShutdownAsync(ct);
	}

	/// <summary>A file in the site changed: rebuild once things settle, ignoring the build output and hidden folders.</summary>
	private void Touched(string fullPath)
	{
		var relative = _fs.Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
		if (relative.Split('/').Any(s => s.StartsWith('.')))
		{
			return;
		}
		var debounce = new CancellationTokenSource();
		Interlocked.Exchange(ref _debounce, debounce)?.Cancel();
		_ = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(TimeSpan.FromMilliseconds(150), _time, debounce.Token);
				await RebuildAsync(debounce.Token);
			}
			catch (OperationCanceledException)
			{
				// A newer change rebuilds instead.
			}
		});
	}

	private async Task RebuildAsync(CancellationToken ct)
	{
		await _building.WaitAsync(ct);
		try
		{
			var read = await SiteReader.ReadAsync(_fs, _root, ct);
			var diagnostics = read.Diagnostics.ToList();
			if (read.Site is { } site)
			{
				var built = await SiteBuilder.RenderAsync(_fs, _root, site, new BuildOptions(_time.GetUtcNow())
				{
					Drafts = true,
					Url = new Uri($"http://localhost:{_port}/")
				}, ct);
				diagnostics.AddRange(built.Diagnostics);
				_site = built;
			}
			Console.WriteLine($"Built at {_time.GetLocalNow():HH:mm:ss}: {diagnostics.Count(d => d.Level == DiagnosticLevel.Error)} errors, {diagnostics.Count(d => d.Level == DiagnosticLevel.Warning)} warnings.");
			await BlogCommands.Report(diagnostics, _root);
		}
		finally
		{
			_building.Release();
		}
		Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
	}

	private async Task ServeAsync(HttpContext context)
	{
		var path = context.Request.Path.Value ?? "/";
		var file = _site.Find(path);
		var name = path.EndsWith('/') || !_fs.Path.HasExtension(path) ? "index.html" : path;
		if (file is null)
		{
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			file = _site.Find("/404.html") ?? [];
			name = "404.html";
		}
		else if (!path.EndsWith('/') && !_fs.Path.HasExtension(path))
		{
			// A folder without its slash: relative links on the page would resolve against the parent.
			context.Response.Redirect(path + "/");
			return;
		}
		context.Response.ContentType = ContentTypes.TryGetContentType(name, out var type) ? type : "application/octet-stream";
		context.Response.Headers.CacheControl = "no-store";
		if (name.EndsWith(".html", StringComparison.Ordinal))
		{
			var body = file.AsMemory();
			var end = file.AsSpan().LastIndexOf("</body>"u8);
			var cut = end < 0 ? file.Length : end;
			await context.Response.Body.WriteAsync(body[..cut], context.RequestAborted);
			await context.Response.Body.WriteAsync(ReloadScript, context.RequestAborted);
			await context.Response.Body.WriteAsync(body[cut..], context.RequestAborted);
			return;
		}
		await context.Response.Body.WriteAsync(file, context.RequestAborted);
	}

	/// <summary>One event when the site is rebuilt, then the page reloads and opens a new stream.</summary>
	private async Task ReloadAsync(HttpContext context)
	{
		context.Response.ContentType = "text/event-stream";
		context.Response.Headers.CacheControl = "no-store";
		await context.Response.Body.FlushAsync(context.RequestAborted);
		try
		{
			await _changed.Task.WaitAsync(context.RequestAborted);
			await context.Response.WriteAsync("data: reload\n\n", context.RequestAborted);
		}
		catch (OperationCanceledException)
		{
			// The page closed.
		}
	}

	public ValueTask DisposeAsync()
	{
		_watcher.Dispose();
		_building.Dispose();
		_debounce?.Dispose();
		return ValueTask.CompletedTask;
	}
}
