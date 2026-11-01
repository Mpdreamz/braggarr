using AwesomeAssertions;
using CsCheck;

namespace Braggarr.Domain.Tests;

/// <summary>What a verdict covers, when it blocks, when it lets the judge approve, and when it is out of date.</summary>
public class JudgingTests
{
	private static readonly Gen<Criterion> Criteria =
		Gen.Select(Gen.OneOfConst("honest", "hashtags", "tone", "links"), Gen.Bool, Gen.Bool, Gens.CampaignNetworks)
			.Select((name, blocks, everywhere, networks) => new Criterion(name, $"Judge {name}.", blocks) { Networks = everywhere ? null : networks });

	private static readonly Gen<ValueList<Criterion>> CriteriaSets =
		Criteria.List[0, 4].Select(list => list.DistinctBy(c => c.Name).ToValueList());

	[Test]
	public void A_complete_verdict_covers_every_criterion_on_every_variant_it_applies_to_and_nothing_else() =>
		Gen.Select(Gens.Campaigns, CriteriaSets).Sample((campaign, criteria) =>
		{
			var expected = Domain.Criteria.Expected(campaign, criteria);

			var variants = campaign.Posts.Sum(p => p.Variants.Count);
			// Said from the criterion's own networks, not through AppliesTo: the test must not trust what it tests.
			static bool About(Criterion c, Network n) => c.Networks is null || c.Networks.Contains(n);
			expected.Should().HaveCount(criteria.Sum(c => campaign.Posts.Sum(p => p.Variants.Count(v => About(c, v.Network)))));
			expected.Should().OnlyHaveUniqueItems();
			expected.All(e => About(e.Criterion, e.Network) && campaign.Posts.Single(p => p.Id == e.Post).For(e.Network) != null).Should().BeTrue();
			if (criteria.Any(c => c.Networks is null))
			{
				expected.Count.Should().BeGreaterThanOrEqualTo(variants);
			}
		});

	[Test]
	public void The_criteria_fingerprint_changes_with_any_word_of_them_and_not_with_their_order() =>
		Gen.Select(CriteriaSets, Gen.Int[0, 3]).Sample((criteria, which) =>
		{
			var digest = Domain.Criteria.Digest(criteria);

			Domain.Criteria.Digest(criteria.Reverse()).Should().Be(digest);
			if (criteria.Count > 0)
			{
				var i = which % criteria.Count;
				var c = criteria[i];
				foreach (var changed in new[] { c with { Prompt = c.Prompt + "!" }, c with { Blocks = !c.Blocks }, c with { Networks = c.Networks is null ? [Network.X] : null } })
				{
					Domain.Criteria.Digest(criteria.Select((x, j) => j == i ? changed : x)).Should().NotBe(digest);
				}
			}
		});

	private static Finding F(bool passed, bool blocks) => new("c", "01", Network.Bluesky, passed, blocks, "");

	[Test]
	public void Only_a_failed_blocking_criterion_blocks_and_only_no_failure_at_all_lets_the_judge_approve()
	{
		static Judgement Of(params Finding[] findings) => new("d", "c", DateTimeOffset.UnixEpoch, "m", [.. findings]);

		Of(F(true, true), F(true, false)).Should().Match<Judgement>(j => !j.Blocked && j.AllPassed);
		Of(F(true, true), F(false, false)).Should().Match<Judgement>(j => !j.Blocked && !j.AllPassed);
		Of(F(false, true), F(true, false)).Should().Match<Judgement>(j => j.Blocked && !j.AllPassed);
		Of().Should().Match<Judgement>(j => !j.Blocked && j.AllPassed);
	}

	[Test]
	public void A_verdict_is_current_only_for_the_version_and_criteria_it_judged_and_is_not_part_of_what_goes_out() =>
		Gens.Campaigns.Sample(campaign =>
		{
			var judgement = new Judgement(campaign.Digest, "criteria", DateTimeOffset.UnixEpoch, "m", []);
			var judged = campaign with { Judgement = judgement };
			var edited = judged with { Links = [.. campaign.Links, "https://example.com/more"] };

			judged.Digest.Should().Be(campaign.Digest);
			judged.JudgedAsIs("criteria").Should().BeTrue();
			judged.JudgedAsIs("other criteria").Should().BeFalse();
			edited.JudgedAsIs("criteria").Should().BeFalse();
			campaign.JudgedAsIs("criteria").Should().BeFalse();
		});
}
