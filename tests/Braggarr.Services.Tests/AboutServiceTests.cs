using AwesomeAssertions;
using Braggarr.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

/// <summary>The blog's about page, from what Braggarr knows, proposed to the blog as one pull request.</summary>
public class AboutServiceTests
{
	private static readonly ProjectBrief Curb = new("curb", "A C# formatter that honours all of your .editorconfig.", "Small diffs.", "C# teams")
	{
		Links = [new BriefLink("Website", "https://curb.nullean.net"), new BriefLink("Benchmarks", "https://curb.nullean.net/benchmarks/"), new BriefLink("Repository", "https://github.com/nullean/curb")]
	};

	private static readonly ProjectBrief Heed = new("heed", "A Mac app for running coding agents.", "Green PRs.", "Developers") { Links = [new BriefLink("Repository", "https://github.com/heed-build/heed")] };

	private sealed class World
	{
		public Watched Projects { get; } = new();
		public FakeRepository Repository { get; } = new() { Voice = "Plain." };
		public Facts Facts { get; } = new();
		public Pictures Pictures { get; } = new();
		public Sites Sites { get; } = new();
		public FakeAccounts Accounts { get; } = new();
		public FakeProfiles Profiles { get; } = new();
		public FakeDrafter Drafter { get; } = new() { Intro = "I build tools I need." };
		public bool Claude { get; set; } = true;
		public FakeNotifier Notifier { get; } = new();

		public World()
		{
			Repository.Briefs.AddRange([Curb, Heed]);
			Accounts.Accounts[Network.Blog] = ["owner/blog"];
			Projects.Groups.AddRange([new AboutGroup("For the Mac") { Description = "Apps for Apple silicon." }, new AboutGroup(".NET tools")]);
			Projects.List.AddRange(
			[
				Project("nullean/curb", "curb", ".NET tools"),
				Project("heed-build/heed", "heed", "For the Mac"),
				Project("owner/secret", "secret", "For the Mac"),
				Project("owner/ungrouped", "ungrouped", null)
			]);
			Facts.Known["nullean/curb"] = new ProjectFacts("nullean/curb", ProjectVisibility.Public)
			{
				Stars = 37, Language = "C#", Homepage = "https://curb.nullean.net/",
				Release = new AboutRelease("0.10.1", new DateTimeOffset(2026, 11, 1, 10, 34, 25, TimeSpan.Zero), "https://github.com/nullean/curb/releases/tag/0.10.1"),
				IconUrl = "https://curb.nullean.net/images/png/curb-mark-180.png", ImageUrl = "https://curb.nullean.net/_static/curb-lockup.png"
			};
			Facts.Known["heed-build/heed"] = new ProjectFacts("heed-build/heed", ProjectVisibility.Public) { Homepage = "https://heed.build", ImageUrl = "https://heed.build/social.png" };
			Facts.Known["owner/secret"] = new ProjectFacts("owner/secret", ProjectVisibility.Private) { IconUrl = "https://secret.example.com/icon.png" };
			Pictures.Served["https://curb.nullean.net/images/png/curb-mark-180.png"] = new Picture(".png", [1, 2, 3]);
			Pictures.Served["https://curb.nullean.net/_static/curb-lockup.png"] = new Picture(".png", [4, 5]);
			Pictures.Served["https://secret.example.com/icon.png"] = new Picture(".png", [6]);
		}

		public AboutService Service()
		{
			var time = new ManualTimeProvider(SchedulerWorld.Start);
			var profiles = new ProfileService(Profiles, Accounts, Repository, new FakeDrafters(null), new FakeUsage(), time, NullLogger<ProfileService>.Instance);
			return new AboutService(Projects, Repository, Facts, Pictures, Sites, Accounts, profiles, new FakeDrafters(Claude ? Drafter : null), new FakeUsage(), Notifier, time, NullLogger<AboutService>.Instance);
		}

		private static WatchedProject Project(string repository, string name, string? group) => new(repository, name, [Network.Bluesky], null, Group: group);
	}

	[Test]
	public async Task The_page_shows_the_groups_in_order_with_their_public_projects_and_goes_to_the_blog()
	{
		var world = new World();

		var outcome = await world.Service().ProposeAsync(redraftIntro: false, CancellationToken.None);

		outcome.Should().Be(new Outcome<AboutProposal?>.Done(new AboutProposal(7, "https://github.com/owner/blog/pull/7")));
		var (site, page, media) = world.Sites.Proposed.Should().ContainSingle().Subject;
		site.Should().Be("owner/blog");
		page.Sections.Select(s => (s.Group.Title, string.Join(",", s.Projects.Select(p => p.Name)))).Should().Equal(("For the Mac", "heed"), (".NET tools", "curb"));
		page.Sections[0].Group.Description.Should().Be("Apps for Apple silicon.");
		var curb = page.Sections[1].Projects[0];
		curb.Should().Be(new AboutProject("curb", "nullean/curb", ProjectVisibility.Public)
		{
			Tldr = Curb.Tldr,
			Url = "https://curb.nullean.net",
			Links = [new BriefLink("Benchmarks", "https://curb.nullean.net/benchmarks/")],
			Release = world.Facts.Known["nullean/curb"].Release,
			Stars = 37,
			Language = "C#",
			Icon = "media/curb-icon.png",
			Image = "media/curb-card.png"
		});
		media.Keys.Should().BeEquivalentTo("media/curb-icon.png", "media/curb-card.png");
		media["media/curb-icon.png"].Should().Equal(1, 2, 3);
		world.Notifier.Sent.Should().ContainSingle(n => n.Url == "https://github.com/owner/blog/pull/7");
	}

	[Test]
	public async Task A_project_GitHub_does_not_call_public_is_left_off_and_nothing_of_it_is_fetched()
	{
		var world = new World();
		world.Facts.Known["heed-build/heed"] = world.Facts.Known["heed-build/heed"] with { Visibility = ProjectVisibility.Unknown };

		_ = await world.Service().ProposeAsync(false, CancellationToken.None);

		var page = world.Sites.Proposed.Single().Page;
		page.Projects.Select(p => p.Repository).Should().Equal("nullean/curb");
		world.Pictures.Asked.Should().NotContain(u => u.Contains("secret", StringComparison.Ordinal) || u.Contains("heed", StringComparison.Ordinal));
		world.Facts.Asked.Should().NotContain("owner/ungrouped", "a project in no group is not on the page, so GitHub is not asked about it");
	}

	[Test]
	public async Task The_blogs_intro_stays_and_one_is_drafted_only_when_there_is_none_or_when_asked()
	{
		var world = new World();
		world.Sites.Intro = "My own words.";

		_ = await world.Service().ProposeAsync(false, CancellationToken.None);
		_ = await world.Service().ProposeAsync(true, CancellationToken.None);
		world.Sites.Intro = null;
		_ = await world.Service().ProposeAsync(false, CancellationToken.None);

		world.Sites.Proposed.Select(p => p.Page.Intro).Should().Equal("My own words.", "I build tools I need.", "I build tools I need.");
		world.Drafter.IntrosAsked.Should().HaveCount(2);
		world.Drafter.IntrosAsked[0].Projects.Select(b => b.Project).Should().BeEquivalentTo("curb", "heed");
		world.Drafter.IntrosAsked[0].Voice.Should().Be("Plain.");
	}

	[Test]
	public async Task Without_Claude_a_blog_without_an_intro_gets_none_and_asking_for_one_is_refused_nicely()
	{
		var world = new World { Claude = false };

		_ = await world.Service().ProposeAsync(false, CancellationToken.None);
		world.Drafter.Intro = null;
		world.Claude = true;
		var refused = await world.Service().ProposeAsync(true, CancellationToken.None);

		world.Sites.Proposed.Single().Page.Intro.Should().BeNull();
		refused.Should().BeOfType<Outcome<AboutProposal?>.Refused>().Which.Reasons.Single().Should().StartWith("Claude did not write the intro");
	}

	[Test]
	public async Task The_owners_profiles_with_an_https_address_are_linked()
	{
		var world = new World();
		world.Profiles.Shown[(Network.Bluesky, "me")] = new AccountProfile(Network.Bluesky, "me", "Martijn", null, null, true, false) { Url = "https://bsky.app/profile/me" };
		world.Profiles.Shown[(Network.Mastodon, "me")] = new AccountProfile(Network.Mastodon, "me", null, null, null, true, false) { Url = "http://mastodon.example/@me" };

		_ = await world.Service().ProposeAsync(false, CancellationToken.None);

		world.Sites.Proposed.Single().Page.Profiles.Should().Equal(new AboutProfile(Network.Bluesky, "https://bsky.app/profile/me") { Name = "Martijn" });
	}

	[Test]
	public async Task Without_a_blog_nothing_is_proposed_and_a_blog_that_shows_this_already_gets_no_pull_request()
	{
		var world = new World();
		world.Sites.Unchanged = true;
		var unchanged = await world.Service().ProposeAsync(false, CancellationToken.None);
		world.Accounts.Accounts[Network.Blog] = [];
		var noBlog = await world.Service().ProposeAsync(false, CancellationToken.None);

		unchanged.Should().Be(new Outcome<AboutProposal?>.Done(null));
		world.Notifier.Sent.Should().BeEmpty();
		noBlog.Should().BeOfType<Outcome<AboutProposal?>.Refused>().Which.Reasons.Single().Should().Contain("Connect a blog");
		world.Sites.Proposed.Should().ContainSingle();
	}

	private sealed class Watched : IWatchedProjects
	{
		public List<WatchedProject> List { get; } = [];
		public List<AboutGroup> Groups { get; } = [];
		public IReadOnlyList<WatchedProject> All => List;
		IReadOnlyList<AboutGroup> IWatchedProjects.Groups => Groups;
		public TimeZoneInfo Zone => TimeZoneInfo.Utc;
		public void MarkSeen(string repository, string tag) { }
		public void RecordSkipped(string repository, string tag, string reason) { }
	}

	private sealed class Facts : IProjectFacts
	{
		public Dictionary<string, ProjectFacts> Known { get; } = [];
		public List<string> Asked { get; } = [];

		public Task<ProjectFacts> ReadAsync(string repository, CancellationToken ct)
		{
			Asked.Add(repository);
			return Task.FromResult(Known.GetValueOrDefault(repository) ?? new ProjectFacts(repository, ProjectVisibility.Unknown));
		}
	}

	private sealed class Pictures : IPictures
	{
		public Dictionary<string, Picture> Served { get; } = [];
		public List<string> Asked { get; } = [];

		public Task<Picture?> FetchAsync(string url, CancellationToken ct)
		{
			Asked.Add(url);
			return Task.FromResult(Served.GetValueOrDefault(url));
		}
	}

	private sealed class Sites : IAboutSites
	{
		public string? Intro { get; set; }
		public bool Unchanged { get; set; }
		public List<(string Site, AboutPage Page, IReadOnlyDictionary<string, byte[]> Media)> Proposed { get; } = [];

		public Task<string?> IntroAsync(string site, CancellationToken ct) => Task.FromResult(Intro);

		public Task<AboutProposal?> ProposeAsync(string site, AboutPage page, IReadOnlyDictionary<string, byte[]> media, CancellationToken ct)
		{
			Proposed.Add((site, page, media));
			return Task.FromResult(Unchanged ? null : new AboutProposal(7, "https://github.com/owner/blog/pull/7"));
		}
	}
}
