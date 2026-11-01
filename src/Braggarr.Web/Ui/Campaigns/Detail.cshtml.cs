using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Web.Ui.Campaigns;

public sealed class DetailModel(SchedulingService scheduling, CampaignService campaigns, JudgingService judging, Clock clock) : UiPage
{
	public Clock Clock => clock;

	public Campaign? Campaign { get; private set; }

	public Schedule Schedule => scheduling.Current;

	public IReadOnlyList<string> Problems { get; private set; } = [];

	public JudgeState Judge { get; private set; } = JudgeState.Off;

	public async Task<IResult> OnGetAsync(string slug, CancellationToken ct)
	{
		Campaign = scheduling.Current.Campaign(slug);
		if (Campaign is null)
		{
			return NotFound();
		}
		Judge = await judging.StateAsync(ct);
		return Page();
	}

	public Task<IResult> OnPostHoldAsync(string slug, CancellationToken ct) => DecideAsync(slug, campaigns.HoldAsync, ct);

	public Task<IResult> OnPostResumeAsync(string slug, CancellationToken ct) => DecideAsync(slug, campaigns.ResumeAsync, ct);

	public Task<IResult> OnPostReapproveAsync(string slug, CancellationToken ct) => DecideAsync(slug, (s, by, c) => campaigns.ReapproveAsync(s, by, c), ct);

	public async Task<IResult> OnPostThereAsync(string slug, string post, Network network, string url, CancellationToken ct)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var link) || link.Scheme is not ("https" or "http"))
		{
			Problems = ["Paste the link to the post as you see it on the network."];
			return await OnGetAsync(slug, ct);
		}
		await scheduling.ConfirmPublishedAsync(new DeliveryKey(slug, post, network), link.ToString(), ct);
		return Redirect(Forms.Url("/Campaigns/Detail", ("slug", slug)));
	}

	public Task<IResult> OnPostRepeatAsync(string slug, int months, CancellationToken ct) =>
		DecideAsync(slug, (s, by, c) => campaigns.SetRepeatAsync(s, months, by, c), ct);

	public async Task<IResult> OnPostEditAsync(string slug, string post, DateTime at, Dictionary<Network, string>? texts, CancellationToken ct)
	{
		var when = new DateTimeOffset(at, clock.Zone.GetUtcOffset(at));
		if (await campaigns.EditPostAsync(slug, post, when, texts ?? [], Actor.Owner(User.Identity!.Name!), ct) is Outcome<Campaign>.Refused(var reasons))
		{
			Problems = reasons;
			return await OnGetAsync(slug, ct);
		}
		await scheduling.TickAsync(ct);
		return Redirect(Forms.Url("/Campaigns/Detail", ("slug", slug)));
	}

	public PostEdit Editor(Campaign campaign, Post post) => new(campaign, post, clock, "/Campaigns/Detail", new Dictionary<string, string> { ["slug"] = campaign.Slug }, Locked(campaign, post));

	/// <summary>The networks of a post that went out, or are going out: their text is history.</summary>
	public IReadOnlySet<Network> Locked(Campaign campaign, Post post) =>
		post.NetworksIn(campaign).Where(n => Schedule.Find(new DeliveryKey(campaign.Slug, post.Id, n)) is { State: not DeliveryState.Pending }).ToHashSet();

	public async Task<IResult> OnPostPostedAsync(string slug, string post, Network network, CancellationToken ct)
	{
		await scheduling.MarkManualDoneAsync(new DeliveryKey(slug, post, network), ct);
		return Redirect(Forms.Url("/Campaigns/Detail", ("slug", slug)));
	}

	public async Task<IResult> OnPostMissingAsync(string slug, string post, Network network, CancellationToken ct)
	{
		await scheduling.ConfirmMissingAsync(new DeliveryKey(slug, post, network), ct);
		return Redirect(Forms.Url("/Campaigns/Detail", ("slug", slug)));
	}

	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) => (method, handler) switch
	{
		("GET", null) => OnGetAsync(form.Text("slug")!, ct),
		("POST", "hold") => OnPostHoldAsync(form.Text("slug")!, ct),
		("POST", "resume") => OnPostResumeAsync(form.Text("slug")!, ct),
		("POST", "reapprove") => OnPostReapproveAsync(form.Text("slug")!, ct),
		("POST", "there") => OnPostThereAsync(form.Text("slug")!, form.Text("post")!, form.Enum<Network>("network"), form.Text("url")!, ct),
		("POST", "repeat") => OnPostRepeatAsync(form.Text("slug")!, form.Number("months"), ct),
		("POST", "edit") => OnPostEditAsync(form.Text("slug")!, form.Text("post")!, form.DateTime("at"), form.Dictionary<Network>("texts"), ct),
		("POST", "posted") => OnPostPostedAsync(form.Text("slug")!, form.Text("post")!, form.Enum<Network>("network"), ct),
		("POST", "missing") => OnPostMissingAsync(form.Text("slug")!, form.Text("post")!, form.Enum<Network>("network"), ct),
		_ => null
	};

	protected override RazorSlices.RazorSlice Slice() => Detail.Create(this);

	private async Task<IResult> DecideAsync(string slug, Func<string, Actor, CancellationToken, Task<Outcome<Campaign>>> decide, CancellationToken ct)
	{
		if (await decide(slug, Actor.Owner(User.Identity!.Name!), ct) is Outcome<Campaign>.Refused(var reasons))
		{
			Problems = reasons;
			return await OnGetAsync(slug, ct);
		}
		await scheduling.TickAsync(ct);
		return Redirect(Forms.Url("/Campaigns/Detail", ("slug", slug)));
	}
}
