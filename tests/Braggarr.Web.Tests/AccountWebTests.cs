using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Braggarr.Adapters.Config;
using Braggarr.Adapters.Posts;
using Braggarr.Adapters.Social;
using Braggarr.Domain;
using Braggarr.Web.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Braggarr.Web.Tests;

/// <summary>The owner's accounts: kept per account, chosen by the defaults, set in Settings, changed on a proposal, named by agents.</summary>
public class AccountWebTests
{
	private static readonly Campaign Curb = new(
		"curb-1-0", "Curb 1.0", "curb", CampaignStatus.Approved, [Network.Bluesky, Network.Mastodon], ["https://github.com/nullean/curb"], "Brief.", "owner:mpdreamz",
		[new Post("01-launch", new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)), [new Variant(Network.Bluesky, "Out."), new Variant(Network.Mastodon, "Out.")])]
	);

	[Test]
	public void A_campaign_without_accounts_keeps_the_fingerprint_it_was_approved_with_and_accounts_change_it()
	{
		// Computed with the code from before accounts: an approved campaign must stay approved after the upgrade.
		Curb.Digest.Should().Be("b415a4fb53923dbb5042b3fdaebc5403516e3f731fb6732e55d63ab9d8330abf");
		(Curb with { Accounts = [new CampaignAccount(Network.Bluesky, "nullean")] }).Digest.Should().NotBe(Curb.Digest);
	}

	[Test]
	public void Accounts_read_back_from_campaign_md_by_network_name()
	{
		var named = Curb with { Accounts = [new CampaignAccount(Network.Bluesky, "nullean.bsky.social"), new CampaignAccount(Network.Mastodon, "hachyderm.io")] };

		var files = PostsFormat.WriteCampaign(named);

		files[PostsFormat.CampaignFile].Should().Contain("accounts:\n  bluesky: nullean.bsky.social\n  mastodon: hachyderm.io\n");
		PostsFormat.ReadCampaign(new CampaignFiles("curb-1-0", files), CampaignStatus.Approved).Accounts.Should().Equal(named.Accounts);
		PostsFormat.WriteCampaign(Curb)[PostsFormat.CampaignFile].Should().NotContain("accounts");
	}

	private static (AccountStore Accounts, SecretStore Secrets, SettingsStore Settings, SettingsAccounts Resolver) Store()
	{
		var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
		var secrets = new SecretStore(new EphemeralDataProtectionProvider(), fs, "/config/secrets.json");
		var accounts = new AccountStore(secrets);
		var settings = new SettingsStore(fs, "/config");
		return (accounts, secrets, settings, new SettingsAccounts(accounts, settings));
	}

	[Test]
	public void The_one_connection_per_network_from_before_becomes_an_account_and_its_default_once()
	{
		var (accounts, secrets, settings, resolver) = Store();
		secrets.Set("connection:bluesky", new BlueskyCredentials("mpdreamz.bsky.social", "app-password"));
		secrets.Set("connection:mastodon", new MastodonCredentials("https://hachyderm.io", "token"));

		resolver.MoveSingleConnections();
		resolver.MoveSingleConnections();

		accounts.All().Should().Equal(new ConnectedAccount(Network.Bluesky, "mpdreamz.bsky.social"), new ConnectedAccount(Network.Mastodon, "hachyderm.io"));
		accounts.Get<BlueskyCredentials>(Network.Bluesky, "mpdreamz.bsky.social")!.AppPassword.Should().Be("app-password");
		secrets.Get("connection:bluesky").Should().BeNull();
		settings.Current.AccountDefaults.Should().Equal(new AccountDefault("bluesky", "mpdreamz.bsky.social"), new AccountDefault("mastodon", "hachyderm.io"));
	}

	[Test]
	public void The_project_default_wins_then_the_orgs_then_the_global_and_a_disconnected_default_is_passed_over()
	{
		var (accounts, _, settings, resolver) = Store();
		foreach (var name in new[] { "me.bsky.social", "nullean.bsky.social", "curb.bsky.social" })
		{
			accounts.Set(Network.Bluesky, name, new BlueskyCredentials(name, "p"));
		}
		_ = settings.Update(s => s with
		{
			Projects = [new ProjectSettings("nullean/curb", "curb", ["bluesky"]), new ProjectSettings("nullean/mermaider", "mermaider", ["bluesky"]), new ProjectSettings("heed-build/heed", "heed", ["bluesky"])],
			// The most specific listed last, so the order in the file never decides.
			AccountDefaults =
			[
				new AccountDefault("bluesky", "curb.bsky.social", Project: "curb"),
				new AccountDefault("bluesky", "gone.bsky.social", Project: "mermaider"),
				new AccountDefault("bluesky", "nullean.bsky.social", Org: "nullean"),
				new AccountDefault("bluesky", "me.bsky.social")
			]
		});

		resolver.Choose(Network.Bluesky, "curb").Should().Be(new Braggarr.Services.AccountChoice(Network.Bluesky, "curb.bsky.social", "curb's default"));
		resolver.Choose(Network.Bluesky, "mermaider").Should().Be(new Braggarr.Services.AccountChoice(Network.Bluesky, "nullean.bsky.social", "nullean's default"));
		resolver.Choose(Network.Bluesky, "heed").Should().Be(new Braggarr.Services.AccountChoice(Network.Bluesky, "me.bsky.social", "the global default"));
		resolver.Choose(Network.Bluesky, "not-watched").Should().Be(new Braggarr.Services.AccountChoice(Network.Bluesky, "me.bsky.social", "the global default"));
		resolver.Choose(Network.Mastodon, "curb").Should().BeNull("nothing is connected there");
	}

	[Test]
	public void Each_account_has_its_own_publisher_and_an_unknown_one_has_none()
	{
		var (accounts, secrets, _, _) = Store();
		accounts.Set(Network.Bluesky, "me.bsky.social", new BlueskyCredentials("me.bsky.social", "p"));
		accounts.Set(Network.Bluesky, "nullean.bsky.social", new BlueskyCredentials("nullean.bsky.social", "p"));
		var publishers = new Publishers(new NoClients(), secrets, accounts, TimeProvider.System);

		var me = publishers.Find(Network.Bluesky, "me.bsky.social");
		var nullean = publishers.Find(Network.Bluesky, "nullean.bsky.social");

		me.Should().NotBeNull();
		nullean.Should().NotBeNull().And.NotBeSameAs(me);
		publishers.Find(Network.Bluesky, "someone.bsky.social").Should().BeNull();
		publishers.Find(Network.Mastodon, "me.bsky.social").Should().BeNull();
	}

	private sealed class NoClients : IHttpClientFactory
	{
		public HttpClient CreateClient(string name) => new();
	}

	[Test]
	public void Without_any_default_the_first_connected_account_posts()
	{
		var (accounts, _, _, resolver) = Store();
		accounts.Set(Network.X, "nullean", new XCredentials("k", "s", "t", "ts", "nullean"));

		resolver.Choose(Network.X, "curb").Should().Be(new Braggarr.Services.AccountChoice(Network.X, "nullean", "the only X account"));
	}

	private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields) =>
		new(fields.Select(f => KeyValuePair.Create(f.Key, f.Value)).Prepend(KeyValuePair.Create("__RequestVerificationToken", token)));

	[Test]
	public async Task The_owner_sets_who_posts_for_everything_an_org_and_a_project_and_disconnecting_forgets_its_defaults()
	{
		using var factory = new BraggarrFactory();
		var accounts = factory.Services.GetRequiredService<AccountStore>();
		accounts.Set(Network.Bluesky, "me.bsky.social", new BlueskyCredentials("me.bsky.social", "p"));
		accounts.Set(Network.Bluesky, "nullean.bsky.social", new BlueskyCredentials("nullean.bsky.social", "p"));
		_ = factory.Settings.Update(s => s with { Projects = [new ProjectSettings("nullean/curb", "curb", ["bluesky"])] });
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);

		var page = await browser.GetStringAsync("/Settings?section=networks");
		var token = await BraggarrFactory.FormTokenAsync(browser, "/Settings?section=networks");
		_ = await browser.PostAsync("/Settings?section=networks&handler=Defaults", Form(token,
			("default|global||bluesky", "me.bsky.social"), ("default|org|nullean|bluesky", "nullean.bsky.social"), ("default|project|curb|bluesky", "")));
		var defaults = factory.Settings.Current.AccountDefaults;
		var refused = await (await browser.PostAsync("/Settings?section=networks&handler=Defaults", Form(token, ("default|global||bluesky", "someone.bsky.social")))).Content.ReadAsStringAsync();
		_ = await browser.PostAsync("/Settings?section=networks&handler=Disconnect", Form(token, ("network", "bluesky"), ("name", "nullean.bsky.social")));

		page.Should().Contain("nullean&#x27;s projects").And.Contain("name=\"default|project|curb|bluesky\"");
		defaults.Should().Equal(new AccountDefault("bluesky", "me.bsky.social"), new AccountDefault("bluesky", "nullean.bsky.social", Org: "nullean"));
		refused.Should().Contain("is not a connected Bluesky account");
		accounts.On(Network.Bluesky).Should().Equal("me.bsky.social");
		factory.Settings.Current.AccountDefaults.Should().Equal(new AccountDefault("bluesky", "me.bsky.social"));
	}

	[Test]
	public async Task An_agent_names_the_account_and_the_owner_changes_it_on_the_proposal()
	{
		using var factory = new BraggarrFactory();
		var accounts = factory.Services.GetRequiredService<AccountStore>();
		accounts.Set(Network.Bluesky, "me.bsky.social", new BlueskyCredentials("me.bsky.social", "p"));
		accounts.Set(Network.Bluesky, "nullean.bsky.social", new BlueskyCredentials("nullean.bsky.social", "p"));
		var campaign = new
		{
			title = "Mermaider 2.0",
			project = "mermaider",
			networks = new[] { "bluesky" },
			links = new[] { "https://github.com/nullean/mermaider" },
			brief = "Announce 2.0.",
			accounts = new Dictionary<string, string> { ["bluesky"] = "nullean.bsky.social" },
			posts = new[] { new { at = "2030-11-03T10:00:00+01:00", variants = new Dictionary<string, string> { ["bluesky"] = "Mermaider 2.0 is out." } } }
		};

		var proposed = await factory.Agent().PostAsJsonAsync("/api/v1/campaigns", campaign);
		var unknown = await factory.Agent().PostAsJsonAsync("/api/v1/campaigns", campaign with { title = "Mermaider 2.1", accounts = new Dictionary<string, string> { ["bluesky"] = "who.bsky.social" } });
		var named = factory.Reviews.Open.Single().Campaign.AccountFor(Network.Bluesky);
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);
		var page = await browser.GetStringAsync("/Campaigns/Proposal?number=1");
		var token = await BraggarrFactory.FormTokenAsync(browser, "/Campaigns/Proposal?number=1");
		_ = await browser.PostAsync("/Campaigns/Proposal?handler=Accounts&number=1", Form(token, ("account[Bluesky]", "me.bsky.social")));
		var view = JsonDocument.Parse(await factory.Agent().GetStringAsync("/api/v1/campaigns/mermaider-2-0")).RootElement;

		proposed.StatusCode.Should().Be(HttpStatusCode.Created);
		unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await unknown.Content.ReadAsStringAsync()).Should().Contain("who.bsky.social");
		named.Should().Be("nullean.bsky.social");
		page.Should().Contain("<option value=\"nullean.bsky.social\" selected=\"selected\">");
		factory.Reviews.Open.Single().Campaign.AccountFor(Network.Bluesky).Should().Be("me.bsky.social");
		view.GetProperty("accounts").GetProperty("bluesky").GetString().Should().Be("me.bsky.social");
	}
}
