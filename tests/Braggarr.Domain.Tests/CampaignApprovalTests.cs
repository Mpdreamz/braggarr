using AwesomeAssertions;
using Nullean.Entities;
using Nullean.Entities.CsCheck;

namespace Braggarr.Domain.Tests;

/// <summary>
/// The rule the whole review flow rests on: an agent proposes; the owner approves, or the judge when the owner lets it. An agent
/// never does.
/// </summary>
public class CampaignApprovalTests
{
	public static readonly TemporalRuleSet<CampaignEvent> Rules = TemporalRuleSet<CampaignEvent>.From(
		("only-the-owner-or-the-judge-approves", Temporal<CampaignEvent>.Always<CampaignEvent.Approved>(e => e.By.MayApprove)),
		("an-agent-never-approves", Temporal<CampaignEvent>.Always<CampaignEvent.Approved>(e => e.By.Kind != ActorKind.Agent)),
		("an-approval-names-who-gave-it", Temporal<CampaignEvent>.Always<CampaignEvent.Approved>(e => e.Stamp.By == e.By.ToString())),
		("only-the-owner-resumes", Temporal<CampaignEvent>.Always<CampaignEvent.Resumed>(e => e.By.Kind == ActorKind.Owner))
	);

	private static readonly CampaignCommand[] Commands =
	[
		.. from decision in Enum.GetValues<CampaignDecision>()
		from actor in new[] { Gens.Owner, Gens.Agent, Actor.System, Actor.Judge }
		select new CampaignCommand(decision, actor) { At = Gens.Start }
	];

	private static Campaign Proposed(bool complete) =>
		new(
			"curb-1-0", "Curb 1.0", "curb", CampaignStatus.Proposed, [Network.Bluesky, Network.Mastodon], [], "", "agent:release-bot",
			[
				new Post(
					"01-launch",
					Gens.Start,
					complete
						? [new Variant(Network.Bluesky, "Curb 1.0 is out."), new Variant(Network.Mastodon, "Curb 1.0 is out.")]
						: [new Variant(Network.Bluesky, "Curb 1.0 is out.")]
				)
			]
		);

	[Test]
	public void No_sequence_of_commands_lets_anyone_but_the_owner_or_the_judge_approve()
	{
		// A proposal, and a campaign edited on main after it was approved, approved or held: approving again is an edge too.
		var proposed = Proposed(complete: true);
		var edited = proposed.Approve(new Approving(Gens.Owner, Gens.Start)) with
		{
			Posts = [proposed.Posts[0] with { Variants = proposed.Posts[0].Variants.Replace(v => v.Network == Network.Bluesky, new Variant(Network.Bluesky, "Curb 1.0, edited.")) }]
		};
		Campaign[] starts = [proposed, edited, edited with { Status = CampaignStatus.Held }];
		var specs = starts.Select(start => EntitySpec.EventSourced(
			start, CampaignDecider.Decide, CampaignDecider.Evolve, Commands, CampaignRules.All
		)).ToList();

		// The rules on the campaign, over every start together: each edge of both lifecycles is taken from one of them.
		_ = specs[0].Spec.From(starts).Exhaustive();
		// The rules on the events, from each start.
		foreach (var spec in specs)
		{
			_ = spec.Exhaustive(Rules);
		}
	}

	[Test]
	public void An_incomplete_campaign_cannot_be_approved_even_by_the_owner() =>
		Proposed(complete: false).Approve(new(Gens.Owner, Gens.Start)).Status.Should().Be(CampaignStatus.Proposed);

	private static readonly Variant Blog = new(Network.Blog, "# Curb\n\nIt keeps diffs small.") { Article = new Article("Curb 1.0", "A formatter.", ["dotnet"]) };

	[Test]
	public void A_blog_variant_fits_only_with_an_article_that_fits()
	{
		Blog.Fits.Should().BeTrue();
		(Blog with { Article = null }).Fits.Should().BeFalse("a blog post needs its title and description");
		(Blog with { Article = Blog.Article! with { Title = new string('x', 101) } }).Fits.Should().BeFalse();
		(Blog with { Article = Blog.Article! with { Description = "" } }).Fits.Should().BeFalse();
		(Blog with { Article = Blog.Article! with { Tags = ["Not A Slug"] } }).Fits.Should().BeFalse();
		(Blog with { Article = Blog.Article! with { Cover = new MediaRef("media/card.png", "") } }).Fits.Should().BeFalse("a cover has alt text");
		new Variant(Network.Bluesky, "Out.") { Article = Blog.Article }.Fits.Should().BeFalse("only a blog post has an article");
	}

	[Test]
	public void Changing_what_a_blog_post_says_about_itself_takes_its_approval_away()
	{
		var campaign = new Campaign("curb-1-0", "Curb 1.0", "curb", CampaignStatus.Proposed, [Network.Blog], [], "", "agent:x",
			[new Post("00-blog", Gens.Start, [Blog])]);
		var digest = campaign.Digest;

		Article[] edits =
		[
			Blog.Article! with { Title = "Curb 1.0, again" },
			Blog.Article! with { Description = "Another one." },
			Blog.Article! with { Tags = ["dotnet", "csharp"] },
			Blog.Article! with { Slug = "curb" },
			Blog.Article! with { Cover = new MediaRef("media/card.png", "Curb's logo") }
		];
		foreach (var edit in edits)
		{
			(campaign with { Posts = [campaign.Posts[0] with { Variants = [Blog with { Article = edit }] }] }).Digest.Should().NotBe(digest, edit.ToString());
		}
		new Campaign("c", "C", "c", CampaignStatus.Proposed, [Network.Bluesky], [], "", "x", [new Post("p", Gens.Start, [new Variant(Network.Bluesky, "Out.")])]).Digest
			.Should().Be(new Campaign("c", "C", "c", CampaignStatus.Proposed, [Network.Bluesky], [], "", "x", [new Post("p", Gens.Start, [new Variant(Network.Bluesky, "Out.")])]).Digest);
	}
}
