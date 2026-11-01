using Braggarr.Domain;
using Braggarr.Services;
using Braggarr.Web.Settings;

namespace Braggarr.Web.Ui;

/// <summary>One post on the calendar: of an approved or held campaign, or of a proposal still waiting for review.</summary>
public sealed record CalendarSlot(Campaign Campaign, Post Post, DateTimeOffset Local, IReadOnlyList<(Network Network, string State)> Networks, int? Proposal)
{
	public bool InReview => Proposal is not null;

	public string Kind =>
		InReview ? "review"
		: Campaign.Status == CampaignStatus.Held ? "held"
		: Networks.All(n => n.State == "done") ? "done"
		: Networks.Any(n => n.State == "problem") ? "problem"
		: "waiting";

	public string Href => Proposal is { } number ? $"/Campaigns/Proposal?number={number}" : $"/Campaigns/Detail?slug={Campaign.Slug}";
}

/// <summary>
/// A month in the owner's time zone, as whole weeks from Monday, with every post at its time: what is planned, and what is
/// proposed and waiting for review.
/// </summary>
public sealed class CalendarModel(SchedulingService scheduling, CampaignService campaigns, SettingsStore settings, Clock clock) : UiPage
{
	public Clock Clock => clock;

	public int Month { get; private set; }

	/// <summary>The first of the month shown.</summary>
	public DateTime First { get; private set; }

	public IReadOnlyList<IReadOnlyList<(DateTime Day, IReadOnlyList<CalendarSlot> Slots)>> Weeks { get; private set; } = [];

	/// <summary>The rebalance dialog is open: asked for, or showing a preview.</summary>
	public bool Rebalancing { get; private set; }

	public RebalanceSettings Options { get; private set; } = new();

	public RebalancePlan? Preview { get; private set; }

	public string? Message { get; private set; }

	public IReadOnlyList<string> Problems { get; private set; } = [];

	public async Task OnGetAsync(int month, bool rebalance, CancellationToken ct)
	{
		Rebalancing = rebalance;
		Options = settings.Current.Rebalance;
		await LoadAsync(month, ct);
	}

	/// <summary>Shows what a rebalance would move, and keeps the options for next time.</summary>
	public async Task<IResult> OnPostPreviewAsync(int month, List<string>? rest, int perDay, string from, string to, CancellationToken ct)
	{
		Options = Read(rest, perDay, from, to);
		Rebalancing = true;
		Preview = await campaigns.PlanRebalanceAsync(Options.ToOptions(), ct);
		Problems = Preview.Problems;
		if (Problems.Count == 0)
		{
			_ = settings.Update(s => s with { Rebalance = Options });
		}
		await LoadAsync(month, ct);
		return Page();
	}

	public async Task<IResult> OnPostApplyAsync(int month, List<string>? rest, int perDay, string from, string to, CancellationToken ct)
	{
		Options = Read(rest, perDay, from, to);
		switch (await campaigns.RebalanceAsync(Options.ToOptions(), Actor.Owner(User.Identity!.Name!), ct))
		{
			case Outcome<RebalanceResult>.Done(var result):
				Message = result.Moved == 0 ? "Nothing needed to move." : $"Moved {result.Moved} {(result.Moved == 1 ? "post" : "posts")}."
					+ (result.ApprovedAgain.Count > 0 ? $" Approved again for their new times: {string.Join(", ", result.ApprovedAgain)}." : "")
					+ (result.NeedApproving.Count > 0 ? $" These need approving again once the judge has read them: {string.Join(", ", result.NeedApproving)}." : "");
				break;
			case Outcome<RebalanceResult>.Refused(var reasons):
				Problems = reasons;
				Rebalancing = true;
				break;
		}
		await LoadAsync(month, ct);
		return Page();
	}

	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) => (method, handler) switch
	{
		("GET", null) => Shown(OnGetAsync(form.Number("month"), form.Bool("rebalance"), ct)),
		("POST", "preview") => OnPostPreviewAsync(form.Number("month"), form.StringList("rest"), form.Number("perDay"), form.Text("from")!, form.Text("to")!, ct),
		("POST", "apply") => OnPostApplyAsync(form.Number("month"), form.StringList("rest"), form.Number("perDay"), form.Text("from")!, form.Text("to")!, ct),
		_ => null
	};

	protected override RazorSlices.RazorSlice Slice() => Calendar.Create(this);

	private static RebalanceSettings Read(List<string>? rest, int perDay, string from, string to) =>
		new() { RestDays = [.. (rest ?? []).Where(d => Enum.TryParse<DayOfWeek>(d, out _)).Distinct()], PerDay = perDay, From = from, To = to };

	private async Task LoadAsync(int month, CancellationToken ct)
	{
		Month = month;
		var today = clock.Now.Date;
		First = new DateTime(today.Year, today.Month, 1).AddMonths(month);
		var start = First.AddDays(-(((int)First.DayOfWeek + 6) % 7));
		var last = First.AddMonths(1).AddDays(-1);
		var end = last.AddDays(6 - (((int)last.DayOfWeek + 6) % 7)).AddDays(1);

		var schedule = scheduling.Current;
		var planned =
			from c in schedule.Campaigns
			where c.Status is CampaignStatus.Approved or CampaignStatus.Held
			from p in c.Posts
			select new CalendarSlot(c, p, clock.Local(p.At), [.. p.NetworksIn(c).Select(n => (n, CampaignCard.StateOf(schedule.Find(new DeliveryKey(c.Slug, p.Id, n)))))], null);
		var proposed =
			from proposal in await campaigns.ProposalsAsync(ct)
			from p in proposal.Campaign.Posts
			select new CalendarSlot(proposal.Campaign, p, clock.Local(p.At), [.. p.NetworksIn(proposal.Campaign).Select(n => (n, "review"))], proposal.Number);
		var slots = planned.Concat(proposed).Where(s => s.Local.Date >= start && s.Local.Date < end).OrderBy(s => s.Local).ToList();

		Weeks =
		[
			.. Enumerable.Range(0, (int)(end - start).TotalDays / 7).Select(w =>
				(IReadOnlyList<(DateTime, IReadOnlyList<CalendarSlot>)>)
				[
					.. Enumerable.Range(0, 7).Select(d => start.AddDays((7 * w) + d)).Select(day => (day, (IReadOnlyList<CalendarSlot>)[.. slots.Where(s => s.Local.Date == day)]))
				])
		];
	}
}
