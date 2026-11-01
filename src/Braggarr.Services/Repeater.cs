using Braggarr.Domain;
using Microsoft.Extensions.Logging;

namespace Braggarr.Services;

/// <summary>
/// Proposes a campaign again once its repeat is due: a new campaign about the same thing, for ten the next morning, written fresh
/// by Claude with the earlier posts shown as what not to repeat. The copy does not repeat itself; the original records the date,
/// so a rejected copy is not proposed again until the next interval.
/// </summary>
public sealed class Repeater(SchedulingService scheduling, CampaignService campaigns, ICampaignRepository repository, IOwnerTime owner, TimeProvider time, ILogger<Repeater> logger)
{
	public static readonly Actor Agent = Actor.Agent("repeater");

	public async Task<IReadOnlyList<Proposal>> CheckAsync(CancellationToken ct)
	{
		var now = time.GetUtcNow();
		var proposed = new List<Proposal>();
		foreach (var campaign in scheduling.Current.RepeatsDue(now))
		{
			var outcome = await campaigns.ProposeAsync(Draft(campaign, now), Agent, ct);
			if (outcome is Outcome<Proposal>.Done(var proposal))
			{
				proposed.Add(proposal);
			}
			else if (outcome is Outcome<Proposal>.Refused(var reasons))
			{
				logger.LogWarning("{Slug} was not proposed again: {Reasons}", campaign.Slug, string.Join(" ", reasons));
			}
			// Either way this interval is done; the next one counts from now.
			await repository.MarkRepeatedAsync(campaign.Slug, now, ct);
		}
		if (proposed.Count > 0)
		{
			scheduling.RequestSync();
		}
		return proposed;
	}

	private CampaignDraft Draft(Campaign campaign, DateTimeOffset now)
	{
		var local = TimeZoneInfo.ConvertTime(now, owner.Zone);
		var morning = local.Date.AddDays(1).AddHours(CampaignService.Hour);
		var earlier = string.Join("\n", campaign.Posts.SelectMany(p => p.Variants).Select(v => $"- {v.Network}: {v.Text.Replace('\n', ' ')}").Distinct().Take(14));
		var brief =
			$"{campaign.Brief}\n\nThis ran {campaign.RepeatMonths} months ago or more. Write it fresh for people who missed it or forgot: a new angle, "
			+ $"what has happened since if the links show it, never the same sentences. The earlier posts, not to repeat:\n{earlier}";
		var stem = campaign.Slug.Length > 50 ? campaign.Slug[..50].TrimEnd('-') : campaign.Slug;
		return new CampaignDraft(
			$"{stem}-again-{local:yyyy-MM}",
			campaign.Title,
			campaign.Project,
			campaign.Networks,
			campaign.Links,
			brief,
			[]
		)
		{ Start = new DateTimeOffset(morning, owner.Zone.GetUtcOffset(morning)) };
	}
}
