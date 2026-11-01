namespace Braggarr.Web;

/// <summary>
/// The headers every response carries. Scripts only from this origin and never inline; forms may also post to github.com, which
/// the setup page needs to create the GitHub App from its manifest.
/// </summary>
public static class SecurityHeaders
{
	public const string ContentSecurityPolicy =
		"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: https:; connect-src 'self'; "
		+ "form-action 'self' https://github.com; frame-ancestors 'none'; base-uri 'self'; object-src 'none'";

	public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
		app.Use(async (http, next) =>
		{
			http.Response.OnStarting(() =>
			{
				var headers = http.Response.Headers;
				headers.ContentSecurityPolicy = ContentSecurityPolicy;
				headers.XFrameOptions = "DENY";
				headers.XContentTypeOptions = "nosniff";
				headers["Referrer-Policy"] = "same-origin";
				return Task.CompletedTask;
			});
			await next(http);
		});
}
