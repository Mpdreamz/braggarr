using Braggarr.Adapters;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Web.Ui;

public sealed record ActivityRow(Campaign? Campaign, Delivery Delivery, DateTimeOffset? At);

/// <summary>Queue: what goes out in the next week, and what needs you. History: what went out. Events: what happened since start.</summary>
public sealed class ActivityModel(SchedulingService scheduling, InMemoryJournal journal, Clock clock) : UiPage
{
	public Clock Clock => clock;

	public string Tab { get; private set; } = "queue";

	public IReadOnlyList<ActivityRow> Rows { get; private set; } = [];

	public IReadOnlyList<(DateTimeOffset At, DeliveryEvent Event)> Events { get; private set; } = [];

	public void OnGet(string? tab)
	{
		Tab = tab is "history" or "events" ? tab : "queue";
		var schedule = scheduling.Current;
		DateTimeOffset? When(Delivery d) => schedule.Campaign(d.Key.Campaign)?.Posts.FirstOrDefault(p => p.Id == d.Key.Post)?.At;
		var horizon = clock.Now.AddDays(7);
		Rows = Tab switch
		{
			"history" =>
			[
				.. from d in schedule.Deliveries
				where d.State is DeliveryState.Published or DeliveryState.ManualDone
				orderby d.LastAttempt ?? When(d) descending
				select new ActivityRow(schedule.Campaign(d.Key.Campaign), d, d.LastAttempt ?? When(d))
			],
			_ =>
			[
				.. from d in schedule.Deliveries
				let at = When(d)
				where d.State is DeliveryState.Unknown or DeliveryState.ManualDue or DeliveryState.Sending or DeliveryState.Failed
					|| (d.State == DeliveryState.Pending && at <= horizon)
				orderby d.State is DeliveryState.Pending ? 1 : 0, at
				select new ActivityRow(schedule.Campaign(d.Key.Campaign), d, at)
			]
		};
		Events = Tab == "events" ? [.. journal.Recent().Take(200)] : [];
	}

	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) => (method, handler) switch
	{
		("GET", null) => Shown(() => OnGet(form.Text("tab"))),
		_ => null
	};

	protected override RazorSlices.RazorSlice Slice() => Activity.Create(this);
}
