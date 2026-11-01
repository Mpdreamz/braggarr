using Braggarr.Domain;

namespace Braggarr.Services;

/// <summary>
/// Proposes an introduction campaign for a project: Claude reads the repository and ranks its talking points (what it is first,
/// then the features that deserve their own post), each with the media it wants, and the posts are laid out from them from ten
/// the next morning. It is a proposal from <c>introducer</c>, waiting for the owner's review. Without a model, it has one point
/// from the description.
/// </summary>
public sealed class Introducer(IRepositories repositories, DraftingService drafting, CampaignService campaigns, IOwnerTime owner, TimeProvider time)
{
	public static readonly Actor Agent = Actor.Agent("introducer");

	public async Task<Outcome<Proposal>> ProposeAsync(string repository, string name, IReadOnlyList<Network> networks, CancellationToken ct)
	{
		var info = await repositories.ReadAsync(repository, name, ct);
		var plan = await drafting.PlanAsync(info, ct) is { Points.Count: > 0 } planned ? planned : Fallback(info);

		var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), owner.Zone);
		var launch = local.Date.AddDays(1).AddHours(CampaignService.Hour);

		var links = new List<string>();
		if (!string.IsNullOrWhiteSpace(info.Homepage))
		{
			links.Add(info.Homepage);
		}
		links.Add($"https://github.com/{repository}");

		return await campaigns.ProposeAsync(
			new CampaignDraft(Slugs.From($"{name} introduction"), plan.Title, name, networks, links, plan.Brief, [])
			{
				Points = [.. plan.Points.Where(p => !string.IsNullOrWhiteSpace(p.Text)).Take(DraftingService.MaxPoints)],
				Start = new DateTimeOffset(launch, owner.Zone.GetUtcOffset(launch))
			},
			Agent,
			ct
		);
	}

	private static CampaignPlan Fallback(RepositoryInfo info) =>
		new(
			$"Introducing {info.Name}",
			$"{info.Description}\n\n{(info.Readme.Length > 1500 ? info.Readme[..1500] : info.Readme)}",
			[new TalkingPoint(string.IsNullOrWhiteSpace(info.Description) ? $"What {info.Name} is and who it is for" : info.Description.Trim())]
		);
}
