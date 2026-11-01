using AwesomeAssertions;
using CsCheck;
using Nullean.Entities.CsCheck;

namespace Braggarr.Domain.Tests;

/// <summary>What Braggarr puts on a blog's about page: the owner's groups, in order, with only their public projects.</summary>
public class AboutPageTests
{
	private static readonly AboutProject[] Watched =
	[
		new("curb", "nullean/curb", ProjectVisibility.Public) { Tldr = "A formatter.", Stars = 37, Language = "C#", Icon = "media/curb-icon.png", Image = "media/curb-card.jpg" },
		new("heed", "heed-build/heed", ProjectVisibility.Public) { Url = "https://heed.build", Links = [new BriefLink("Getting started", "https://heed.build/getting-started/")] },
		new("hunch", "heed-build/hunch", ProjectVisibility.Public) { Release = new AboutRelease("0.2.1", new DateTimeOffset(2026, 10, 28, 15, 40, 55, TimeSpan.Zero), "https://github.com/heed-build/hunch/releases/tag/0.2.1") },
		new("secret", "owner/secret", ProjectVisibility.Private),
		new("gone", "owner/gone", ProjectVisibility.Unknown),
		new("curb again", "Nullean/Curb", ProjectVisibility.Public)
	];

	private static readonly AboutGroup[] Groups = [new("For the Mac") { Description = "Apps for Apple silicon." }, new(".NET tools"), new("Empty"), new("for the mac")];

	/// <summary>Any groups, and any watched projects each in a group or none, laid out the way the page is.</summary>
	private static readonly Gen<AboutPage> Pages =
		from groups in Gen.Shuffle(Groups, 0, Groups.Length)
		from picks in Gen.Int[0, Groups.Length].Array[Watched.Length]
		from order in Gen.Shuffle(Watched.Select((p, i) => (p, i)).ToArray())
		from intro in Gen.OneOfConst<string?>(null, "I build tools I need.")
		select AboutPage.Lay(groups, [.. order.Select(o => (o.p, picks[o.i] < Groups.Length ? Groups[picks[o.i]].Title : null))]) with
		{
			Intro = intro,
			Profiles = [new AboutProfile(Network.Bluesky, "https://bsky.app/profile/example.com")]
		};

	private static readonly AboutProject Extra = new("extra", "owner/extra", ProjectVisibility.Public);

	public static IEnumerable<Func<AxisCase>> Axes() =>
		AutoTest.For(AboutPageRules.Instance, Pages)
			.CounterExample(AboutPageRules.OnlyPublicProjects, p => p with { Sections = [.. p.Sections, new AboutSection(new AboutGroup("Hidden"), [Watched[3]])] })
			.CounterExample(AboutPageRules.EachProjectOnce, p => p with { Sections = [.. p.Sections, new AboutSection(new AboutGroup("Twice"), [Extra, Extra])] })
			.CounterExample(AboutPageRules.GroupsAreDistinct, p => p with { Sections = [.. p.Sections, new AboutSection(new AboutGroup(" "), [Extra])] })
			.CounterExample(AboutPageRules.NoEmptyGroups, p => p with { Sections = [.. p.Sections, new AboutSection(new AboutGroup("Nothing yet"), [])] })
			.CounterExample(AboutPageRules.PicturesAreSavedBesideThePage, p => p with { Sections = [.. p.Sections, new AboutSection(new AboutGroup("Pictured"), [Extra with { Icon = "../secret.png" }])] })
			.CounterExample(AboutPageRules.LinksAreHttps, p => p with { Profiles = [.. p.Profiles, new AboutProfile(Network.Mastodon, "http://hachyderm.io/@me")] })
			.AsDataSource();

	[Test]
	[MethodDataSource(nameof(Axes))]
	public Task EveryDeclaredAxisHolds(AxisCase axis) => axis.RunAsync();

	[Test]
	public void The_groups_keep_the_owners_order_and_each_its_projects_in_the_order_they_are_watched()
	{
		var page = AboutPage.Lay(
			[new AboutGroup("For the Mac"), new AboutGroup(".NET tools")],
			[(Watched[0], ".NET tools"), (Watched[1], "For the Mac"), (Watched[2], "for the mac ")]);

		page.Sections.Select(s => (s.Group.Title, Projects: string.Join(",", s.Projects.Select(p => p.Name))))
			.Should().Equal(("For the Mac", "heed,hunch"), (".NET tools", "curb"));
	}

	[Test]
	public void A_project_that_is_not_public_in_no_group_or_twice_is_left_off_and_so_is_an_empty_group()
	{
		var page = AboutPage.Lay(
			[new AboutGroup("Tools"), new AboutGroup("Empty"), new AboutGroup("tools")],
			[(Watched[0], "Tools"), (Watched[1], null), (Watched[3], "Tools"), (Watched[4], "Tools"), (Watched[5], "Tools"), (Watched[2], "Gone")]);

		page.Sections.Should().ContainSingle().Which.Projects.Should().Equal(Watched[0]);
	}

	[Test]
	public void A_picture_is_one_plain_file_in_media()
	{
		AboutPage.IsMedia("media/curb-icon.png").Should().BeTrue();
		AboutPage.IsMedia("media/.hidden.png").Should().BeFalse();
		AboutPage.IsMedia("media/a/b.png").Should().BeFalse();
		AboutPage.IsMedia("media/").Should().BeFalse();
		AboutPage.IsMedia("icon.png").Should().BeFalse();
	}
}
