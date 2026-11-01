namespace Braggarr.Domain;

/// <summary>What a decision about a campaign changed, and who decided it. The history the approval rules are checked against.</summary>
public abstract record CampaignEvent(string Slug, Actor By)
{
	/// <summary>Approved, or approved again after an edit: the stamp says what was approved, when, and from when it may go out.</summary>
	public sealed record Approved(string Slug, Actor By, Approval Stamp) : CampaignEvent(Slug, By);

	public sealed record Rejected(string Slug, Actor By) : CampaignEvent(Slug, By);

	public sealed record Held(string Slug, Actor By) : CampaignEvent(Slug, By);

	public sealed record Resumed(string Slug, Actor By) : CampaignEvent(Slug, By);
}

public enum CampaignDecision
{
	Approve,
	Reject,
	Hold,
	Resume,

	/// <summary>Approve again what changed on main since it was approved.</summary>
	Reapprove
}

/// <summary>A request to change a campaign's status, from someone, at a time; an approval by the judge says when it may go out.</summary>
public sealed record CampaignCommand(CampaignDecision Decision, Actor By)
{
	public DateTimeOffset At { get; init; }

	public DateTimeOffset? NotBefore { get; init; }
}

/// <summary>Decide and evolve: what a command does to a campaign, as events, and the campaign those events lead to.</summary>
public static class CampaignDecider
{
	public static IReadOnlyList<CampaignEvent> Decide(Campaign campaign, CampaignCommand command)
	{
		var next = Apply(campaign, command);
		if (next.Status == campaign.Status && next.Approval == campaign.Approval)
		{
			return [];
		}
		CampaignEvent change = next.Status switch
		{
			// Approved again on main: the status stays, the stamp is new.
			CampaignStatus.Approved or CampaignStatus.Held when next.Approval != campaign.Approval => new CampaignEvent.Approved(campaign.Slug, command.By, next.Approval!),
			CampaignStatus.Approved when campaign.Status == CampaignStatus.Held => new CampaignEvent.Resumed(campaign.Slug, command.By),
			CampaignStatus.Rejected => new CampaignEvent.Rejected(campaign.Slug, command.By),
			CampaignStatus.Held => new CampaignEvent.Held(campaign.Slug, command.By),
			_ => throw new InvalidOperationException($"No event moves a campaign to {next.Status}.")
		};
		return [change];
	}

	public static Campaign Evolve(Campaign campaign, CampaignEvent change) => change switch
	{
		CampaignEvent.Approved approved => campaign with
		{
			Status = campaign.Status == CampaignStatus.Held ? CampaignStatus.Held : CampaignStatus.Approved,
			Approval = approved.Stamp
		},
		CampaignEvent.Resumed => campaign with { Status = CampaignStatus.Approved },
		CampaignEvent.Rejected => campaign with { Status = CampaignStatus.Rejected },
		CampaignEvent.Held => campaign with { Status = CampaignStatus.Held },
		_ => campaign
	};

	private static Campaign Apply(Campaign campaign, CampaignCommand command) => command.Decision switch
	{
		CampaignDecision.Approve => campaign.Approve(new Approving(command.By, command.At) { NotBefore = command.NotBefore }),
		CampaignDecision.Reapprove => campaign.Reapprove(new Approving(command.By, command.At) { NotBefore = command.NotBefore }),
		CampaignDecision.Reject => campaign.Reject(command.By),
		CampaignDecision.Hold => campaign.Hold(command.By),
		CampaignDecision.Resume => campaign.Resume(command.By),
		_ => campaign
	};
}
