using Braggarr.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Braggarr.Adapters;

public sealed class NtfyOptions
{
	public string Server { get; set; } = "https://ntfy.sh";

	/// <summary>The topic to publish to. Unset, notices are only logged. On ntfy.sh, anyone who knows the topic can read it, so make it long and random.</summary>
	public string? Topic { get; set; }
}

/// <summary>Notices as ntfy pushes: a tap opens the link (the issue, the pull request, the post).</summary>
public sealed class NtfyNotifier(HttpClient http, IOptionsMonitor<NtfyOptions> options, ILogger<NtfyNotifier> logger) : INotifier
{
	public async Task NotifyAsync(Notice notice, CancellationToken ct)
	{
		if (logger.IsEnabled(LogLevel.Information))
		{
			logger.LogInformation("{Title}: {Message}", notice.Title, notice.Message);
		}
		if (options.CurrentValue is not { Topic: { Length: > 0 } topic } ntfy)
		{
			return;
		}
		using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{ntfy.Server.TrimEnd('/')}/{topic}")) { Content = new StringContent(notice.Message) };
		request.Headers.Add("Title", notice.Title);
		request.Headers.Add("Priority", notice.Level switch { NoticeLevel.Problem => "high", NoticeLevel.Action => "default", _ => "low" });
		request.Headers.Add("Tags", notice.Level switch { NoticeLevel.Problem => "warning", NoticeLevel.Action => "loudspeaker", _ => "information_source" });
		if (notice.Url is { } url)
		{
			request.Headers.Add("Click", url);
		}
		if (notice.Actions.Count > 0)
		{
			// ntfy shows at most three buttons; a "view" button opens its URL.
			request.Headers.Add("Actions", string.Join("; ", notice.Actions.Take(3).Select(a => $"view, {a.Label.Replace(",", "", StringComparison.Ordinal).Replace(";", "", StringComparison.Ordinal)}, {a.Url}")));
		}
		try
		{
			using var response = await http.SendAsync(request, ct);
			_ = response.EnsureSuccessStatusCode();
		}
		catch (HttpRequestException e)
		{
			logger.LogWarning(e, "ntfy did not take the notice \"{Title}\".", notice.Title);
		}
	}
}
