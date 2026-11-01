using Braggarr.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Braggarr.Services;

/// <summary>
/// Judges what changed: every open proposal, and every campaign changed on main since it was approved, that has no verdict for
/// what it says now against the criteria as they are now. The verdict is written next to the campaign. A blocking failure is
/// announced; when the owner lets the judge approve and every criterion passed, it approves, and nothing goes out before the
/// delay the owner set, so there is time to hold it.
/// </summary>
public sealed class JudgingService(
	IJudges judges,
	ICampaignRepository repository,
	IReviews review,
	SchedulingService scheduling,
	CampaignService campaigns,
	DraftingService drafting,
	INotifier notifier,
	IUsageLog usage,
	IOptionsMonitor<JudgingOptions> options,
	IOwnerTime owner,
	TimeProvider time,
	ILogger<JudgingService> logger
)
{
	private string? _unreadable;

	/// <summary>Whether there is a judge, the criteria it judges by and their fingerprint, or why they cannot be read.</summary>
	public async Task<JudgeState> StateAsync(CancellationToken ct)
	{
		if (judges.Current is null)
		{
			return JudgeState.Off;
		}
		try
		{
			var criteria = await repository.CriteriaAsync(ct);
			return new JudgeState(criteria.Count > 0, criteria, criteria.Count > 0 ? Criteria.Digest(criteria) : null, null);
		}
		catch (FormatException e)
		{
			return new JudgeState(true, [], null, e.Message);
		}
	}

	public async Task CheckAsync(CancellationToken ct)
	{
		if (judges.Current is not { } judge)
		{
			return;
		}
		IReadOnlyList<Criterion> criteria;
		try
		{
			criteria = await repository.CriteriaAsync(ct);
		}
		catch (FormatException e)
		{
			// Said once per problem, not every minute; nothing is judged or approved by the judge until it is fixed.
			if (_unreadable != e.Message)
			{
				_unreadable = e.Message;
				await notifier.NotifyAsync(new Notice(NoticeLevel.Action, "The judge's criteria cannot be read", e.Message), ct);
			}
			return;
		}
		_unreadable = null;
		if (criteria.Count == 0)
		{
			return;
		}
		var version = Criteria.Digest(criteria);
		var voice = await repository.VoiceAsync(ct);

		foreach (var proposal in await review.OpenAsync(ct))
		{
			// Text still being written is judged once it is all there, not paid for twice.
			if (!proposal.Campaign.IsComplete || proposal.Campaign.JudgedAsIs(version) || await JudgeAsync(judge, proposal.Campaign, criteria, version, voice, ct) is not { } judgement)
			{
				continue;
			}
			var judged = await review.UpdateAsync(proposal, proposal.Campaign with { Judgement = judgement }, Actor.Judge, ct);
			await AfterAsync(judged.Campaign, judgement, async notBefore => await campaigns.ApproveAsync(judged.Number, Actor.Judge, ct, notBefore) is Outcome<Proposal>.Done, judged.Url, ct);
		}

		foreach (var campaign in scheduling.Current.Campaigns.Where(c => c.NeedsApproval && c.IsComplete && !c.JudgedAsIs(version)).ToList())
		{
			if (await JudgeAsync(judge, campaign, criteria, version, voice, ct) is not { } judgement)
			{
				continue;
			}
			await repository.SetJudgementAsync(campaign.Slug, judgement, ct);
			await scheduling.RefreshAsync(ct);
			await AfterAsync(campaign with { Judgement = judgement }, judgement, async notBefore => await campaigns.ReapproveAsync(campaign.Slug, Actor.Judge, ct, notBefore) is Outcome<Campaign>.Done, null, ct);
		}
	}

	/// <summary>
	/// A verdict for every criterion and variant it applies to: what the model did not say about one is a failure, and whether
	/// a failure blocks is the criterion's to say, never the model's.
	/// </summary>
	private async Task<Judgement?> JudgeAsync(IJudge judge, Campaign campaign, IReadOnlyList<Criterion> criteria, string version, string? voice, CancellationToken ct)
	{
		IReadOnlyList<Finding> said;
		try
		{
			(said, var cost) = await judge.JudgeAsync(new JudgeRequest(campaign, criteria, voice) { About = await drafting.AboutAsync(campaign.Project, ct) }, ct);
			usage.Record(cost, $"judge {campaign.Slug}", time.GetUtcNow());
		}
		catch (DraftException e)
		{
			logger.LogWarning("Judging {Slug} failed; it is tried again on the next check: {Problem}", campaign.Slug, e.Message);
			return null;
		}
		var findings = Criteria.Expected(campaign, criteria)
			.Select(e => said.FirstOrDefault(f => f.Criterion == e.Criterion.Name && f.Post == e.Post && f.Network == e.Network) is { } f
				? f with { Blocks = e.Criterion.Blocks }
				: new Finding(e.Criterion.Name, e.Post, e.Network, Passed: false, e.Criterion.Blocks, "The judge said nothing about this, so it does not pass."))
			.ToValueList();
		return new Judgement(campaign.Digest, version, time.GetUtcNow(), judge.Model, findings);
	}

	private async Task AfterAsync(Campaign campaign, Judgement judgement, Func<DateTimeOffset, Task<bool>> approve, string? url, CancellationToken ct)
	{
		if (judgement.Blocked)
		{
			var failed = judgement.Findings.Where(f => !f.Passed && f.Blocks).Select(f => f.Criterion).Distinct();
			await notifier.NotifyAsync(new Notice(NoticeLevel.Action, $"The judge blocks {campaign.Title}", $"Failed: {string.Join(", ", failed)}.", url), ct);
			return;
		}
		if (!judgement.AllPassed)
		{
			var warned = judgement.Findings.Where(f => !f.Passed).Select(f => f.Criterion).Distinct();
			await notifier.NotifyAsync(new Notice(NoticeLevel.Action, $"The judge has warnings on {campaign.Title}", $"Warned: {string.Join(", ", warned)}.", url), ct);
			return;
		}
		if (!options.CurrentValue.AutoApprove)
		{
			return;
		}
		var notBefore = time.GetUtcNow() + options.CurrentValue.Delay;
		if (await approve(notBefore))
		{
			var local = TimeZoneInfo.ConvertTime(notBefore, owner.Zone);
			await notifier.NotifyAsync(
				new Notice(
					NoticeLevel.Action,
					$"The judge approved {campaign.Title}",
					$"Every criterion passed. Nothing goes out before {local:ddd d MMM HH:mm}: hold it before then if you disagree.",
					url
				),
				ct
			);
		}
	}
}

/// <summary>
/// What the pages show about the judge: <see cref="On"/> when there is a judge and something to judge by (or criteria that
/// cannot be read, which stop approval), and the fingerprint a verdict must carry to be current.
/// </summary>
public sealed record JudgeState(bool On, IReadOnlyList<Criterion> Criteria, string? Version, string? Problem)
{
	public static JudgeState Off { get; } = new(false, [], null, null);

	public bool IsCurrent(Campaign campaign) => Version is { } version && campaign.JudgedAsIs(version);
}
