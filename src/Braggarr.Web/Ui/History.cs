namespace Braggarr.Web.Ui;

/// <summary>The old address of the history: it lives under Activity now.</summary>
public sealed class HistoryModel : UiPage
{
	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) =>
		method == "GET" && handler is null ? Task.FromResult(Redirect("/Activity?tab=history")) : null;

	protected override RazorSlices.RazorSlice Slice() => Shared.Blank.Create();
}
