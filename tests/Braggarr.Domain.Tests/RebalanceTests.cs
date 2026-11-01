using AwesomeAssertions;
using CsCheck;

namespace Braggarr.Domain.Tests;

/// <summary>What a rebalance promises, over any campaigns and any options the owner may choose.</summary>
public class RebalanceTests
{
	private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

	private static readonly Gen<RebalanceOptions> Options =
		from rest in Gen.Enum<DayOfWeek>().Array[0, 3]
		from perDay in Gen.Int[1, 3]
		from start in Gen.Int[7, 12]
		from hours in Gen.Int[2, 9]
		select new RebalanceOptions([.. rest.Distinct()], perDay, new TimeOnly(start, 0), new TimeOnly(start + hours, 0));

	private static readonly Gen<IReadOnlyList<Rebalanceable>> Campaigns =
		from campaigns in Gen.OneOf(Gens.Campaign("curb-1-0"), Gens.Campaign("heed-0-20"), Gens.Campaign("mermaider-svg")).Array[1, 3]
		from fixedFirst in Gen.Bool.Array[3]
		select (IReadOnlyList<Rebalanceable>)
		[
			.. campaigns.DistinctBy(c => c.Slug).Select((c, i) => new Rebalanceable(c, fixedFirst[i] ? new HashSet<string> { c.Posts[0].Id } : []))
		];

	private static (Campaign Campaign, Post Post, bool Fixed)[] After(IReadOnlyList<Rebalanceable> campaigns, IReadOnlyList<PostMove> moves) =>
	[
		.. from c in campaigns
		let moved = Rebalancer.Apply(c.Campaign, moves)
		from p in moved.Posts
		select (moved, p, c.Fixed.Contains(p.Id))
	];

	private static DateTime Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Amsterdam).DateTime;

	[Test]
	public void Every_post_that_can_still_move_ends_on_an_allowed_day_within_the_hours_and_none_moves_earlier() =>
		Gen.Select(Campaigns, Options).Sample((campaigns, options) =>
		{
			var now = Gens.Start.AddDays(-1);
			var moves = Rebalancer.Plan(campaigns, options, now, Amsterdam);

			foreach (var move in moves)
			{
				var local = Local(move.To);
				options.RestDays.Should().NotContain(local.DayOfWeek);
				TimeOnly.FromDateTime(local).Should().BeOnOrAfter(options.From).And.BeOnOrBefore(options.To);
				move.To.Should().BeAfter(move.From);
				move.To.Should().BeOnOrAfter(now);
			}
			moves.Should().OnlyContain(m => !campaigns.Single(c => c.Campaign.Slug == m.Campaign).Fixed.Contains(m.Post), "what went out stays");

			// And every post that can still move, moved or kept, ends where the rules allow.
			foreach (var (_, post, _) in After(campaigns, moves).Where(a => !a.Fixed))
			{
				var local = Local(post.At);
				options.RestDays.Should().NotContain(local.DayOfWeek, post.Id);
				TimeOnly.FromDateTime(local).Should().BeOnOrAfter(options.From).And.BeOnOrBefore(options.To);
				post.At.Should().BeOnOrAfter(now);
			}
		});

	[Test]
	public void No_network_gets_more_posts_a_day_than_allowed_from_what_moved() =>
		Gen.Select(Campaigns, Options).Sample((campaigns, options) =>
		{
			var moves = Rebalancer.Plan(campaigns, options, Gens.Start.AddDays(-1), Amsterdam);
			var after = After(campaigns, moves);
			var moved = moves.Select(m => (m.Campaign, m.Post)).ToHashSet();

			// Posts that went out may already share a day; what a rebalance answers for is where it put the posts it moved.
			foreach (var (campaign, post, _) in after.Where(a => moved.Contains((a.Campaign.Slug, a.Post.Id))))
			{
				var day = DateOnly.FromDateTime(Local(post.At));
				foreach (var network in post.NetworksIn(campaign))
				{
					after.Count(a => DateOnly.FromDateTime(Local(a.Post.At)) == day && a.Post.NetworksIn(a.Campaign).Contains(network))
						.Should().BeLessThanOrEqualTo(options.PerDay, $"{network} on {day}");
				}
			}
		});

	[Test]
	public void The_posts_of_a_campaign_that_can_still_move_keep_their_order_and_spacing() =>
		Gen.Select(Campaigns, Options).Sample((campaigns, options) =>
		{
			var moves = Rebalancer.Plan(campaigns, options, Gens.Start.AddDays(-1), Amsterdam);

			foreach (var c in campaigns)
			{
				var before = c.Campaign.Posts.Where(p => !c.Fixed.Contains(p.Id)).OrderBy(p => p.At).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
				var moved = Rebalancer.Apply(c.Campaign, moves);
				var times = before.Select(p => moved.Posts.Single(m => m.Id == p.Id).At).ToList();
				for (var i = 1; i < times.Count; i++)
				{
					if (moves.Any(m => m.Campaign == c.Campaign.Slug && (m.Post == before[i].Id || m.Post == before[i - 1].Id)))
					{
						(times[i] - times[i - 1]).Should().BeGreaterThanOrEqualTo(
							before[i].At - before[i - 1].At < TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : before[i].At - before[i - 1].At,
							"a moved post keeps its campaign's spacing");
					}
					times[i].Should().BeOnOrAfter(times[i - 1]);
				}
			}
		});

	[Test]
	public void Rebalancing_what_was_just_rebalanced_moves_nothing() =>
		Gen.Select(Campaigns, Options).Sample((campaigns, options) =>
		{
			var now = Gens.Start.AddDays(-1);
			var moves = Rebalancer.Plan(campaigns, options, now, Amsterdam);
			var again = campaigns.Select(c => c with { Campaign = Rebalancer.Apply(c.Campaign, moves) }).ToList();

			Rebalancer.Plan(again, options, now, Amsterdam).Should().BeEmpty();
		});

	[Test]
	public void A_post_already_keeping_the_rules_stays_where_it_is()
	{
		var monday = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(1));
		var campaign = new Campaign("curb-1-0", "Curb", "curb", CampaignStatus.Approved, [Network.Bluesky], [], "", "owner:me",
			[new Post("01", monday, [new Variant(Network.Bluesky, "Out.")]), new Post("02", monday.AddDays(5), [new Variant(Network.Bluesky, "Again.")])]);

		var moves = Rebalancer.Plan([new Rebalanceable(campaign, new HashSet<string>())], RebalanceOptions.Default, monday.AddDays(-1), Amsterdam);

		moves.Should().ContainSingle("Saturday is a rest day by default; Monday at ten is fine")
			.Which.Should().Be(new PostMove("curb-1-0", "02", monday.AddDays(5), new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.FromHours(1))));
	}

	[Test]
	public void Two_campaigns_on_one_day_are_spread_and_options_that_leave_no_day_are_refused()
	{
		var monday = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(1));
		Campaign On(string slug) => new(slug, slug, slug, CampaignStatus.Proposed, [Network.Bluesky], [], "", "agent:bot", [new Post("01", monday, [new Variant(Network.Bluesky, "Out.")])]);

		var moves = Rebalancer.Plan([new Rebalanceable(On("a"), new HashSet<string>()), new Rebalanceable(On("b"), new HashSet<string>())], RebalanceOptions.Default, monday.AddDays(-1), Amsterdam);

		moves.Should().ContainSingle().Which.Should().Be(new PostMove("b", "01", monday, new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(1))));
		(RebalanceOptions.Default with { RestDays = [.. Enum.GetValues<DayOfWeek>()] }).Problems.Should().ContainSingle();
		(RebalanceOptions.Default with { From = new TimeOnly(17, 0), To = new TimeOnly(9, 0) }).Problems.Should().ContainSingle();
		(RebalanceOptions.Default with { PerDay = 0 }).Problems.Should().ContainSingle();
	}
}
