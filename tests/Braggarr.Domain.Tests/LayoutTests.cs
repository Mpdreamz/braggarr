using AwesomeAssertions;
using CsCheck;

namespace Braggarr.Domain.Tests;

public class LayoutTests
{
	private static readonly Gen<TalkingPoint[]> Points = Gen.OneOfConst(
		"A C# formatter that keeps diffs small", "Follows your .editorconfig", "One native binary", "A CI check", "A GitHub Action",
		"Formats a large solution in seconds", "Ignores generated files", "Free and open source"
	).Array[1, 8].Select(texts => texts.Distinct().Select(t => new TalkingPoint(t)).ToArray());

	[Test]
	public void Every_point_is_said_somewhere_and_reaches_every_stream_network_and_posts_go_only_where_the_campaign_does() =>
		Gen.Select(Points, Gens.CampaignNetworks).Sample((points, networks) =>
		{
			var posts = Layout.Lay(points, networks, Gens.Start, Layout.DefaultSpacing);
			var campaign = new Campaign("x", "X", "x", CampaignStatus.Proposed, networks, [], "", "", posts) { Points = [.. points] };

			// A short or long network, or the blog, says every point. Hacker News alone carries only the first, as its title; YouTube
			// only points that want a video, and these want none.
			campaign.IsCovered.Should().Be(
				networks.Any(n => n.Shape() is NetworkShape.Stream or NetworkShape.Article or NetworkShape.Longform)
				|| (points.Length == 1 && networks.Contains(Network.HackerNews))
			);
			posts.Select(p => p.Id).Should().OnlyHaveUniqueItems();
			posts.Should().OnlyContain(p => p.Networks!.All(n => networks.Contains(n)) && p.Networks!.Count > 0);
			// YouTube posts only what wants a video, and these points want none.
			networks.Where(n => n != Network.YouTube).Should().OnlyContain(n => posts.Any(p => p.Networks!.Contains(n)), "every other network gets at least one post");
			posts.Should().NotContain(p => p.Networks!.Contains(Network.YouTube));
		});

	[Test]
	public void Stream_networks_get_a_post_per_point_spaced_out_and_linkedin_one_with_the_top_points()
	{
		TalkingPoint[] points = [new("Keeps diffs small"), new("Follows .editorconfig"), new("One native binary")];

		var posts = Layout.Lay(points, [Network.Bluesky, Network.X, Network.LinkedIn, Network.HackerNews], Gens.Start, TimeSpan.FromDays(2));

		posts.Select(p => (p.Id, p.At, string.Join(",", p.Networks!), string.Join(",", p.Points))).Should().Equal(
			("00-overview", Gens.Start, "LinkedIn", "0,1,2"),
			("01-keeps-diffs-small", Gens.Start, "Bluesky,X,HackerNews", "0"),
			("02-follows-editorconfig", Gens.Start.AddDays(2), "Bluesky,X", "1"),
			("03-one-native-binary", Gens.Start.AddDays(4), "Bluesky,X", "2"));
	}

	[Test]
	public void YouTube_gets_the_points_that_want_a_video_with_their_short_post()
	{
		TalkingPoint[] points =
		[
			new("Keeps diffs small", [new WantedMedia(MediaKind.Video, "A run where one line changes")]),
			new("Fast"),
			new("A GitHub Action", [new WantedMedia(MediaKind.Video, "The check on a pull request")])
		];

		var posts = Layout.Lay(points, [Network.Bluesky, Network.YouTube], Gens.Start, TimeSpan.FromDays(2));

		posts.Select(p => (p.Id, string.Join(",", p.Networks!))).Should().Equal(
			("01-keeps-diffs-small", "Bluesky,YouTube"),
			("02-fast", "Bluesky"),
			("03-a-github-action", "Bluesky,YouTube"));
		Layout.Lay(points, [Network.YouTube], Gens.Start, TimeSpan.FromDays(2)).Select(p => p.Id).Should().Equal("01-keeps-diffs-small", "03-a-github-action");
	}

	[Test]
	public void Without_short_networks_the_long_post_takes_every_point()
	{
		TalkingPoint[] points = [.. Enumerable.Range(1, 7).Select(i => new TalkingPoint($"Point {i}"))];

		var posts = Layout.Lay(points, [Network.LinkedIn], Gens.Start, TimeSpan.FromDays(2));

		posts.Should().ContainSingle().Which.Points.Should().Equal(0, 1, 2, 3, 4, 5, 6);
		Layout.Lay(points, [Network.LinkedIn, Network.Bluesky], Gens.Start, TimeSpan.FromDays(2))[0].Points.Should().Equal(0, 1, 2, 3, 4);
	}

	[Test]
	public void A_campaign_that_leaves_a_point_off_a_stream_network_is_not_ready()
	{
		var posts = Layout.Lay([new TalkingPoint("One"), new TalkingPoint("Two")], [Network.Bluesky], Gens.Start, TimeSpan.FromDays(1));
		var drafted = posts.Select(p => p with { Variants = [new Variant(Network.Bluesky, "Text.")] }).ToValueList();
		var campaign = new Campaign("x", "X", "x", CampaignStatus.Proposed, [Network.Bluesky], [], "", "", drafted) { Points = [new("One"), new("Two")] };

		campaign.IsReady.Should().BeTrue();
		(campaign with { Posts = [drafted[0]] }).IsReady.Should().BeFalse();
		(campaign with { Posts = [drafted[0]] }).Approve(new(Gens.Owner, Gens.Start)).Status.Should().Be(CampaignStatus.Proposed);
	}
}
