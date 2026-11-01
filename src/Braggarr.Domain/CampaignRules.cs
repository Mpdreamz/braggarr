using Nullean.Entities;

namespace Braggarr.Domain;

/// <summary>A campaign's life: proposed as a pull request, then approved (merged) or rejected (closed); held and resumed by the owner.</summary>
public static class CampaignLifecycle
{
	public static readonly StateMachine<CampaignStatus, string> Machine = StateMachine
		.Define<CampaignStatus, string>(CampaignStatus.Proposed)
		.Permit("Approve", CampaignStatus.Proposed, CampaignStatus.Approved)
		.Permit("Reject", CampaignStatus.Proposed, CampaignStatus.Rejected)
		.Permit("Hold", CampaignStatus.Approved, CampaignStatus.Held)
		.Permit("Resume", CampaignStatus.Held, CampaignStatus.Approved)
		.Build();
}

/// <summary>Every rule a campaign keeps. <c>[Constraints]</c> gathers them into <c>CampaignRules.All</c>.</summary>
[Constraints<Campaign>]
public static partial class CampaignRules
{
	// Declared first: static fields initialise in order, and the idempotency rules below read it.
	private static readonly Actor Owner = Actor.Owner("owner");

	public static readonly Constraint<Campaign> HasATitle = Constraints<Campaign>.Always(
		c => !string.IsNullOrWhiteSpace(c.Title),
		"A campaign has a title: it names the pull request and the campaign everywhere it is shown."
	);

	public static readonly Constraint<Campaign> LinksAreWebAddresses = Constraints<Campaign>.Always(
		c => c.Links.All(l => Uri.TryCreate(l, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"),
		"Every link is a full web address (https://…): drafting puts them in posts, and Hacker News submits the first."
	);

	public static readonly Constraint<Campaign> PointsSaySomething = Constraints<Campaign>.Always(
		c => c.Points.All(p => !string.IsNullOrWhiteSpace(p.Text) && p.Wanted.All(w => !string.IsNullOrWhiteSpace(w.Description))),
		"Every talking point says something, and every media it wants says what it should show."
	);

	public static readonly Constraint<Campaign> SlugIsAFolderName = Constraints<Campaign>.Always(
		c => Slugs.IsValid(c.Slug),
		"A campaign's slug is lowercase letters, digits and dashes, because it names a folder and a branch."
	);

	public static readonly Constraint<Campaign> AccountsAreForItsNetworks = Constraints<Campaign>.Always(
		c => c.Accounts.All(a => c.Networks.Contains(a.Network) && !string.IsNullOrWhiteSpace(a.Account))
			&& c.Accounts.Select(a => a.Network).Distinct().Count() == c.Accounts.Count,
		"A campaign names at most one account per network, and only for its own networks."
	);

	public static readonly Constraint<Campaign> PostIdsAreUnique = Constraints<Campaign>.Always(
		c => c.Posts.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == c.Posts.Count,
		"Two posts in one campaign never share an id."
	);

	/// <summary>
	/// A post's id names its file (<c>campaigns/&lt;slug&gt;/&lt;id&gt;.md</c>), so it is held to what a slug is: no slash, no dot,
	/// nothing that could reach another campaign's folder.
	/// </summary>
	public static readonly Constraint<Campaign> PostIdsAreFileNames = Constraints<Campaign>.Always(
		c => c.Posts.All(p => Slugs.IsValid(p.Id)),
		"A post's id is lowercase letters, digits and dashes, because it names the post's file in the campaign's folder."
	);

	public static readonly Constraint<Campaign> VariantsAreForThePostsNetworks = Constraints<Campaign>.Always(
		c => c.Posts.All(p => p.Variants.All(v => p.NetworksIn(c).Contains(v.Network))),
		"A variant is only written for a network its post goes to."
	);

	public static readonly Constraint<Campaign> PostsGoToTheCampaignsNetworks = Constraints<Campaign>.Always(
		c => c.Posts.All(p => p.Networks is null || p.Networks.All(n => c.Networks.Contains(n))),
		"A post only goes to networks the campaign posts to."
	);

	public static readonly Constraint<Campaign> PostsCoverKnownPoints = Constraints<Campaign>.Always(
		c => c.Posts.All(p => p.Points.All(i => i >= 0 && i < c.Points.Count)),
		"A post covers only talking points the campaign has."
	);

	public static readonly Constraint<Campaign> OneVariantPerNetwork = Constraints<Campaign>.Always(
		c => c.Posts.All(p => p.Variants.Select(v => v.Network).Distinct().Count() == p.Variants.Count),
		"A post has at most one variant per network."
	);

	public static readonly Constraint<Campaign> ImagesAreDescribedAndInTheCampaignFolder = Constraints<Campaign>.Always(
		c => c.Posts.All(p => p.Variants.All(v => v.MediaFits)),
		"Every image has alt text, lives in the campaign's media folder, and no variant carries more than its network takes."
	);

	public static readonly Constraint<Campaign> ApprovedCampaignsCoverEveryPoint = Constraints<Campaign>
		.When(c => c.Status is CampaignStatus.Approved or CampaignStatus.Held)
		.Requires(c => c.IsCovered, "Every talking point of an approved campaign is in some post, and reaches every short network.");

	public static readonly Constraint<Campaign> ApprovedCampaignsHaveTheirVideos = Constraints<Campaign>
		.When(c => c.Status is CampaignStatus.Approved or CampaignStatus.Held)
		.Requires(c => c.HasItsVideos, "A YouTube post of an approved campaign has its video: without one there is nothing to post.");

	public static readonly Constraint<Campaign> OnlyTheBlogHasAnArticle = Constraints<Campaign>.Always(
		c => c.Posts.All(p => p.Variants.All(v => v.Article is null || v.Network == Network.Blog)),
		"Only a blog post has a title, description and tags of its own; every other network's post is its text."
	);

	/// <summary>A campaign posts as one account per network, so one blog post per campaign is one per site.</summary>
	public static readonly Constraint<Campaign> OneBlogPostPerCampaign = Constraints<Campaign>.Always(
		c => c.Posts.Count(p => p.NetworksIn(c).Contains(Network.Blog)) <= 1,
		"A campaign has at most one blog post: its site gets one article about it, which the other posts link."
	);

	public static readonly Constraint<Campaign> BlogLinksNeedABlogPost = Constraints<Campaign>.Always(
		c => !c.Posts.Any(p => p.Variants.Any(v => v.LinksToBlog())) || c.Posts.Any(p => p.NetworksIn(c).Contains(Network.Blog)),
		"A post links {blog} only in a campaign with a blog post: otherwise there is no address to put there."
	);

	public static readonly Constraint<Campaign> BlogComesFirst = Constraints<Campaign>.Always(
		c => c.Posts.FirstOrDefault(p => p.NetworksIn(c).Contains(Network.Blog)) is not { } blog
			|| c.Posts.Where(p => p.Variants.Any(v => v.LinksToBlog())).All(p => p.At >= blog.At + Blogging.DeployMargin),
		"A post that links the blog post is planned at least half an hour after it, for its site to build."
	);

	public static readonly Constraint<Campaign> ApprovedBlogPostsHaveTheirImages = Constraints<Campaign>
		.When(c => c.Status is CampaignStatus.Approved or CampaignStatus.Held)
		.Requires(
			c => c.Posts.All(p => p.Variants.Where(v => v.Network == Network.Blog).All(v => v.Media.All(m => !m.IsWanted))),
			"An approved blog post has every image it wanted: a site keeps an article, so it never goes up with a gap."
		);

	public static readonly Constraint<Campaign> RepeatsAreYearsApartAtMost = Constraints<Campaign>.Always(
		c => c.RepeatMonths is >= 0 and <= 24,
		"A campaign repeats every 1 to 24 months, or never."
	);

	public static readonly Constraint<Campaign> ApprovedCampaignsAreComplete = Constraints<Campaign>
		.When(c => c.Status is CampaignStatus.Approved or CampaignStatus.Held)
		.Requires(c => c.IsComplete, "An approved campaign has a variant that fits for every post and network.");

	/// <summary>The edges of <see cref="CampaignLifecycle.Machine"/>, so the rule cannot drift from the diagram.</summary>
	public static readonly Constraint<Campaign> Lifecycle = CampaignLifecycle.Machine.AsConstraint<Campaign>(c => c.Status);

	/// <summary>
	/// The lifecycle with approval in it. Approving again (after an edit) is an edge of its own, approved or held. No operation
	/// takes an approval away: only a change to what the campaign says does, and that comes from the posts repo, not from here.
	/// </summary>
	public static readonly Constraint<Campaign> ApprovalLifecycle = Constraints<Campaign>
		.Transitions(c => (c.Status, c.IsApproved))
		.Allow((CampaignStatus.Proposed, false), (CampaignStatus.Approved, true))
		.Allow((CampaignStatus.Proposed, false), (CampaignStatus.Rejected, false))
		.Allow((CampaignStatus.Approved, true), (CampaignStatus.Held, true))
		.Allow((CampaignStatus.Held, true), (CampaignStatus.Approved, true))
		.Allow((CampaignStatus.Approved, false), (CampaignStatus.Held, false))
		.Allow((CampaignStatus.Held, false), (CampaignStatus.Approved, false))
		.Allow((CampaignStatus.Approved, false), (CampaignStatus.Approved, true))
		.Allow((CampaignStatus.Held, false), (CampaignStatus.Held, true));

	public static readonly Constraint<Campaign> ApprovingTwiceIsApprovingOnce = Constraints<Campaign>.Idempotent(c => c.Approve(new Approving(Owner, DateTimeOffset.UnixEpoch)));

	public static readonly Constraint<Campaign> ApprovingAgainTwiceIsApprovingAgainOnce = Constraints<Campaign>.Idempotent(c => c.Reapprove(new Approving(Owner, DateTimeOffset.UnixEpoch)));

	public static readonly Constraint<Campaign> ApprovalsAreTheOwnersOrTheJudges = Constraints<Campaign>.Always(
		c => c.Approval is null || c.Approval.By.StartsWith("owner:", StringComparison.Ordinal) || c.Approval.By.StartsWith("judge:", StringComparison.Ordinal),
		"Only the owner or the judge approves a campaign; an agent never does."
	);

	public static readonly Constraint<Campaign> AJudgedApprovalWaitsAfterItIsGiven = Constraints<Campaign>.Always(
		c => c.Approval is not { NotBefore: { } notBefore } || notBefore >= c.Approval.At,
		"An approval's first moment to go out is never before the approval itself."
	);

	public static readonly Constraint<Campaign> HoldingTwiceIsHoldingOnce = Constraints<Campaign>.Idempotent(c => c.Hold(Owner));
}
