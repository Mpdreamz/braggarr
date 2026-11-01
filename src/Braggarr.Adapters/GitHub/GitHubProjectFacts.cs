using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Adapters.GitHub;

/// <summary>
/// A repository's visibility, stars, language, website and latest release through GitHub's API, and its pictures: the
/// website's icon and social preview, read from its page, else the repository's own social preview.
/// </summary>
public sealed partial class GitHubProjectFacts(GitHubClient github, IReleases releases, HttpClient web) : IProjectFacts
{
	/// <summary>A website is read up to this much: the tags that name its pictures are in its head.</summary>
	public const int PageLimit = 512 * 1024;

	public async Task<ProjectFacts> ReadAsync(string repository, CancellationToken ct)
	{
		Repo repo;
		try
		{
			repo = await github.GetAsync<Repo>($"repos/{repository}", ct);
		}
		catch (GitHubException)
		{
			return new ProjectFacts(repository, ProjectVisibility.Unknown);
		}
		if (repo.Private)
		{
			return new ProjectFacts(repository, ProjectVisibility.Private);
		}
		var homepage = Https(repo.Homepage);
		var (icon, image) = homepage is null ? (null, null) : await FromSiteAsync(homepage, ct);
		return new ProjectFacts(repository, ProjectVisibility.Public)
		{
			Stars = repo.StargazersCount,
			Language = repo.Language,
			Homepage = homepage,
			Release = await LatestAsync(repository, ct),
			IconUrl = icon,
			ImageUrl = image ?? await SocialPreviewAsync(repository, ct)
		};
	}

	private async Task<AboutRelease?> LatestAsync(string repository, CancellationToken ct)
	{
		try
		{
			var all = await releases.LatestAsync(repository, ct);
			return (all.FirstOrDefault(r => !r.Prerelease) ?? (all.Count > 0 ? all[0] : null)) is { } latest
				? new AboutRelease(latest.Tag, latest.PublishedAt, latest.Url)
				: null;
		}
		catch (GitHubException)
		{
			return null;
		}
	}

	/// <summary>The repository's own social preview; null for GitHub's generated card, which only repeats its name.</summary>
	private async Task<string?> SocialPreviewAsync(string repository, CancellationToken ct)
	{
		var (owner, name) = (repository[..repository.IndexOf('/')], repository[(repository.IndexOf('/') + 1)..]);
		try
		{
			var answer = await github.PostAsync<GraphAnswer>("graphql", new GraphQuery(
				"query($owner: String!, $name: String!) { repository(owner: $owner, name: $name) { usesCustomOpenGraphImage openGraphImageUrl } }",
				new GraphVariables(owner, name)), ct);
			return answer.Data?.Repository is { UsesCustomOpenGraphImage: true } r ? Https(r.OpenGraphImageUrl) : null;
		}
		catch (GitHubException)
		{
			return null;
		}
	}

	/// <summary>The icon and social preview a website's page names: its apple-touch-icon, else its SVG icon, else its largest PNG; its og:image.</summary>
	internal async Task<(string? Icon, string? Image)> FromSiteAsync(string homepage, CancellationToken ct)
	{
		string html;
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, homepage);
			request.Headers.UserAgent.ParseAdd("Braggarr/1.0");
			using var response = await web.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
			if (response.StatusCode != HttpStatusCode.OK)
			{
				return (null, null);
			}
			await using var stream = await response.Content.ReadAsStreamAsync(ct);
			var buffer = new byte[PageLimit];
			var read = 0;
			int n;
			while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), ct)) > 0)
			{
				read += n;
			}
			html = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
		}
		catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
		{
			return (null, null);
		}
		return Pictures(new Uri(homepage), html);
	}

	/// <summary>The pictures a page's head names, as https addresses resolved against the page.</summary>
	internal static (string? Icon, string? Image) Pictures(Uri page, string html)
	{
		var tags = Tag().Matches(html).Select(m => m.Value).ToList();
		var image = tags.Where(t => Attribute(t, "property") is "og:image").Select(t => Resolve(page, Attribute(t, "content"))).FirstOrDefault(u => u is not null);
		var icons = tags
			.Select(t => (Rel: Attribute(t, "rel")?.ToLowerInvariant(), Type: Attribute(t, "type") ?? "", Sizes: Attribute(t, "sizes") ?? "", Href: Resolve(page, Attribute(t, "href"))))
			.Where(i => i.Href is not null && i.Rel is "icon" or "apple-touch-icon" or "shortcut icon")
			.OrderBy(i => i.Rel == "apple-touch-icon" ? 0 : i.Type.Contains("svg", StringComparison.Ordinal) || i.Href!.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
			.ThenByDescending(i => Size().Match(i.Sizes) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0)
			.ToList();
		return (icons.FirstOrDefault().Href, image);
	}

	private static string? Attribute(string tag, string name) =>
		Regex.Match(tag, $"""\b{name}\s*=\s*["']([^"']*)["']""", RegexOptions.IgnoreCase) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

	/// <summary>An address on the page, resolved; only https, so a page cannot point Braggarr at its own network.</summary>
	private static string? Resolve(Uri page, string? href) =>
		!string.IsNullOrWhiteSpace(href) && Uri.TryCreate(page, href, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;

	private static string? Https(string? url) =>
		Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;

	[GeneratedRegex(@"<(?:meta|link)\b[^>]*>", RegexOptions.IgnoreCase)]
	private static partial Regex Tag();

	[GeneratedRegex(@"^(\d+)x\d+$")]
	private static partial Regex Size();

	internal sealed record Repo(bool Private, int StargazersCount, string? Language, string? Homepage);

	internal sealed record GraphQuery(string Query, GraphVariables Variables);

	internal sealed record GraphVariables(string Owner, string Name);

	internal sealed record GraphAnswer(GraphData? Data);

	internal sealed record GraphData(GraphRepository? Repository);

	internal sealed record GraphRepository(
		[property: JsonPropertyName("usesCustomOpenGraphImage")] bool UsesCustomOpenGraphImage,
		[property: JsonPropertyName("openGraphImageUrl")] string? OpenGraphImageUrl);
}
