using System.Net;
using Braggarr.Services;

namespace Braggarr.Adapters.Blog;

/// <summary>Pictures from the web, for an about page: PNG, JPEG, GIF, WebP or SVG over https, up to <see cref="Limit"/>.</summary>
public sealed class WebPictures(HttpClient http) : IPictures
{
	public const int Limit = 2 * 1024 * 1024;

	public async Task<Picture?> FetchAsync(string url, CancellationToken ct)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
		{
			return null;
		}
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, uri);
			request.Headers.UserAgent.ParseAdd("Braggarr/1.0");
			using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
			var extension = response.Content.Headers.ContentType?.MediaType switch
			{
				"image/png" => ".png",
				"image/jpeg" => ".jpg",
				"image/gif" => ".gif",
				"image/webp" => ".webp",
				"image/svg+xml" => ".svg",
				_ => null
			};
			if (response.StatusCode != HttpStatusCode.OK || extension is null || response.Content.Headers.ContentLength > Limit)
			{
				return null;
			}
			await using var stream = await response.Content.ReadAsStreamAsync(ct);
			using var copy = new MemoryStream();
			var buffer = new byte[81920];
			int n;
			while ((n = await stream.ReadAsync(buffer, ct)) > 0)
			{
				if (copy.Length + n > Limit)
				{
					return null;
				}
				copy.Write(buffer, 0, n);
			}
			return new Picture(extension, copy.ToArray());
		}
		catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
		{
			return null;
		}
	}
}
