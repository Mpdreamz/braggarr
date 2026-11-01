using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Web.Ui.Settings;

/// <summary>
/// Each connected account's profile: what a visitor misses, Claude's suggestion from the briefs of what it posts about, and
/// applying a field where the network lets Braggarr, or copying it where it does not.
/// </summary>
public sealed class ProfilesModel(ProfileService profiles) : UiPage
{
	public IReadOnlyList<ProfileView> Views { get; private set; } = [];

	public bool CanSuggest => profiles.CanSuggest;

	/// <summary>The suggestion asked for on this response, for one account.</summary>
	public (Network Network, string Account, ProfileSuggestion Suggestion)? Suggested { get; private set; }

	public string? Message { get; private set; }

	public IReadOnlyList<string> Problems { get; private set; } = [];

	public async Task OnGetAsync(CancellationToken ct) => Views = await profiles.AllAsync(ct);

	public async Task<IResult> OnPostSuggestAsync(Network network, string account, CancellationToken ct)
	{
		switch (await profiles.SuggestAsync(network, account, ct))
		{
			case Outcome<ProfileSuggestion>.Done(var suggestion):
				Suggested = (network, account, suggestion);
				Message = $"Claude's suggestion for {account} on {NetworkIcons.Name(network)}. Nothing changes until you apply a field.";
				break;
			case Outcome<ProfileSuggestion>.Refused(var reasons):
				Problems = reasons;
				break;
		}
		Views = await profiles.AllAsync(ct);
		return Page();
	}

	public async Task<IResult> OnPostApplyAsync(Network network, string account, ProfileField field, string? value, CancellationToken ct)
	{
		switch (await profiles.ApplyAsync(network, account, field, value ?? "", Actor.Owner(User.Identity!.Name!), ct))
		{
			case Outcome<AccountProfile>.Done:
				Message = $"Changed the {ProfileService.Name(field)} of {account} on {NetworkIcons.Name(network)}.";
				break;
			case Outcome<AccountProfile>.Refused(var reasons):
				Problems = reasons;
				break;
		}
		Views = await profiles.AllAsync(ct);
		return Page();
	}

	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) => (method, handler) switch
	{
		("GET", null) => Shown(OnGetAsync(ct)),
		("POST", "suggest") => OnPostSuggestAsync(form.Enum<Network>("network"), form.Text("account")!, ct),
		("POST", "apply") => OnPostApplyAsync(form.Enum<Network>("network"), form.Text("account")!, form.Enum<ProfileField>("field"), form.Text("value"), ct),
		_ => null
	};

	protected override RazorSlices.RazorSlice Slice() => ProfilesPage.Create(this);

	public static string Id(Network network, string account, ProfileField field) =>
		$"{network.Slug()}-{string.Concat(account.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'))}-{field.ToString().ToLowerInvariant()}";
}
