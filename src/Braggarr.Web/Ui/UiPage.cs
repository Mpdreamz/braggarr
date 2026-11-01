using System.Security.Claims;

namespace Braggarr.Web.Ui;

/// <summary>
/// An owner's page: its handlers, the state they leave, and the slice that shows it. <see cref="UiPages"/> picks a handler as
/// Razor Pages did: by HTTP method (HEAD runs GET's) and the <c>handler</c> in the query string, falling back to the method's
/// handler without a name, and showing the page untouched when there is none.
/// </summary>
public abstract class UiPage
{
	public HttpContext HttpContext { get; internal set; } = null!;

	public ClaimsPrincipal User => HttpContext.User;

	public HttpRequest Request => HttpContext.Request;

	/// <summary>
	/// The handler for <paramref name="method"/> (GET, POST, …) named <paramref name="handler"/>, lowercase, or null for the one
	/// without a name; null when the page has no such handler.
	/// </summary>
	protected internal abstract Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct);

	/// <summary>What the properties were bound from; null on a GET, which binds none.</summary>
	public Binder? Bound { get; internal set; }

	/// <summary>
	/// The properties a form sets on any request but a GET, as <c>[BindProperty]</c> did: a property the request has no value for
	/// keeps the one it has.
	/// </summary>
	protected internal virtual void Bind(Binder form)
	{
	}

	/// <summary>
	/// What a field bound to <paramref name="name"/> shows: as posted when it was, else <paramref name="model"/>, as the
	/// <c>asp-for</c> tag helpers did.
	/// </summary>
	public string Field(string name, object? model) =>
		(Bound?.Has(name) == true ? Bound.Raw(name) : Convert.ToString(model, global::System.Globalization.CultureInfo.CurrentCulture)) ?? "";

	/// <summary>The page as its handler left it.</summary>
	public IResult Page() => new Html(Slice());

	/// <summary>The slice that shows this page.</summary>
	protected abstract RazorSlices.RazorSlice Slice();

	protected Task<IResult> Shown() => Task.FromResult(Page());

	protected Task<IResult> Shown(Action handled)
	{
		handled();
		return Task.FromResult(Page());
	}

	protected async Task<IResult> Shown(Task handled)
	{
		await handled;
		return Page();
	}

	protected static IResult Redirect(string url) => Results.Redirect(url);

	protected static IResult NotFound() => Results.NotFound();

	protected static IResult Forbid() => Results.Forbid();
}
