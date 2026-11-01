using System.Globalization;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Web.Ui.Campaigns;

/// <summary>
/// A campaign from the owner: the facts and the times. Claude writes the variants when it is set up; either way it becomes a
/// proposal like any other, reviewed on its own page before anything goes out.
/// </summary>
public sealed class NewModel(CampaignService campaigns, DraftingService drafting, Clock clock) : UiPage
{
	public bool CanDraft => drafting.IsAvailable;

	public IReadOnlyList<string> Problems { get; private set; } = [];

	public string Title { get; set; } = "";
	public string? Project { get; set; } = "";
	public string? Links { get; set; }
	public string? Brief { get; set; }
	public List<Network> Networks { get; set; } = [Network.Bluesky, Network.Mastodon];

	/// <summary>One time per line, in the owner's time zone: <c>2026-11-03 10:00</c>.</summary>
	public string? Times { get; set; } = "";

	/// <summary>Months after the last post to propose it again, written fresh; 0 never.</summary>
	public int RepeatMonths { get; set; }

	public string Example => clock.Now.AddDays(1).ToString("yyyy-MM-dd 10:00", CultureInfo.InvariantCulture);

	public void OnGet() => Times = Example;

	/// <summary>
	/// Whether a posted field fails the check model validation made of it: a non-nullable property is required, so one posted
	/// blank, or not at all, is invalid, as is a number that is not one.
	/// </summary>
	public bool Invalid(string name) => Bound is { } posted && (name == nameof(RepeatMonths) ? posted.OptionalNumber(name) is null : posted.Text(name) is null);

	protected internal override void Bind(Binder form)
	{
		if (form.Has(nameof(Title)))
		{
			Title = form.Text(nameof(Title))!;
		}
		if (form.Has(nameof(Project)))
		{
			Project = form.Text(nameof(Project));
		}
		if (form.Has(nameof(Links)))
		{
			Links = form.Text(nameof(Links));
		}
		if (form.Has(nameof(Brief)))
		{
			Brief = form.Text(nameof(Brief));
		}
		if (form.Has(nameof(Networks)))
		{
			Networks = form.EnumList<Network>(nameof(Networks));
		}
		if (form.Has(nameof(Times)))
		{
			Times = form.Text(nameof(Times));
		}
		if (form.Has(nameof(RepeatMonths)) && form.OptionalNumber(nameof(RepeatMonths)) is { } months)
		{
			RepeatMonths = months;
		}
	}

	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) => (method, handler) switch
	{
		("GET", null) => Shown(OnGet),
		("POST", null) => OnPostAsync(ct),
		_ => null
	};

	protected override RazorSlices.RazorSlice Slice() => New.Create(this);

	public async Task<IResult> OnPostAsync(CancellationToken ct)
	{
		var times = new List<DateTimeOffset>();
		foreach (var line in (Times ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (!DateTime.TryParseExact(line, ["yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
			{
				Problems = [$"\"{line}\" is not a time like {Example}."];
				return Page();
			}
			times.Add(new DateTimeOffset(local, clock.Zone.GetUtcOffset(local)));
		}
		if (times.Count == 0 || Networks.Count == 0 || string.IsNullOrWhiteSpace(Title) || string.IsNullOrWhiteSpace(Project))
		{
			Problems = ["A campaign needs a title, a project, at least one network and at least one time."];
			return Page();
		}

		var draft = new CampaignDraft(
			null, Title, Project, Networks,
			[.. (Links ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
			Brief ?? "",
			[.. times.Order().Select((t, i) => new PostDraft(i == 0 ? "01-launch" : $"{i + 1:00}-follow-up", t, new Dictionary<Network, string>()))]
		)
		{
			RepeatMonths = RepeatMonths
		};
		switch (await campaigns.ProposeAsync(draft, Actor.Owner(User.Identity!.Name!), ct))
		{
			case Outcome<Proposal>.Done(var proposal):
				return Redirect($"/Campaigns/Proposal?number={proposal.Number}");
			case Outcome<Proposal>.Refused(var reasons):
				Problems = reasons;
				return Page();
			default:
				return Page();
		}
	}
}
