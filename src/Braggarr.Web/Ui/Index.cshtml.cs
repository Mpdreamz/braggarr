using Braggarr.Domain;
using Braggarr.Services;
using Braggarr.Web.Hosting;

namespace Braggarr.Web.Ui;

/// <summary>The campaigns, as posters: proposals waiting for review first, then the scheduled ones, newest first.</summary>
public sealed class IndexModel(SchedulingService scheduling, CampaignService campaigns, SchedulerHost host, Clock clock) : UiPage
{
	public IReadOnlyList<CampaignCard> Proposals { get; private set; } = [];

	public IReadOnlyList<CampaignCard> Live { get; private set; } = [];

	public IReadOnlyList<(Campaign Campaign, Delivery Delivery)> NeedsYou { get; private set; } = [];

	public string? Problem { get; private set; }

	public string? Filter { get; private set; }

	public async Task OnGetAsync(string? show, CancellationToken ct)
	{
		Filter = show;
		var schedule = scheduling.Current;
		NeedsYou =
		[
			.. from d in schedule.Deliveries
			where d.State is DeliveryState.Unknown or DeliveryState.ManualDue || (d.State == DeliveryState.Failed && d.IsSettled)
			let c = schedule.Campaign(d.Key.Campaign)
			where c is not null
			select (c, d)
		];
		var live = schedule.Campaigns
			.OrderByDescending(c => c.Posts.Count > 0 ? c.Posts.Max(p => p.At) : DateTimeOffset.MinValue)
			.Select(c => CampaignCard.From(c, schedule, clock));
		Live = [.. show switch { "held" => live.Where(c => c.State == "held"), "done" => live.Where(c => c.State == "done"), "active" => live.Where(c => c.State is not ("done" or "held")), _ => live }];
		try
		{
			Proposals = [.. (await campaigns.ProposalsAsync(ct)).Select(p => CampaignCard.From(p, clock))];
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			Problem = $"The proposals could not be read from GitHub: {e.Message}";
		}
		Problem ??= host.LastProblem is { } p ? $"The scheduler could not finish its last run: {p}" : null;
	}

	public IResult OnPostSync()
	{
		scheduling.RequestSync();
		return Redirect("/");
	}

	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) => (method, handler) switch
	{
		("GET", null) => Shown(OnGetAsync(form.Text("show"), ct)),
		("POST", "sync") => Task.FromResult(OnPostSync()),
		_ => null
	};

	protected override RazorSlices.RazorSlice Slice() => Index.Create(this);
}
