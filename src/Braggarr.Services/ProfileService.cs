using Braggarr.Domain;
using Microsoft.Extensions.Logging;

namespace Braggarr.Services;

/// <summary>One account's profile as read, with what it misses; or why it could not be read.</summary>
public sealed record ProfileView(Network Network, string Account, AccountProfile? Profile, IReadOnlyList<ProfileFinding> Findings, string? Problem);

/// <summary>
/// The owner's profiles: read from each connected account and checked for what a visitor misses; suggested by Claude from
/// the briefs of the projects the account posts about; changed, field by field, by the owner where the network allows.
/// </summary>
public sealed class ProfileService(
	IProfiles profiles,
	IAccounts accounts,
	ICampaignRepository repository,
	IDrafters drafters,
	IUsageLog usage,
	TimeProvider time,
	ILogger<ProfileService> logger
)
{
	public bool CanSuggest => drafters.Current is not null;

	/// <summary>Every connected account on every network that posts with one.</summary>
	public async Task<IReadOnlyList<ProfileView>> AllAsync(CancellationToken ct)
	{
		var views = new List<ProfileView>();
		foreach (var network in Networks.All.Where(Profiles.HasProfile))
		{
			foreach (var account in accounts.Connected(network))
			{
				views.Add(await ViewAsync(network, account, ct));
			}
		}
		return views;
	}

	public async Task<ProfileView> ViewAsync(Network network, string account, CancellationToken ct)
	{
		try
		{
			var profile = await profiles.ReadAsync(network, account, ct);
			return new ProfileView(network, account, profile, Profiles.Check(profile), null);
		}
		catch (Exception e) when (e is PublishException or HttpRequestException)
		{
			logger.LogWarning("Reading the {Network} profile of {Account} failed: {Problem}", network, account, e.Message);
			return new ProfileView(network, account, null, [], e.Message);
		}
	}

	/// <summary>
	/// The briefs of the projects an account posts about: those whose campaigns the defaults send to it. An account no project
	/// is sent to (the only account, or one kept for later) gets them all.
	/// </summary>
	public async Task<IReadOnlyList<ProjectBrief>> AboutAsync(Network network, string account, CancellationToken ct)
	{
		var briefs = await repository.BriefsAsync(ct);
		var mine = briefs.Where(b => accounts.Choose(network, b.Project)?.Account == account).ToList();
		return mine.Count > 0 ? mine : briefs;
	}

	/// <summary>Claude's display name, bio and link for an account. Nothing changes until the owner applies a field.</summary>
	public async Task<Outcome<ProfileSuggestion>> SuggestAsync(Network network, string account, CancellationToken ct)
	{
		if (drafters.Current is not { } drafter)
		{
			return new Outcome<ProfileSuggestion>.Refused("Claude is not set up: add a key under Settings → Claude.");
		}
		var view = await ViewAsync(network, account, ct);
		if (view.Profile is not { } current)
		{
			return new Outcome<ProfileSuggestion>.Refused(view.Problem ?? $"The {network} profile of {account} could not be read.");
		}
		try
		{
			var (suggestion, cost) = await drafter.ProfileAsync(new ProfileRequest(current, await AboutAsync(network, account, ct), await repository.VoiceAsync(ct)), ct);
			usage.Record(cost, $"profile {network.Slug()} {account}", time.GetUtcNow());
			return new Outcome<ProfileSuggestion>.Done(suggestion);
		}
		catch (DraftException e)
		{
			logger.LogWarning("Suggesting a {Network} profile for {Account} failed: {Problem}", network, account, e.Message);
			return new Outcome<ProfileSuggestion>.Refused($"Claude could not write a profile: {e.Message}");
		}
	}

	/// <summary>Sets one field of one account's profile, as the owner, where the network lets Braggarr.</summary>
	public async Task<Outcome<AccountProfile>> ApplyAsync(Network network, string account, ProfileField field, string value, Actor by, CancellationToken ct)
	{
		if (by.Kind != ActorKind.Owner)
		{
			return new Outcome<AccountProfile>.Refused("Only the owner changes their profiles.");
		}
		if (!accounts.Connected(network).Contains(account))
		{
			return new Outcome<AccountProfile>.Refused($"No {network} account \"{account}\" is connected.");
		}
		if (!Profiles.CanApply(network, field))
		{
			return new Outcome<AccountProfile>.Refused($"{network} does not let Braggarr change the {Name(field)}: copy it and paste it on {network}.");
		}
		var text = value.Trim();
		if (!Profiles.Fits(network, field, text))
		{
			return new Outcome<AccountProfile>.Refused(field switch
			{
				ProfileField.DisplayName => $"A {network} display name is one line of at most {Profiles.NameLimit(network)} characters.",
				ProfileField.Bio => $"A {network} bio is at most {Profiles.BioLimit(network)} characters.",
				_ => "A link is a web address, starting with https://."
			});
		}
		var change = field switch
		{
			ProfileField.DisplayName => new ProfileChange(DisplayName: text),
			ProfileField.Bio => new ProfileChange(Bio: text),
			_ => new ProfileChange(Link: text)
		};
		try
		{
			await profiles.ApplyAsync(network, account, change, ct);
			return new Outcome<AccountProfile>.Done(await profiles.ReadAsync(network, account, ct));
		}
		catch (Exception e) when (e is PublishException or HttpRequestException)
		{
			return new Outcome<AccountProfile>.Refused(e.Message);
		}
	}

	public static string Name(ProfileField field) => field switch
	{
		ProfileField.DisplayName => "display name",
		ProfileField.Bio => "bio",
		ProfileField.Link => "website",
		ProfileField.Avatar => "profile picture",
		_ => "banner"
	};
}
