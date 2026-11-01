using Braggarr.Adapters.Config;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Adapters.Social;

/// <summary>The publishers for the networks that have credentials in the secret store, rebuilt when the credentials change.</summary>
public sealed class Publishers : IPublishers
{
	public const string YouTubeClientSecret = "client:youtube";

	private readonly IHttpClientFactory _http;
	private readonly AccountStore _accounts;
	private readonly TimeProvider _time;
	private readonly GitHub.GitHubClient? _github;
	private Dictionary<(Network, string), IPublisher> _publishers = [];

	public Publishers(IHttpClientFactory http, SecretStore secrets, AccountStore accounts, TimeProvider time, GitHub.GitHubClient? github = null)
	{
		_http = http;
		_accounts = accounts;
		_time = time;
		_github = github;
		secrets.Changed += Rebuild;
		Rebuild();
	}

	public IPublisher? Find(Network network, string account) => _publishers.GetValueOrDefault((network, account));

	private void Rebuild()
	{
		var now = _time.GetUtcNow();
		var publishers = new Dictionary<(Network, string), IPublisher>();
		foreach (var (name, bluesky) in _accounts.Each<BlueskyCredentials>(Network.Bluesky))
		{
			publishers[(Network.Bluesky, name)] = new BlueskyPublisher(_http.CreateClient("bluesky"), bluesky, _time);
		}
		foreach (var (name, mastodon) in _accounts.Each<MastodonCredentials>(Network.Mastodon))
		{
			publishers[(Network.Mastodon, name)] = new MastodonPublisher(_http.CreateClient("mastodon"), mastodon, _time);
		}
		foreach (var (name, linkedin) in _accounts.Each<LinkedInCredentials>(Network.LinkedIn).Where(a => a.Credentials.ExpiresAt > now))
		{
			publishers[(Network.LinkedIn, name)] = new LinkedInPublisher(_http.CreateClient("linkedin"), linkedin, _time);
		}
		foreach (var (name, threads) in _accounts.Each<ThreadsCredentials>(Network.Threads).Where(a => a.Credentials.ExpiresAt > now))
		{
			publishers[(Network.Threads, name)] = new ThreadsPublisher(_http.CreateClient("threads"), threads, _time);
		}
		foreach (var (name, x) in _accounts.Each<XCredentials>(Network.X))
		{
			publishers[(Network.X, name)] = new XPublisher(_http.CreateClient("x"), x, _time);
		}
		foreach (var (name, youtube) in _accounts.Each<YouTubeCredentials>(Network.YouTube))
		{
			publishers[(Network.YouTube, name)] = new YouTubePublisher(_http.CreateClient("youtube"), youtube, _time);
		}
		// A blog commits through Braggarr's GitHub App, so it needs the app set up.
		foreach (var (name, site) in _github is null ? [] : _accounts.Each<Blog.BlogSite>(Network.Blog))
		{
			publishers[(Network.Blog, name)] = new Blog.BlogPublisher(_github!, site);
		}
		_publishers = publishers;
	}
}
