using System.Net;
using Braggarr.Services;

namespace Braggarr.Adapters.Blog;

/// <summary>Whether a blog post's address answers: a 200, once its site has built and serves it.</summary>
public sealed class SiteProbe(HttpClient http) : ISites
{
	public async Task<bool> AnswersAsync(string url, CancellationToken ct)
	{
		using var response = await http.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, ct);
		return response.StatusCode == HttpStatusCode.OK;
	}
}
