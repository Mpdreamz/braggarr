namespace Braggarr.Web.Ui.Campaigns;

/// <summary>The campaigns are the home page now.</summary>
public sealed class IndexModel : UiPage
{
	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) =>
		method == "GET" && handler is null ? Task.FromResult(Redirect("/")) : null;

	protected override RazorSlices.RazorSlice Slice() => Shared.Blank.Create();
}
