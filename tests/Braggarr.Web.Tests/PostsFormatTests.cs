using AwesomeAssertions;
using Braggarr.Adapters.Posts;
using Braggarr.Domain;
using CsCheck;

namespace Braggarr.Web.Tests;

public class PostsFormatTests
{
	private static readonly Campaign Curb = new(
		"curb-1-0", "Curb 1.0", "curb", CampaignStatus.Proposed, [Network.Bluesky, Network.HackerNews], ["https://github.com/nullean/curb"],
		"Announce curb 1.0.\n\nIt formats C# without moving lines.", "agent:release-bot",
		[
			new Post(
				"01-launch",
				new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.FromHours(1)),
				[
					new Variant(Network.Bluesky, "Curb 1.0 is out.\n\nhttps://github.com/nullean/curb", [new MediaRef("media/card.png", "Curb's logo")]),
					new Variant(Network.HackerNews, "Show HN: Curb, a C# formatter that keeps diffs small\nhttps://github.com/nullean/curb")
				]
			) { Angle = "What it is, for whom" }
		]
	)
	{ RepeatMonths = 3, LastRepeat = new DateTimeOffset(2027, 2, 3, 10, 0, 0, TimeSpan.FromHours(1)) };

	[Test]
	public void What_braggarr_writes_it_reads_back_byte_for_byte()
	{
		var files = PostsFormat.WriteCampaign(Curb);

		var read = PostsFormat.ReadCampaign(new CampaignFiles("curb-1-0", files), CampaignStatus.Proposed);

		read.Should().Be(Curb);
		PostsFormat.WriteCampaign(read).Should().Equal(files);
	}

	/// <summary>Article bodies that would trip a reader: a network heading, front-matter fences, code, unicode, length.</summary>
	private static readonly Gen<string> ArticleBodies = Gen.OneOfConst(
		"# Curb 1.0\n\nIt keeps diffs small.",
		"## bluesky\n\nNot a variant: a heading in the article.",
		"---\ntitle: not front matter\n---\n\nA rule, then text.",
		"```yaml\n---\nkey: value\n## x\n```\n\nCode that looks like structure.",
		"Ünïcödé, emoji 🎉, and a right-to-left word: שלום.",
		"![The diff](media/diff.png)\n\nAn image in the text, and [a link](https://example.com).",
		string.Concat(Enumerable.Repeat("A long paragraph that goes on. ", 400))
	).Array[1, 4].Select(parts => string.Join("\n\n", parts.Distinct()));

	[Test]
	public void A_blog_article_reads_back_byte_for_byte_and_nothing_in_it_reads_as_a_variant() =>
		Gen.Select(ArticleBodies, Gen.Bool).Sample((body, cover) =>
		{
			var article = new Variant(Network.Blog, body, [.. body.Contains("media/diff.png", StringComparison.Ordinal) ? [new MediaRef("media/diff.png", "The diff")] : Array.Empty<MediaRef>()])
			{
				Article = new Article("Curb 1.0, from the start", "What curb does to a diff.", ["dotnet"]) { Cover = cover ? new MediaRef("media/card.png", "Curb's logo") : null }
			};
			var campaign = Curb with
			{
				Networks = [Network.Bluesky, Network.HackerNews, Network.Blog],
				Posts = [Curb.Posts[0] with { Variants = [.. Curb.Posts[0].Variants, article], Networks = null }]
			};

			var files = PostsFormat.WriteCampaign(campaign);
			var read = PostsFormat.ReadCampaign(new CampaignFiles("curb-1-0", files), CampaignStatus.Proposed);

			files.Keys.Should().Contain("01-launch.blog.md");
			files["01-launch.md"].Should().NotContain("## blog");
			read.Posts.Single().Variants.Select(v => v.Network).Should().BeEquivalentTo([Network.Bluesky, Network.HackerNews, Network.Blog]);
			read.Should().Be(campaign);
			PostsFormat.WriteCampaign(read).Should().Equal(files);
		});

	private static readonly Campaign Points = new(
		"curb-points", "Curb 1.0", "curb", CampaignStatus.Proposed, [Network.Bluesky, Network.LinkedIn], [], "Announce curb.", "agent:bot", []
	)
	{
		Points =
		[
			new TalkingPoint("Keeps diffs small", [new WantedMedia(MediaKind.Video, "A format run where one line changes")]),
			new TalkingPoint("Fast")
		]
	};

	[Test]
	public void Talking_points_and_where_each_post_goes_read_back_byte_for_byte()
	{
		var start = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.FromHours(1));
		var campaign = Points with { Posts = Layout.Lay(Points.Points, Points.Networks, start, Layout.DefaultSpacing) };
		campaign = campaign with
		{
			Posts =
			[
				.. campaign.Posts.Select(p => p with
				{
					Variants = [.. p.NetworksIn(campaign).Select(n => new Variant(n, "Text.", p.Points.Contains(0) ? [MediaRef.Wanted(campaign.Points[0].Wanted[0])] : []))]
				})
			]
		};

		var files = PostsFormat.WriteCampaign(campaign);
		var read = PostsFormat.ReadCampaign(new CampaignFiles(campaign.Slug, files), CampaignStatus.Proposed);

		read.Should().Be(campaign);
		PostsFormat.WriteCampaign(read).Should().Equal(files);
		files["01-keeps-diffs-small.md"].Should().Contain("networks:").And.Contain("points:").And.Contain("![A format run where one line changes](wanted:video)");
	}

	[Test]
	public void Points_read_as_a_person_would_write_them()
	{
		var campaign = PostsFormat.ReadCampaign(
			new CampaignFiles("c", new Dictionary<string, string>
			{
				["campaign.md"] = """
					---
					title: Curb
					networks: [bluesky]
					points:
					- text: Keeps diffs small
					  wanted:
					  - kind: image
					    description: A diff with one line
					- text: Fast
					---
					Brief.
					""",
				["01-small.md"] = """
					---
					at: 2026-11-09T10:00:00+01:00
					networks: [bluesky]
					points: [1]
					---

					## bluesky

					Small diffs.
					"""
			}),
			CampaignStatus.Proposed
		);

		campaign.Points.Should().Equal(new TalkingPoint("Keeps diffs small", [new WantedMedia(MediaKind.Image, "A diff with one line")]), new TalkingPoint("Fast"));
		campaign.Posts[0].Points.Should().Equal(0);
		campaign.Posts[0].Networks.Should().Equal(Network.Bluesky);
	}

	[Test]
	public void An_approval_reads_back_and_is_written_on_its_own_lines()
	{
		var approval = new Approval("judge:claude", Curb.Digest, new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.FromHours(1)))
		{
			NotBefore = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.FromHours(1))
		};
		var files = PostsFormat.WriteCampaign(Curb with { Approval = approval });

		PostsFormat.ReadCampaign(new CampaignFiles("curb-1-0", files), CampaignStatus.Approved).Approval.Should().Be(approval);
		var stamped = PostsFormat.SetApproval(PostsFormat.WriteCampaign(Curb)[PostsFormat.CampaignFile], approval);
		stamped.Should().Contain("approved_by: judge:claude").And.Contain($"approved_digest: {Curb.Digest}").And.Contain("approved_not_before: 2026-11-02T08:00:00+01:00");
		PostsFormat.ReadCampaign(new CampaignFiles("curb-1-0", new Dictionary<string, string>(files) { [PostsFormat.CampaignFile] = stamped }), CampaignStatus.Approved)
			.Approval.Should().Be(approval);
	}

	[Test]
	public void A_post_file_reads_as_a_person_would_write_it()
	{
		var post = PostsFormat.ReadPost("02-week-later", """
			---
			at: 2026-11-09T10:00:00+01:00
			---

			## bluesky

			A week of curb.

			## mastodon

			A week of curb, longer.
			Two lines.
			""");

		post.At.Should().Be(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(1)));
		post.Variants.Should().Equal(new Variant(Network.Bluesky, "A week of curb."), new Variant(Network.Mastodon, "A week of curb, longer.\nTwo lines."));
	}

	[Test]
	public void A_blog_heading_in_a_post_file_is_text_not_a_variant()
	{
		var post = PostsFormat.ReadPost("01-launch", "---\nat: 2026-11-09T10:00:00+01:00\n---\n\n## mastodon\n\nWhat I wrote on the blog:\n## blog\nis a heading here.");

		post.Variants.Should().Equal(new Variant(Network.Mastodon, "What I wrote on the blog:\n## blog\nis a heading here."));
	}

	[Test]
	public void Holding_touches_only_the_held_line()
	{
		const string original = "---\ntitle: Curb 1.0   # the owner's own comment\nnetworks: [bluesky]\n---\n\nThe brief.\n";

		var held = PostsFormat.SetHeld(original, held: true);

		held.Should().Be("---\ntitle: Curb 1.0   # the owner's own comment\nnetworks: [bluesky]\nheld: true\n---\n\nThe brief.\n");
		PostsFormat.SetHeld(held, held: false).Should().Be(original);
	}

	[Test]
	public void Deliveries_round_trip()
	{
		var key = new DeliveryKey("curb-1-0", "01-launch", Network.Bluesky);
		var at = new DateTimeOffset(2026, 11, 2, 9, 30, 5, TimeSpan.Zero);
		Delivery[] deliveries =
		[
			Delivery.New(key).Begin(at).Succeed(new Receipt("at://did/app.bsky.feed.post/3abc", "https://bsky.app/profile/me/post/3abc")),
			Delivery.New(key with { Network = Network.HackerNews }).OpenManual(12)
		];

		var read = PostsFormat.ReadDeliveries("curb-1-0", PostsFormat.WriteDeliveries(deliveries));

		read.Should().BeEquivalentTo(deliveries);
	}
}
