using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Web.Ui.Campaigns;

/// <summary>A proposal under review. A video is uploaded through the page too, so its endpoint takes up to 100 MB (see <see cref="UiPages"/>).</summary>
public sealed class ProposalModel(CampaignService campaigns, DraftingService drafting, JudgingService judging, IAccounts accounts, Clock clock) : UiPage
{
	public Clock Clock => clock;

	public Proposal? Proposal { get; private set; }

	public IReadOnlyList<string> Problems { get; private set; } = [];

	public bool CanDraft => drafting.IsAvailable;

	public JudgeState Judge { get; private set; } = JudgeState.Off;

	/// <summary>The accounts connected on a network, to choose from.</summary>
	public IReadOnlyList<string> AccountsOn(Network network) => accounts.Connected(network);

	public async Task<IResult> OnPostAccountsAsync(int number, Dictionary<Network, string>? account, CancellationToken ct)
	{
		var chosen = (account ?? []).Where(a => !string.IsNullOrWhiteSpace(a.Value)).ToDictionary(a => a.Key, a => a.Value.Trim());
		if (await campaigns.ChooseAccountsAsync(number, chosen, Actor.Owner(User.Identity!.Name!), ct) is Outcome<Proposal>.Refused(var reasons))
		{
			Problems = reasons;
			return await OnGetAsync(number, ct);
		}
		return Redirect(Forms.Url("/Campaigns/Proposal", ("number", number.ToString(System.Globalization.CultureInfo.InvariantCulture))));
	}

	public PostEdit Editor(Post post) =>
		new(Proposal!.Campaign, post, clock, "/Campaigns/Proposal", new Dictionary<string, string> { ["number"] = Proposal.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) }, new HashSet<Network>());

	public async Task<IResult> OnGetAsync(int number, CancellationToken ct)
	{
		Proposal = (await campaigns.ProposalsAsync(ct)).FirstOrDefault(p => p.Number == number);
		if (Proposal is null)
		{
			return NotFound();
		}
		Judge = await judging.StateAsync(ct);
		return Page();
	}

	public Task<IResult> OnPostApproveAsync(int number, CancellationToken ct) =>
		DecideAsync(number, (n, by) => campaigns.ApproveAsync(n, by, ct), ct);

	public Task<IResult> OnPostRejectAsync(int number, CancellationToken ct) =>
		DecideAsync(number, (n, by) => campaigns.RejectAsync(n, by, ct), ct);

	public async Task<IResult> OnPostRedraftAsync(int number, string post, Network network, string? note, CancellationToken ct)
	{
		var proposal = (await campaigns.ProposalsAsync(ct)).FirstOrDefault(p => p.Number == number);
		if (proposal is null)
		{
			return NotFound();
		}
		if (await campaigns.RedraftAsync(proposal.Campaign.Slug, post, network, note, Actor.Owner(User.Identity!.Name!), ct) is Outcome<Proposal>.Refused(var reasons))
		{
			Problems = reasons;
			return await OnGetAsync(number, ct);
		}
		return Redirect(Forms.Url("/Campaigns/Proposal", ("number", number.ToString(System.Globalization.CultureInfo.InvariantCulture))));
	}

	public async Task<IResult> OnPostEditAsync(int number, string post, DateTime at, Dictionary<Network, string>? texts, CancellationToken ct)
	{
		var proposal = (await campaigns.ProposalsAsync(ct)).FirstOrDefault(p => p.Number == number);
		if (proposal is null)
		{
			return NotFound();
		}
		var when = new DateTimeOffset(at, clock.Zone.GetUtcOffset(at));
		if (await campaigns.EditPostAsync(proposal.Campaign.Slug, post, when, texts ?? [], Actor.Owner(User.Identity!.Name!), ct) is Outcome<Campaign>.Refused(var reasons))
		{
			Problems = reasons;
			return await OnGetAsync(number, ct);
		}
		return Redirect(Forms.Url("/Campaigns/Proposal", ("number", number.ToString(System.Globalization.CultureInfo.InvariantCulture))));
	}

	public async Task<IResult> OnPostImageAsync(int number, string post, IFormFile? file, string? alt, List<Network>? networks, CancellationToken ct)
	{
		var proposal = (await campaigns.ProposalsAsync(ct)).FirstOrDefault(p => p.Number == number);
		if (proposal is null)
		{
			return NotFound();
		}
		var bytes = file is null ? null : await Api.CampaignApi.ReadAsync(file, ct);
		if (file is null || bytes is null)
		{
			Problems = [$"Choose an image of at most {CampaignService.MaxImageBytes / 1000} kB, or an MP4 video of at most {CampaignService.MaxVideoBytes / 1_000_000} MB."];
			return await OnGetAsync(number, ct);
		}
		if (await campaigns.AddMediaAsync(proposal.Campaign.Slug, file.FileName, bytes, post, networks, alt, Actor.Owner(User.Identity!.Name!), ct)
			is Outcome<Proposal>.Refused(var reasons))
		{
			Problems = reasons;
			return await OnGetAsync(number, ct);
		}
		return Redirect(Forms.Url("/Campaigns/Proposal", ("number", number.ToString(System.Globalization.CultureInfo.InvariantCulture))));
	}

	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) => (method, handler) switch
	{
		("GET", null) => OnGetAsync(form.Number("number"), ct),
		("POST", "accounts") => OnPostAccountsAsync(form.Number("number"), form.Dictionary<Network>("account"), ct),
		("POST", "approve") => OnPostApproveAsync(form.Number("number"), ct),
		("POST", "reject") => OnPostRejectAsync(form.Number("number"), ct),
		("POST", "redraft") => OnPostRedraftAsync(form.Number("number"), form.Text("post")!, form.Enum<Network>("network"), form.Text("note"), ct),
		("POST", "edit") => OnPostEditAsync(form.Number("number"), form.Text("post")!, form.DateTime("at"), form.Dictionary<Network>("texts"), ct),
		("POST", "image") => OnPostImageAsync(form.Number("number"), form.Text("post")!, form.File("file"), form.Text("alt"), form.EnumList<Network>("networks"), ct),
		_ => null
	};

	protected override RazorSlices.RazorSlice Slice() => ProposalPage.Create(this);

	private async Task<IResult> DecideAsync(int number, Func<int, Actor, Task<Outcome<Proposal>>> decide, CancellationToken ct)
	{
		switch (await decide(number, Actor.Owner(User.Identity!.Name!)))
		{
			case Outcome<Proposal>.Refused(var reasons):
				Problems = reasons;
				return await OnGetAsync(number, ct);
			default:
				return Redirect("/");
		}
	}
}
