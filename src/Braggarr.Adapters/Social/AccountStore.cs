using Braggarr.Adapters.Config;
using Braggarr.Domain;

namespace Braggarr.Adapters.Social;

/// <summary>One of the owner's accounts on a network, by the name Braggarr shows for it: a handle, a username, a channel.</summary>
public sealed record ConnectedAccount(Network Network, string Name);

/// <summary>
/// The owner's network accounts, any number per network, each with its credentials encrypted in the secret store under
/// <c>account:&lt;network&gt;:&lt;name&gt;</c>.
/// </summary>
public sealed class AccountStore(SecretStore secrets)
{
	private const string Prefix = "account:";

	public static string Key(Network network, string name) => $"{Prefix}{network.Slug()}:{name}";

	public IReadOnlyList<ConnectedAccount> All() =>
	[
		.. from key in secrets.Names()
		where key.StartsWith(Prefix, StringComparison.Ordinal)
		let rest = key[Prefix.Length..]
		let colon = rest.IndexOf(':', StringComparison.Ordinal)
		where colon > 0 && Networks.TryParse(rest[..colon], out _)
		let network = Networks.TryParse(rest[..colon], out var n) ? n : default
		orderby network, rest[(colon + 1)..]
		select new ConnectedAccount(network, rest[(colon + 1)..])
	];

	public IReadOnlyList<string> On(Network network) => [.. All().Where(a => a.Network == network).Select(a => a.Name)];

	public T? Get<T>(Network network, string name) where T : class => secrets.Get<T>(Key(network, name));

	/// <summary>Every account on a network with its credentials.</summary>
	public IReadOnlyList<(string Name, T Credentials)> Each<T>(Network network) where T : class =>
		[.. On(network).Select(name => (name, Get<T>(network, name))).Where(a => a.Item2 is not null).Select(a => (a.name, a.Item2!))];

	public void Set<T>(Network network, string name, T? credentials) where T : class => secrets.Set(Key(network, name), credentials);

	/// <summary>The name an account is shown and chosen by: what the owner knows it as on that network.</summary>
	public static string NameOf(object credentials) => credentials switch
	{
		BlueskyCredentials b => b.Handle,
		MastodonCredentials m => new Uri(m.Instance).Host,
		LinkedInCredentials l => l.Name,
		ThreadsCredentials t => t.Username,
		XCredentials x => x.Username,
		YouTubeCredentials y => y.Channel,
		Blog.BlogSite b => b.Repository,
		_ => throw new ArgumentException($"{credentials.GetType().Name} is not a network's credentials.", nameof(credentials))
	};

	/// <summary>
	/// Moves the one connection per network Braggarr kept before accounts (<c>connection:&lt;network&gt;</c>) to an account of its
	/// own, and returns what it moved, so the caller can make each one its network's default. Running it again moves nothing.
	/// </summary>
	public IReadOnlyList<ConnectedAccount> MoveSingleConnections()
	{
		var moved = new List<ConnectedAccount>();
		Move<BlueskyCredentials>(Network.Bluesky);
		Move<MastodonCredentials>(Network.Mastodon);
		Move<LinkedInCredentials>(Network.LinkedIn);
		Move<ThreadsCredentials>(Network.Threads);
		Move<XCredentials>(Network.X);
		Move<YouTubeCredentials>(Network.YouTube);
		return moved;

		void Move<T>(Network network) where T : class
		{
			var old = $"connection:{network.Slug()}";
			if (secrets.Get<T>(old) is not { } credentials)
			{
				return;
			}
			var name = NameOf(credentials);
			name = string.IsNullOrWhiteSpace(name) ? network.Slug() : name;
			Set(network, name, credentials);
			secrets.Set(old, null);
			moved.Add(new ConnectedAccount(network, name));
		}
	}
}
