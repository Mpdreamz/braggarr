using AwesomeAssertions;
using CsCheck;
using Nullean.Entities.CsCheck;

namespace Braggarr.Domain.Tests;

/// <summary>What a project's brief must say before it is kept, and the file name it is kept under.</summary>
public class ProjectBriefTests
{
	private static readonly Gen<ProjectBrief> Briefs =
		from project in Gen.OneOfConst("curb", "Mermaider", "heed 2", "nullean.entities")
		from tldr in Gen.OneOfConst("A C# formatter that keeps diffs small.", "Typed constraints for .NET records, proved by generated tests.")
		from apart in Gen.OneOfConst("It never moves a line it does not have to.", "One declaration gives you validation and tests.")
		from audience in Gen.OneOfConst("", "C# teams who review diffs")
		from avoid in Gen.OneOfConst("Calling it a linter", "Speed numbers").Array[0, 2]
		from links in Gen.OneOfConst(new BriefLink("Repository", "https://github.com/nullean/curb"), new BriefLink("Docs", "https://curb.dev")).Array[0, 2]
		select new ProjectBrief(project, tldr, apart, audience) { Avoid = [.. avoid], Links = [.. links] };

	public static IEnumerable<Func<AxisCase>> Axes() =>
		AutoTest.For(ProjectBriefRules.Instance, Briefs)
			.CounterExample(ProjectBriefRules.NamesItsProject, b => b with { Project = " ./ " })
			.CounterExample(ProjectBriefRules.SumsItUp, b => b with { Tldr = new string('a', ProjectBrief.TldrLimit + 1) })
			.CounterExample(ProjectBriefRules.SaysWhatSetsItApart, b => b with { Apart = " " })
			.CounterExample(ProjectBriefRules.LinksAreLinks, b => b with { Links = [.. b.Links, new BriefLink("Docs", "javascript:alert(1)")] })
			.AsDataSource();

	[Test]
	[MethodDataSource(nameof(Axes))]
	public Task EveryDeclaredAxisHolds(AxisCase axis) => axis.RunAsync();

	[Test]
	public void A_tldr_is_one_line_and_a_link_needs_a_label()
	{
		var brief = new ProjectBrief("curb", "A formatter.", "Small diffs.", "");

		ProjectBriefRules.All.Validate(brief).Should().BeEmpty();
		ProjectBriefRules.All.Validate(brief with { Tldr = "A formatter.\nFor C#." }).Should().ContainSingle();
		ProjectBriefRules.All.Validate(brief with { Tldr = new string('a', ProjectBrief.TldrLimit) }).Should().BeEmpty();
		ProjectBriefRules.All.Validate(brief with { Links = [new BriefLink(" ", "https://curb.dev")] }).Should().ContainSingle();
	}

	[Test]
	public void A_slug_is_lowercase_letters_digits_and_single_dashes_so_a_brief_stays_in_projects() =>
		Gen.String.Sample(name =>
		{
			var slug = ProjectBrief.Slug(name);

			slug.Should().MatchRegex("^([a-z0-9]+(-[a-z0-9]+)*)?$");
			ProjectBrief.Slug(slug).Should().Be(slug);
		});

	[Test]
	public void Names_people_write_become_the_slug_they_expect()
	{
		ProjectBrief.Slug("Nullean.Entities").Should().Be("nullean-entities");
		ProjectBrief.Slug("../../etc/passwd").Should().Be("etc-passwd");
		ProjectBrief.Slug(" heed 2 ").Should().Be("heed-2");
		ProjectBrief.Slug("ünïcode").Should().Be("n-code");
	}
}
