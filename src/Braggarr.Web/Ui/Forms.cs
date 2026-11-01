using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.WebUtilities;

namespace Braggarr.Web.Ui;

/// <summary>What the Razor Pages tag helpers wrote into the pages, written the same way by hand.</summary>
public static class Forms
{
	private static readonly ConcurrentDictionary<(string Path, DateTimeOffset Modified), string> Versions = new();

	/// <summary>
	/// The anti-forgery token as the form tag helper put it before a post form's <c>&lt;/form&gt;</c>. Like the tag helper, it
	/// stores the cookie token on the response when the browser has none.
	/// </summary>
	public static HtmlString Token(HttpContext http)
	{
		var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
		return new HtmlString($"<input name=\"{tokens.FormFieldName}\" type=\"hidden\" value=\"{HtmlEncoder.Default.Encode(tokens.RequestToken!)}\" />");
	}

	/// <summary>
	/// A page's address with its route values and handler in the query string, as <c>asp-page-handler</c> and <c>asp-route-*</c>
	/// made it: values in the order given, empty ones left out, the handler last.
	/// </summary>
	public static string Handler(string page, string handler, params (string Name, string? Value)[] values) => Url(page, [.. values, ("handler", handler)]);

	public static string Handler(string page, string handler, IEnumerable<KeyValuePair<string, string>> values) =>
		Url(page, [.. values.Select(v => (v.Key, (string?)v.Value)), ("handler", handler)]);

	/// <summary>A page's address with route values in the query string, as <c>RedirectToPage</c> made it.</summary>
	public static string Url(string page, params (string Name, string? Value)[] values)
	{
		var url = new StringBuilder(page);
		var separator = '?';
		foreach (var (name, value) in values)
		{
			if (!string.IsNullOrEmpty(value))
			{
				_ = url.Append(separator).Append(UrlEncoder.Default.Encode(name)).Append('=').Append(UrlEncoder.Default.Encode(value));
				separator = '&';
			}
		}
		return url.ToString();
	}

	/// <summary>A path segment, encoded as a route value is when it fills one (<c>/Projects/{project}</c>).</summary>
	public static string Segment(string value) => UrlEncoder.Default.Encode(value);

	/// <summary>A file under wwwroot with its content's hash appended, as <c>asp-append-version</c> did.</summary>
	public static string Versioned(HttpContext http, string path)
	{
		var file = http.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider.GetFileInfo(path);
		var href = http.Request.PathBase + path;
		if (!file.Exists)
		{
			return href;
		}
		var version = Versions.GetOrAdd((path, file.LastModified), _ =>
		{
			using var stream = file.CreateReadStream();
			return WebEncoders.Base64UrlEncode(SHA256.HashData(stream));
		});
		return $"{href}?v={version}";
	}
}
