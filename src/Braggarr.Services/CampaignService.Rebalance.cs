using Braggarr.Domain;

namespace Braggarr.Services;

/// <summary>One post a rebalance moves, with what the owner needs to decide on it.</summary>
public sealed record RebalanceItem(PostMove Move, string Title, bool InReview, bool StaysApproved);

/// <summary>What a rebalance would move, or why it cannot plan.</summary>
public sealed record RebalancePlan(IReadOnlyList<RebalanceItem> Items, IReadOnlyList<string> Problems);

/// <summary>What applying a rebalance did.</summary>
public sealed record RebalanceResult(int Moved, IReadOnlyList<string> ApprovedAgain, IReadOnlyList<string> NeedApproving);

public sealed partial class CampaignService
{
	/// <summary>
	/// What a rebalance with these options would move: every post of an open proposal or a campaign on main that has not gone
	/// out. A post of which anything went out stays, and its place counts.
	/// </summary>
	public async Task<RebalancePlan> PlanRebalanceAsync(RebalanceOptions options, CancellationToken ct)
	{
		if (options.Problems.Count > 0)
		{
			return new RebalancePlan([], options.Problems);
		}
		var (proposals, onMain) = await RebalanceablesAsync(ct);
		var all = proposals.Select(p => new Rebalanceable(p.Campaign, new HashSet<string>())).Concat(onMain).ToList();
		var moves = Rebalancer.Plan(all, options, time.GetUtcNow(), owner.Zone);
		var judged = await JudgeGatesAsync(ct);
		return new RebalancePlan(
			[
				.. moves.Select(m =>
				{
					var campaign = all.First(c => c.Campaign.Slug == m.Campaign).Campaign;
					var inReview = proposals.Any(p => p.Campaign.Slug == m.Campaign);
					return new RebalanceItem(m, campaign.Title, inReview, !inReview && campaign.IsApproved && !judged);
				})
			],
			[]
		);
	}

	/// <summary>
	/// Moves what <see cref="PlanRebalanceAsync"/> plans, as the owner: on a proposal's branch, or on main. A campaign that was
	/// approved is approved again by the owner for its new times, when nothing stands in the way (the judge must read a
	/// changed campaign first when there are criteria); the result says which still need approving.
	/// </summary>
	public async Task<Outcome<RebalanceResult>> RebalanceAsync(RebalanceOptions options, Actor by, CancellationToken ct)
	{
		if (by.Kind != ActorKind.Owner)
		{
			return new Outcome<RebalanceResult>.Refused("Only the owner rebalances the calendar.");
		}
		if (options.Problems.Count > 0)
		{
			return new Outcome<RebalanceResult>.Refused(options.Problems);
		}
		var (proposals, onMain) = await RebalanceablesAsync(ct);
		var all = proposals.Select(p => new Rebalanceable(p.Campaign, new HashSet<string>())).Concat(onMain).ToList();
		var moves = Rebalancer.Plan(all, options, time.GetUtcNow(), owner.Zone);
		var again = new List<string>();
		var waiting = new List<string>();
		foreach (var slug in moves.Select(m => m.Campaign).Distinct())
		{
			if (proposals.FirstOrDefault(p => p.Campaign.Slug == slug) is { } proposal)
			{
				_ = await review.UpdateAsync(proposal, Rebalancer.Apply(proposal.Campaign, moves), by, ct);
				continue;
			}
			var campaign = onMain.First(c => c.Campaign.Slug == slug).Campaign;
			var moved = Rebalancer.Apply(campaign, moves);
			foreach (var post in moved.Posts.Where(p => moves.Any(m => m.Campaign == slug && m.Post == p.Id)))
			{
				await repository.UpdatePostAsync(slug, post, by, ct);
			}
			if (!campaign.IsApproved)
			{
				continue;
			}
			switch (await ReapproveAsync(moved, by, campaign.Approval?.NotBefore, ct))
			{
				case Outcome<Campaign>.Done:
					again.Add(campaign.Title);
					break;
				default:
					waiting.Add(campaign.Title);
					break;
			}
		}
		scheduling.RequestSync();
		return new Outcome<RebalanceResult>.Done(new RebalanceResult(moves.Count, again, waiting));
	}

	private async Task<(IReadOnlyList<Proposal> Proposals, IReadOnlyList<Rebalanceable> OnMain)> RebalanceablesAsync(CancellationToken ct)
	{
		var schedule = scheduling.Current;
		var proposals = await review.OpenAsync(ct);
		var onMain = schedule.Campaigns
			.Where(c => c.Status is CampaignStatus.Approved or CampaignStatus.Held)
			.Select(c => new Rebalanceable(c, c.Posts
				.Where(p => p.NetworksIn(c).Any(n => schedule.Find(new DeliveryKey(c.Slug, p.Id, n)) is { State: not DeliveryState.Pending }))
				.Select(p => p.Id)
				.ToHashSet()))
			.ToList();
		return (proposals, onMain);
	}

	/// <summary>Whether a changed campaign must wait for the judge before it can be approved again: there is a judge and criteria.</summary>
	private async Task<bool> JudgeGatesAsync(CancellationToken ct)
	{
		try
		{
			return judges.Current is not null && (await repository.CriteriaAsync(ct)).Count > 0;
		}
		catch (FormatException)
		{
			return true;
		}
	}
}
