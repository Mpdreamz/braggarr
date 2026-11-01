using System.Net.Http.Json;
using Braggarr.Domain;
using Braggarr.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Braggarr.Web.Tests;

/// <summary>
/// Not a test of behaviour: with BRAGGARR_SCREENS set to a folder, signs in as the owner over sample campaigns and saves each
/// page's HTML there, with the stylesheet and script beside it, for a browser to screenshot. Does nothing otherwise.
/// </summary>
public class Screens
{
	private static readonly string[] Networks = ["bluesky", "mastodon", "linkedin", "hackernews"];

	private static readonly string[] Pages =
	[
		"/", "/Calendar", "/Activity", "/Projects", "/System", "/Settings", "/Settings?section=claude", "/Campaigns/New",
		"/Campaigns/Proposal?number=1", "/Campaigns/Detail?slug=curb-1-0", "/Campaigns", "/Projects/mermaider", "/Calendar?rebalance=true", "/Settings/Profiles"
	];

	[Test]
	public async Task Save_every_page_for_screenshots()
	{
		if (Environment.GetEnvironmentVariable("BRAGGARR_SCREENS") is not { Length: > 0 } folder)
		{
			return;
		}
		_ = Directory.CreateDirectory(folder);
		using var factory = new BraggarrFactory();
		var browser = await SeededAsync(factory);

		foreach (var page in Pages)
		{
			var response = await browser.GetAsync(page);
			var html = await response.Content.ReadAsStringAsync();
			html = html.Replace("href=\"/css/site.css", "href=\"site.css", StringComparison.Ordinal).Replace("src=\"/js/site.js", "src=\"site.js", StringComparison.Ordinal);
			var name = page == "/" ? "home" : page.Trim('/').Replace('/', '-').Replace('?', '-').Replace('=', '-').Replace('&', '-');
			await File.WriteAllTextAsync(Path.Combine(folder, $"{name}.html"), $"<!-- {(int)response.StatusCode} -->\n{html}");
		}
		var root = FindWebRoot();
		File.Copy(Path.Combine(root, "css", "site.css"), Path.Combine(folder, "site.css"), overwrite: true);
		File.Copy(Path.Combine(root, "js", "site.js"), Path.Combine(folder, "site.js"), overwrite: true);
	}

	/// <summary>The sample campaigns, deliveries and accounts, a proposal from an agent, and a browser signed in as the owner.</summary>
	internal static async Task<HttpClient> SeededAsync(BraggarrFactory factory)
	{
		Seed(factory);
		await factory.Services.GetRequiredService<SchedulingService>().StartAsync(CancellationToken.None);
		_ = await factory.Agent().PostAsJsonAsync("/api/v1/campaigns", new
		{
			title = "Mermaider 2.0",
			project = "mermaider",
			networks = Networks,
			brief = "Diagrams to SVG without a browser.",
			start = factory.Time.GetUtcNow().AddDays(3).ToString("O"),
			points = new object[]
			{
				new { text = "Mermaid diagrams to SVG without a browser", wanted = new[] { new { kind = "video", description = "A terminal run turning a flowchart into an SVG" } } },
				new { text = "Renders a 500-node graph in 40 ms", wanted = new[] { new { kind = "image", description = "The benchmark table from the README" } } },
				new { text = "Works in CI with no Chromium to install" }
			}
		});
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);
		return browser;
	}

	private static string FindWebRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Braggarr.Web", "wwwroot")))
		{
			dir = dir.Parent;
		}
		return Path.Combine(dir!.FullName, "src", "Braggarr.Web", "wwwroot");
	}

	private static void Seed(BraggarrFactory factory)
	{
		var now = factory.Time.GetUtcNow();
		var accounts = factory.Services.GetRequiredService<Braggarr.Adapters.Social.AccountStore>();
		accounts.Set(Network.Bluesky, "mpdreamz.bsky.social", new Braggarr.Adapters.Social.BlueskyCredentials("mpdreamz.bsky.social", "p"));
		accounts.Set(Network.Mastodon, "hachyderm.io", new Braggarr.Adapters.Social.MastodonCredentials("https://hachyderm.io", "t"));
		factory.Profiles.Shown[(Network.Bluesky, "mpdreamz.bsky.social")] = new(Network.Bluesky, "mpdreamz.bsky.social", "Martijn", "Dev.", null, true, false);
		factory.Profiles.Shown[(Network.Mastodon, "hachyderm.io")] =
			new(Network.Mastodon, "hachyderm.io", "Martijn Laarman", "I make heed, a Mac app for running coding agents, and small .NET tools.", "https://heed.build", true, true);
		Campaign C(string slug, string title, string project, int hours, params Network[] networks) =>
			new Campaign(slug, title, project, CampaignStatus.Proposed, [.. networks], [$"https://github.com/nullean/{project}"], $"Announce {title}.", "agent:release-bot",
			[
				new Post("01-launch", now.AddHours(hours), [.. networks.Select(n => new Variant(n, n.IsManual() ? $"Show HN: {title}\nhttps://github.com/nullean/{project}" : $"{title} is out: a small, fast tool that does one thing well. https://github.com/nullean/{project}"))]),
				new Post("02-follow-up", now.AddHours(hours + 72), [.. networks.Select(n => new Variant(n, n.IsManual() ? $"{title}, a week in" : $"A week with {title}: what people built with it."))]) { Angle = "What people built with it" }
			]).Approve(new(Actor.Owner("mpdreamz"), factory.Time.GetUtcNow()));
		var curb = C("curb-1-0", "Curb 1.0", "curb", -30, Network.Bluesky, Network.Mastodon, Network.LinkedIn, Network.X, Network.HackerNews);
		var heed = C("heed-0-21", "Heed 0.21", "heed", 20, Network.Bluesky, Network.Mastodon, Network.Threads);
		var hunch = C("hunch-0-3", "Hunch 0.3", "hunch", 50, Network.Bluesky, Network.LinkedIn).Hold(Actor.Owner("mpdreamz"));
		factory.Repository.Campaigns.AddRange([curb, heed, hunch]);
		var at = now.AddHours(-30);
		foreach (var (n, d) in new (Network, Delivery)[]
		{
			(Network.Bluesky, Delivery.New(new("curb-1-0", "01-launch", Network.Bluesky)).Begin(at).Succeed(new Receipt("1", "https://bsky.app/profile/me/post/1"))),
			(Network.Mastodon, Delivery.New(new("curb-1-0", "01-launch", Network.Mastodon)).Begin(at).Succeed(new Receipt("2", "https://hachyderm.io/@me/2"))),
			(Network.LinkedIn, Delivery.New(new("curb-1-0", "01-launch", Network.LinkedIn)).Begin(at).Recover()),
			(Network.X, Delivery.New(new("curb-1-0", "01-launch", Network.X)).Begin(at).Fail("403: duplicate content")),
			(Network.HackerNews, Delivery.New(new("curb-1-0", "01-launch", Network.HackerNews)).OpenManual(12)),
		})
		{
			factory.Repository.Deliveries[d.Key] = d;
		}
	}
}
