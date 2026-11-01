using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Braggarr.Web.Ui;

/// <summary>
/// Signing out. A POST to /logout goes to the endpoint in <see cref="Auth.GitHubSignIn"/>, which routing prefers for having a
/// method; this page answers the rest.
/// </summary>
public sealed class LogoutModel : UiPage
{
	public async Task<IResult> OnPostAsync()
	{
		await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
		return Redirect("/");
	}

	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) => (method, handler) switch
	{
		("GET", null) => Task.FromResult(Redirect("/")),
		("POST", null) => OnPostAsync(),
		_ => null
	};

	protected override RazorSlices.RazorSlice Slice() => Shared.Blank.Create();
}
