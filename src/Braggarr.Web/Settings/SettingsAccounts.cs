using Braggarr.Adapters.Social;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Web.Settings;

/// <summary>
/// The owner's accounts and the defaults that pick one: the project's, else its GitHub org's (from the watched project's
/// repository), else the global one, else the first connected. A default for an account that is no longer connected is
/// passed over.
/// </summary>
public sealed class SettingsAccounts(AccountStore accounts, SettingsStore settings) : IAccounts
{
	public IReadOnlyList<string> Connected(Network network) => accounts.On(network);

	public AccountChoice? Choose(Network network, string project)
	{
		var connected = accounts.On(network);
		if (connected.Count == 0)
		{
			return null;
		}
		var current = settings.Current;
		var slug = ProjectBrief.Slug(project);
		var org = OrgOf(current, project);
		var defaults = current.AccountDefaults.Where(d => d.Network == network.Slug() && connected.Contains(d.Account)).ToList();
		return defaults.FirstOrDefault(d => d.Project is { } p && slug.Length > 0 && ProjectBrief.Slug(p) == slug) is { } forProject
				? new AccountChoice(network, forProject.Account, $"{project}'s default")
			: defaults.FirstOrDefault(d => d.Project is null && d.Org is { } o && string.Equals(o, org, StringComparison.OrdinalIgnoreCase)) is { } forOrg
				? new AccountChoice(network, forOrg.Account, $"{forOrg.Org}'s default")
			: defaults.FirstOrDefault(d => d.IsGlobal) is { } global
				? new AccountChoice(network, global.Account, "the global default")
			: new AccountChoice(network, connected[0], connected.Count == 1 ? $"the only {network} account" : $"the first {network} account");
	}

	/// <summary>The GitHub org of a watched project's repository: <c>nullean</c> for <c>nullean/curb</c>.</summary>
	public static string? OrgOf(BraggarrSettings settings, string project) =>
		settings.Projects.FirstOrDefault(p => ProjectBrief.Slug(p.Name) == ProjectBrief.Slug(project)) is { } watched
			? watched.Repository.Split('/')[0]
			: null;

	/// <summary>
	/// Moves the connections Braggarr kept before accounts into accounts of their own, each its network's global default, so
	/// everything posts as it did. Called once at startup; does nothing the second time.
	/// </summary>
	public void MoveSingleConnections()
	{
		var moved = accounts.MoveSingleConnections();
		if (moved.Count > 0)
		{
			_ = settings.Update(s => s with
			{
				AccountDefaults =
				[
					.. s.AccountDefaults,
					.. moved.Where(m => !s.AccountDefaults.Any(d => d.IsGlobal && d.Network == m.Network.Slug())).Select(m => new AccountDefault(m.Network.Slug(), m.Name))
				]
			});
		}
	}
}
