using System.Text;
using RazorSlices;

namespace Braggarr.Web.Ui;

/// <summary>
/// A page rendered whole before a byte is sent, as Razor Pages did: a form's anti-forgery token can still set its cookie and
/// headers, and a page that throws halfway shows the error page instead of half a page.
/// </summary>
public sealed class Html(RazorSlice slice) : IResult
{
	public async Task ExecuteAsync(HttpContext httpContext)
	{
		slice.HttpContext = httpContext;
		slice.ServiceProvider = httpContext.RequestServices;
		var html = new StringBuilder();
		await slice.RenderAsync(html, cancellationToken: httpContext.RequestAborted);
		httpContext.Response.ContentType = "text/html; charset=utf-8";
		await httpContext.Response.WriteAsync(html.ToString(), httpContext.RequestAborted);
	}
}
