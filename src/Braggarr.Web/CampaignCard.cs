using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Web;

/// <summary>A campaign as a poster on the campaigns page: until there is artwork, a colour from its slug and its initials.</summary>
public sealed record CampaignCard(
	string Href,
	string Title,
	string Project,
	string State,
	string StateLabel,
	string Meta,
	int Done,
	int Total,
	IReadOnlyList<(Network Network, string State)> Networks
)
{
	public int Hue => (int)(System.IO.Hashing.XxHash32.HashToUInt32(System.Text.Encoding.UTF8.GetBytes(Project + Title)) % 12);

	/// <summary>Two letters: the first of two words ("Weird Fish" is WF), or the first two of one ("curb" is Cu), so heed and hunch differ.</summary>
	public string Initials
	{
		get
		{
			var words = (Project.Length > 0 ? Project : Title).Split(' ', '-', '.', '_').Where(w => w.Length > 0).ToArray();
			return words switch
			{
				[] => "?",
				[var one] => one.Length > 1 ? $"{char.ToUpperInvariant(one[0])}{char.ToLowerInvariant(one[1])}" : one.ToUpperInvariant(),
				[var first, var second, ..] => $"{char.ToUpperInvariant(first[0])}{char.ToUpperInvariant(second[0])}"
			};
		}
	}

	/// <summary>Progress in steps of ten percent, for a width class (the page's CSP allows no inline styles).</summary>
	public int ProgressStep => Total == 0 ? 0 : (int)Math.Round(10.0 * Done / Total);

	public static string StateOf(Delivery? d) => d?.State switch
	{
		DeliveryState.Published or DeliveryState.ManualDone => "done",
		DeliveryState.Failed or DeliveryState.Unknown => "problem",
		DeliveryState.ManualDue or DeliveryState.Sending => "now",
		_ => "waiting"
	};

	public static CampaignCard From(Campaign c, Schedule schedule, Clock clock)
	{
		var deliveries = schedule.Deliveries.Where(d => d.Key.Campaign == c.Slug).ToList();
		var done = deliveries.Count(d => d.State is DeliveryState.Published or DeliveryState.ManualDone);
		var next = c.Posts.Where(p => p.At > clock.Now).OrderBy(p => p.At).FirstOrDefault();
		var (state, label) = c.Status switch
		{
			CampaignStatus.Held => ("held", "Held"),
			_ when deliveries.Any(d => d.State is DeliveryState.Unknown or DeliveryState.Failed or DeliveryState.ManualDue) => ("attention", "Needs you"),
			_ when deliveries.Count > 0 && done == deliveries.Count => ("done", "Done"),
			_ => ("live", "Scheduled")
		};
		var networks = c.Networks.Select(n => (n, StateOf(schedule.Find(new DeliveryKey(c.Slug, c.Posts.Count > 0 ? (next ?? c.Posts[^1]).Id : "", n))))).ToList();
		return new($"/Campaigns/Detail?slug={c.Slug}", c.Title, c.Project, state, label,
			next is null ? $"{done} of {deliveries.Count} posted" : $"Next {clock.Full(next.At)}", done, deliveries.Count, networks);
	}

	public static CampaignCard From(Proposal p, Clock clock)
	{
		var c = p.Campaign;
		var first = c.Posts.OrderBy(x => x.At).FirstOrDefault();
		return new($"/Campaigns/Proposal?number={p.Number}", c.Title, c.Project, c.IsReady ? "proposed" : "incomplete", c.IsReady ? "Review" : "Incomplete",
			first is null ? $"#{p.Number} · by {c.CreatedBy}" : $"#{p.Number} · {clock.Full(first.At)}", 0, c.Posts.Sum(x => x.NetworksIn(c).Count),
			[.. c.Networks.Select(n => (n, "waiting"))]);
	}
}
