namespace Braggarr.Web.Ui;

/// <summary>Where a GitHub account that is not the owner's ends up.</summary>
public sealed class DeniedModel : UiPage
{
	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) => null;

	protected override RazorSlices.RazorSlice Slice() => Denied.Create(this);
}
