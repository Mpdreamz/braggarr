using AwesomeAssertions;
using Nullean.Entities.CsCheck;

namespace Braggarr.Domain.Tests;

public class CampaignRulesTests
{
	/// <summary>
	/// A counter-example changes what a campaign says, and an edit takes an approval away. So an approved campaign keeps an
	/// approval of what it says now, and only the rule the counter-example is for breaks. A proposal it approves gets one too.
	/// </summary>
	private static Func<Campaign, Campaign> Kept(Func<Campaign, Campaign> breakIt) =>
		c =>
		{
			var broken = breakIt(c);
			var wasApproved = c.IsApproved || (c.Status == CampaignStatus.Proposed && broken.Status is CampaignStatus.Approved or CampaignStatus.Held);
			return wasApproved && broken.Status is CampaignStatus.Approved or CampaignStatus.Held && broken.Approval?.Digest != "the-digest-before-an-edit"
				? broken with { Approval = new Approval(Gens.Owner.ToString(), broken.Digest, Gens.Start) }
				: broken;
		};

	public static AutoTest<Campaign> Auto() =>
		AutoTest.For(CampaignRules.Instance, Gens.Campaigns)
			.Deeper(Closed)
			// A post twice: never the blog post, whose second copy would also be a second blog post.
			.CounterExample(CampaignRules.PostIdsAreUnique, Kept(c => c.Posts.FirstOrDefault(p => !p.NetworksIn(c).Contains(Network.Blog)) is { } post ? c with { Posts = c.Posts.Add(post) } : c))
			// A Bluesky post with a blog article's fields, on a proposal: an approved campaign would also stop being complete.
			.CounterExample(CampaignRules.OnlyTheBlogHasAnArticle, c => c.Status is CampaignStatus.Approved or CampaignStatus.Held ? c
				: c.Posts.SelectMany(p => p.Variants.Where(v => v.Network != Network.Blog).Select(v => (p, v))).FirstOrDefault() is ({ } post, { } variant)
					? c with { Posts = c.Posts.Replace(p => p.Id == post.Id, post with { Variants = post.Variants.Replace(v => v == variant, variant with { Article = Gens.Article }) }) }
					: c)
			// The blog post once more, under another id: a second article about the campaign.
			.CounterExample(CampaignRules.OneBlogPostPerCampaign, Kept(c => c.Posts.FirstOrDefault(p => p.NetworksIn(c).Contains(Network.Blog)) is { } blog
				? c with { Posts = c.Posts.Add(blog with { Id = "99-blog-again" }) }
				: c))
			// A link to the blog in a campaign without a blog post, on a variant that fits with it.
			.CounterExample(CampaignRules.BlogLinksNeedABlogPost, Kept(c => c.Networks.Contains(Network.Blog) ? c : WithLink(c, c.Posts)))
			// A post that links the blog post, planned at the same moment as it: before its site has built.
			.CounterExample(CampaignRules.BlogComesFirst, Kept(c => c.Posts.FirstOrDefault(p => p.NetworksIn(c).Contains(Network.Blog)) is { } blog
				? WithLink(c, c.Posts.Where(p => p.Id != blog.Id).Select(p => p with { At = blog.At }))
				: c))
			// An approved blog post whose image is still only wanted.
			.CounterExample(CampaignRules.ApprovedBlogPostsHaveTheirImages, Kept(c => c.Status is CampaignStatus.Approved or CampaignStatus.Held
				&& c.Posts.FirstOrDefault(p => p.For(Network.Blog) is not null) is { } blogPost
					? c with { Posts = c.Posts.Replace(p => p.Id == blogPost.Id, blogPost with { Variants = blogPost.Variants.Replace(v => v.Network == Network.Blog, blogPost.For(Network.Blog)! with { Media = [new MediaRef("wanted:image", "The diff")] }) }) }
					: c))
			// A post id that would write outside the campaign's folder: only the file-name rule breaks.
			.CounterExample(
				CampaignRules.PostIdsAreFileNames, Kept(c => c with { Posts = c.Posts.Replace(p => p.Id == c.Posts[0].Id, c.Posts[0] with { Id = "../heed-0-20/01-launch" }) }))
			.CounterExample(
				CampaignRules.OneVariantPerNetwork, Kept(c => c with { Posts = c.Posts.Replace(p => p.Id == c.Posts[0].Id, c.Posts[0] with { Variants = c.Posts[0].Variants.Add(c.Posts[0].Variants[0] with { Text = "again" }) }) }))
			// A point no post covers: only the coverage rule breaks, on a campaign with a stream network. A rejected campaign can never be
			// approved, so it stays as it is: a base the counter-example leaves unbroken is skipped.
			.CounterExample(
				CampaignRules.ApprovedCampaignsCoverEveryPoint, Kept(c => c.Status == CampaignStatus.Rejected ? c : c with
				{
					Status = c.Status == CampaignStatus.Proposed ? CampaignStatus.Approved : c.Status,
					Networks = [Network.Bluesky],
					Accounts = [.. c.Accounts.Where(a => a.Network == Network.Bluesky)],
					Points = [.. c.Points, new TalkingPoint("Never posted")],
					Posts = [.. c.Posts.Select(p => p with { Networks = [Network.Bluesky], Variants = [new Variant(Network.Bluesky, "Out.")] })]
				}))
			// An account for a network the campaign does not go to: nothing else reads it.
			.CounterExample(
				CampaignRules.AccountsAreForItsNetworks, Kept(c => c with { Accounts = [.. c.Accounts, new CampaignAccount(Networks.All.First(n => !c.Networks.Contains(n)), "nullean")] }))
			.CounterExample(CampaignRules.LinksAreWebAddresses, Kept(c => c with { Links = [.. c.Links, "github.com/nullean/curb"] }))
			.CounterExample(
				CampaignRules.PointsSaySomething, Kept(c => c.Points.Count == 0 ? c : c with { Points = c.Points.Replace(p => p == c.Points[0], c.Points[0] with { Text = " " }) }))
			// A variant for a network its post does not go to, and the campaign does not either: nothing else looks at it.
			.CounterExample(
				CampaignRules.VariantsAreForThePostsNetworks, Kept(c =>
				{
					var stray = Networks.All.First(n => !c.Networks.Contains(n));
					return c with { Posts = c.Posts.Replace(p => p.Id == c.Posts[0].Id, c.Posts[0] with { Variants = c.Posts[0].Variants.Add(new Variant(stray, "Out.")) }) };
				}))
			// An approved campaign that lost one variant: it is no longer complete, and that is all.
			.CounterExample(
				CampaignRules.ApprovedCampaignsAreComplete, Kept(c => c.Status is CampaignStatus.Approved or CampaignStatus.Held && c.Posts[0].Variants.Count > 0
					? c with { Posts = c.Posts.Replace(p => p.Id == c.Posts[0].Id, c.Posts[0] with { Variants = [.. c.Posts[0].Variants.Skip(1)] }) }
					: c))
			.CounterExample(CampaignRules.PostsCoverKnownPoints, Kept(c => c with { Posts = c.Posts.Replace(p => p.Id == c.Posts[0].Id, c.Posts[0] with { Points = [.. c.Posts[0].Points, 99] }) }))
			// One more post, complete, to a network the campaign does not name: only this rule breaks.
			.CounterExample(
				CampaignRules.PostsGoToTheCampaignsNetworks, Kept(c =>
				{
					var elsewhere = Networks.All.First(n => !n.IsManual() && !c.Networks.Contains(n));
					return c with { Posts = c.Posts.Add(new Post("99-elsewhere", Gens.Start, [new Variant(elsewhere, "Out.")]) { Networks = [elsewhere] }) };
				}))
			.CounterExample(
				CampaignRules.ImagesAreDescribedAndInTheCampaignFolder, Kept(// The same variants, one of them with an image that has no alt text: the only rule that breaks. Never a YouTube
																			 // variant, whose video an approved campaign needs.
				c => c.Posts[0].Variants.FirstOrDefault(v => !Lengths.NeedsVideo(v.Network)) is { } first
					? c with
					{
						Posts = c.Posts.Replace(p => p.Id == c.Posts[0].Id, c.Posts[0] with
						{
							Variants = c.Posts[0].Variants.Replace(v => v.Network == first.Network, first with { Media = [new MediaRef("media/card.png", "")] })
						})
					}
					: c))
			// An approved YouTube post whose video is still only wanted: it fits, and is not ready, and that is all.
			.CounterExample(
				CampaignRules.ApprovedCampaignsHaveTheirVideos, Kept(c => c.Status is CampaignStatus.Approved or CampaignStatus.Held && c.Posts.FirstOrDefault(p => p.For(Network.YouTube) is not null) is { } post
					? c with
					{
						Posts = c.Posts.Replace(p => p.Id == post.Id, post with
						{
							Variants = post.Variants.Replace(v => v.Network == Network.YouTube, post.For(Network.YouTube)! with { Media = [new MediaRef("wanted:video", "A run")] })
						})
					}
					: c));

	/// <summary>
	/// The first of <paramref name="candidates"/> with a variant that still fits once it links the blog post, given that link (and
	/// whatever else the candidate changes); the campaign as it is when there is none.
	/// </summary>
	private static Campaign WithLink(Campaign c, IEnumerable<Post> candidates) =>
		candidates.SelectMany(p => p.Variants.Where(v => v.Network != Network.Blog && !v.LinksToBlog() && Lengths.Fits(v.Network, $"{v.Text} {Blogging.Placeholder}")).Select(v => (p, v)))
			.FirstOrDefault() is ({ } post, { } variant)
			? c with { Posts = c.Posts.Replace(p => p.Id == post.Id, post with { Variants = post.Variants.Replace(v => v == variant, variant with { Text = $"{variant.Text} {Blogging.Placeholder}" }) }) }
			: c;

	/// <summary>A proposal ready to approve: two points, laid out on Bluesky and LinkedIn, every variant written.</summary>
	private static readonly Campaign Ready = Drafted(new Campaign(
		"curb-1-0", "Curb 1.0", "curb", CampaignStatus.Proposed, [Network.Bluesky, Network.LinkedIn], ["https://github.com/nullean/curb"], "Announce curb.",
		"agent:release-bot", []
	)
	{ Points = [new TalkingPoint("Keeps diffs small"), new TalkingPoint("Fast")] });

	/// <summary>One variant short of ready.</summary>
	private static readonly Campaign Unwritten = Ready with { Posts = Ready.Posts.Replace(p => p.Id == "01-keeps-diffs-small", Ready.Posts[1] with { Variants = [] }) };

	/// <summary>Every variant written, but the second point never reaches Bluesky.</summary>
	private static readonly Campaign Uncovered = Ready with { Posts = [.. Ready.Posts.Where(p => p.Id != "02-fast")] };

	private static Campaign Drafted(Campaign c) =>
		c with
		{
			Posts = [.. Layout.Lay(c.Points, c.Networks, Gens.Start, Layout.DefaultSpacing).Select(p => p with { Variants = [.. p.NetworksIn(c).Select(n => new Variant(n, "Curb 1.0 is out."))] })]
		};

	/// <summary>Every status a campaign can reach from three proposals, by every kind of actor: proved, and every edge taken.</summary>
	/// <summary>Approved, then edited on main: it needs approving again. And the same, held.</summary>
	private static readonly Campaign Edited = Ready.Approve(new Approving(Gens.Owner, Gens.Start)) with
	{
		Posts = Ready.Posts.Replace(p => p.Id == "02-fast", Ready.Posts.Single(p => p.Id == "02-fast") with { At = Gens.Start.AddDays(5) })
	};

	private static readonly Campaign EditedHeld = Edited with { Status = CampaignStatus.Held };

	private static EntitySpec<Campaign> Closed(EntitySpec<Campaign> spec) =>
		spec.From(Ready, Unwritten, Uncovered, Edited, EditedHeld)
			.Domain(Gens.Owner, Gens.Agent, Actor.System, Actor.Judge)
			.Domain([.. new[] { Gens.Owner, Gens.Agent, Actor.System, Actor.Judge }.Select(a => new Approving(a, Gens.Start))]);

	public static IEnumerable<string> Operations() => CampaignRules.Actions.Select(a => a.Name);

	[Test]
	[MethodDataSource(nameof(Operations))]
	public void An_operation_that_does_nothing_is_noticed(string operation) =>
		NoOps.GoesUnnoticed(CampaignRules.All, CampaignRules.Actions, operation, Closed).Should().BeFalse($"{operation} doing nothing must fail the proof");

	[Test]
	public void Every_bug_the_lifecycle_could_have_is_caught_by_a_rule() =>
		_ = Closed(CampaignRules.Instance.Spec()).From(Ready)
			.Fault("Approve holds", "Approve", (before, after) => after.Status == CampaignStatus.Approved ? after with { Status = CampaignStatus.Held } : after)
			.Fault("Hold rejects", "Hold", (before, after) => after.Status == CampaignStatus.Held ? after with { Status = CampaignStatus.Rejected } : after)
			.Fault("Resume proposes again", "Resume", (before, after) => before.Status == CampaignStatus.Held ? after with { Status = CampaignStatus.Proposed } : after)
			.Fault("Reject takes back an approval", "Reject", (before, after) => before.Status == CampaignStatus.Approved ? after with { Status = CampaignStatus.Rejected } : after)
			.Faults();

	[Test]
	public void Approving_what_is_not_written_is_caught() =>
		_ = Closed(CampaignRules.Instance.Spec()).From(Unwritten)
			.Fault("Approve ignores what is missing", "Approve", (before, after) => before.Status == CampaignStatus.Proposed ? after with { Status = CampaignStatus.Approved } : after)
			.Faults();

	[Test]
	public void Approving_a_point_left_out_is_caught() =>
		_ = Closed(CampaignRules.Instance.Spec()).From(Uncovered)
			.Fault("Approve ignores the points", "Approve", (before, after) => before.Status == CampaignStatus.Proposed ? after with { Status = CampaignStatus.Approved } : after)
			.Faults();

	[Test]
	public void The_diagram_reaches_every_status_through_every_edge() => CampaignLifecycle.Machine.Spec().Exhaustive();

	[Test]
	public void A_campaign_follows_its_diagram_when_the_owner_decides() =>
		CampaignLifecycle.Machine.ConformTo(
			Ready,
			c => c.Status,
			bindings:
			[
				("Approve", c => c.Approve(new Approving(Gens.Owner, Gens.Start))),
				("Reject", c => c.Reject(Gens.Owner)),
				("Hold", c => c.Hold(Gens.Owner)),
				("Resume", c => c.Resume(Gens.Owner))
			]
		);

	public static IEnumerable<Func<AxisCase>> Axes() => Auto().AsDataSource();

	[Test]
	[MethodDataSource(nameof(Axes))]
	public Task EveryDeclaredAxisHolds(AxisCase axis) => axis.RunAsync();
}
