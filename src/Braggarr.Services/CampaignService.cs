using Braggarr.Domain;

namespace Braggarr.Services;

/// <summary>A post as proposed: when it goes out, and its text per network.</summary>
public sealed record PostDraft(string? Id, DateTimeOffset At, IReadOnlyDictionary<Network, string> Variants)
{
	public string? Angle { get; init; }

	/// <summary>The networks this post goes to, when not all of the campaign's.</summary>
	public IReadOnlyList<Network>? Networks { get; init; }

	/// <summary>The campaign's talking points this post covers, by position from 0.</summary>
	public IReadOnlyList<int> Points { get; init; } = [];

	/// <summary>The blog post's title, description and tags, when the post goes to the blog: its text is in <see cref="Variants"/>.</summary>
	public Article? Article { get; init; }
}

/// <summary>A campaign as proposed through the API or the UI. The slug defaults to one made from the title.</summary>
public sealed record CampaignDraft(
	string? Slug,
	string Title,
	string Project,
	IReadOnlyList<Network> Networks,
	IReadOnlyList<string> Links,
	string Brief,
	IReadOnlyList<PostDraft> Posts
)
{
	/// <summary>Months after its last post to propose it again; 0 never.</summary>
	public int RepeatMonths { get; init; }

	/// <summary>What to say, most important first. A draft with points and no posts gets its posts laid out from them.</summary>
	public IReadOnlyList<TalkingPoint> Points { get; init; } = [];

	/// <summary>When laid-out posts start; ten the next morning in the owner's time zone when not given.</summary>
	public DateTimeOffset? Start { get; init; }

	/// <summary>The accounts to post as, by network, where the defaults should not decide.</summary>
	public IReadOnlyDictionary<Network, string> Accounts { get; init; } = new Dictionary<Network, string>();
}

/// <summary>Proposing, approving, rejecting, holding and resuming campaigns. Who may do what is decided by the domain, not here.</summary>
public sealed partial class CampaignService(
	IReviews review,
	ICampaignRepository repository,
	SchedulingService scheduling,
	DraftingService drafting,
	INotifier notifier,
	IOwnerTime owner,
	TimeProvider time,
	IVideoStore videos,
	IJudges judges,
	IAccounts accounts,
	IProjectVisibility? visibility = null
)
{
	/// <summary>
	/// Why a campaign may not go to the blog: only a public project gets a blog post. Without a way to ask GitHub, none does.
	/// </summary>
	private async Task<string?> BlogRefusedAsync(Campaign campaign, CancellationToken ct) =>
		campaign.MayBlog(visibility is null ? ProjectVisibility.Unknown : await visibility.OfAsync(campaign, ct))
			? null
			: $"Only a public project gets a blog post, and GitHub does not say {campaign.Project} is public. Leave the blog out, or make its repository public first.";

	/// <summary>The hour laid-out posts go out at, in the owner's time zone.</summary>
	public const int Hour = 10;

	/// <summary>
	/// Opens a pull request. Whoever asks, the campaign starts as proposed. A draft without posts gets them from its talking points
	/// (asked of the model when the draft only has a brief): a long post on article networks, one short post per point on the
	/// others. Variants the draft leaves out are written first when a model is set up; what cannot be written is left missing,
	/// for the owner to see.
	/// </summary>
	public async Task<Outcome<Proposal>> ProposeAsync(CampaignDraft draft, Actor by, CancellationToken ct)
	{
		if (Current(SlugOf(draft)) is null)
		{
			draft = await LayOutAsync(draft, ct);
		}
		draft = WithPostIds(draft);
		if (Current(SlugOf(draft)) is null)
		{
			(draft, _) = await drafting.FillAsync(draft, null, ct);
		}
		var (accounted, unknown) = WithAccounts(Build(draft, by), draft.Accounts);
		var campaign = accounted;
		var problems = CampaignRules.All.Validate(campaign).Select(v => v.Message).Concat(unknown).ToList();
		if (await BlogRefusedAsync(campaign, ct) is { } blog)
		{
			problems.Add(blog);
		}
		if (Current(campaign.Slug) is not null || (await review.OpenAsync(ct)).Any(p => p.Campaign.Slug == campaign.Slug))
		{
			problems.Add($"A campaign called \"{campaign.Slug}\" already exists. Choose another slug.");
		}
		if (problems.Count > 0)
		{
			return new Outcome<Proposal>.Refused(problems);
		}

		var proposal = await review.ProposeAsync(campaign, by, ct);
		await notifier.NotifyAsync(new Notice(NoticeLevel.Action, $"Review: {campaign.Title}", $"Proposed by {by.Name}.", proposal.Url), ct);
		return new Outcome<Proposal>.Done(proposal);
	}

	private async Task<CampaignDraft> LayOutAsync(CampaignDraft draft, CancellationToken ct)
	{
		if (draft.Posts.Count > 0)
		{
			return draft;
		}
		// Without a model (or when it cannot say), the title is the one point: a post on every network, for the owner to write.
		var points = draft.Points.Count > 0
			? draft.Points
			: await drafting.PointsAsync(new PointsRequest(draft.Title, draft.Project, draft.Brief, draft.Links), ct) is { Count: > 0 } found
				? found
				: string.IsNullOrWhiteSpace(draft.Title) ? [] : [new TalkingPoint(draft.Title.Trim())];
		if (points.Count == 0)
		{
			return draft;
		}
		var posts = Layout.Lay(points, [.. draft.Networks.Distinct().Order()], draft.Start ?? NextMorning(), Layout.DefaultSpacing);
		return draft with
		{
			Points = points,
			Posts = [.. posts.Select(p => new PostDraft(p.Id, p.At, new Dictionary<Network, string>()) { Angle = p.Angle, Networks = p.Networks, Points = p.Points })]
		};
	}

	/// <summary>Ten tomorrow morning in the owner's time zone, so there is a night to review it.</summary>
	/// <summary>Ten tomorrow morning in the owner's time zone: when a post that names no time goes out.</summary>
	public DateTimeOffset NextMorning()
	{
		var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), owner.Zone);
		var day = local.Date.AddDays(1).AddHours(Hour);
		return new DateTimeOffset(day, owner.Zone.GetUtcOffset(day));
	}

	public Task<IReadOnlyList<Proposal>> ProposalsAsync(CancellationToken ct) => review.OpenAsync(ct);

	public Task<byte[]?> ReadProposalMediaAsync(Proposal proposal, string path, CancellationToken ct) => review.ReadMediaAsync(proposal, path, ct);

	/// <summary>Rewrites an open proposal. The slug stays; once merged, a campaign changes through the owner only.</summary>
	public async Task<Outcome<Proposal>> UpdateProposalAsync(string slug, CampaignDraft draft, Actor by, CancellationToken ct)
	{
		var proposal = (await review.OpenAsync(ct)).FirstOrDefault(p => p.Campaign.Slug == slug);
		if (proposal is null)
		{
			return new Outcome<Proposal>.Refused($"There is no open proposal \"{slug}\". Only a proposal can be changed through the API.");
		}
		// The accounts it posts as stay, unless the change names others; a network it now adds gets the default.
		var asked = proposal.Campaign.Accounts.Where(a => accounts.Connected(a.Network).Contains(a.Account)).ToDictionary(a => a.Network, a => a.Account);
		foreach (var (network, account) in draft.Accounts)
		{
			asked[network] = account;
		}
		var (campaign, unknown) = WithAccounts(Build(draft with { Slug = slug }, by) with { CreatedBy = proposal.Campaign.CreatedBy }, asked);
		var problems = CampaignRules.All.Validate(campaign).Select(v => v.Message).Concat(unknown).ToList();
		if (problems.Count > 0)
		{
			return new Outcome<Proposal>.Refused(problems);
		}
		return new Outcome<Proposal>.Done(await review.UpdateAsync(proposal, campaign, by, ct));
	}

	/// <summary>Writes one variant of an open proposal again, with an optional note on what to change.</summary>
	public async Task<Outcome<Proposal>> RedraftAsync(string slug, string post, Network network, string? note, Actor by, CancellationToken ct)
	{
		var proposal = (await review.OpenAsync(ct)).FirstOrDefault(p => p.Campaign.Slug == slug);
		if (proposal is null)
		{
			return new Outcome<Proposal>.Refused($"There is no open proposal \"{slug}\". Only a proposal can be redrafted.");
		}
		return await drafting.RedraftAsync(proposal.Campaign, post, network, note, ct) switch
		{
			Outcome<Campaign>.Done(var campaign) => new Outcome<Proposal>.Done(await review.UpdateAsync(proposal, campaign, by, ct)),
			Outcome<Campaign>.Refused(var reasons) => new Outcome<Proposal>.Refused(reasons),
			_ => new Outcome<Proposal>.Refused("Nothing was written.")
		};
	}

	/// <summary>
	/// The owner edits one post: its time and the text of its variants. For a proposal that is a commit on its branch. For an
	/// approved campaign it is a commit on main, and only variants that have not gone out may change: what was posted stays what
	/// the history says was posted.
	/// </summary>
	public async Task<Outcome<Campaign>> EditPostAsync(string slug, string postId, DateTimeOffset at, IReadOnlyDictionary<Network, string> texts, Actor by, CancellationToken ct)
	{
		if (by.Kind != ActorKind.Owner)
		{
			return new Outcome<Campaign>.Refused("Only the owner edits campaigns here. Agents change proposals through the API.");
		}
		var proposal = (await review.OpenAsync(ct)).FirstOrDefault(p => p.Campaign.Slug == slug);
		var campaign = proposal?.Campaign ?? Current(slug);
		var post = campaign?.Posts.FirstOrDefault(p => p.Id == postId);
		if (campaign is null || post is null)
		{
			return new Outcome<Campaign>.Refused($"There is no post \"{postId}\" in \"{slug}\".");
		}

		var changed = post.Variants
			// A blog article's images are the ones its text shows, so they follow the text.
			.Select(v => texts.TryGetValue(v.Network, out var text) && text.Trim() != v.Text
				? v with { Text = text.Trim(), Media = v.Network == Network.Blog ? Blogging.ImagesIn(text.Trim()) : v.Media }
				: v)
			.Concat(texts.Where(t => post.NetworksIn(campaign).Contains(t.Key) && post.For(t.Key) is null && t.Value.Trim().Length > 0).Select(t => new Variant(t.Key, t.Value.Trim())))
			.OrderBy(v => v.Network)
			.ToValueList();
		var edited = post with { At = at, Variants = changed };
		if (edited == post)
		{
			return new Outcome<Campaign>.Done(campaign);
		}

		if (proposal is null)
		{
			var sent = edited.Variants.Where(v => v != post.For(v.Network) || at != post.At)
				.Select(v => scheduling.Current.Find(new DeliveryKey(slug, postId, v.Network)))
				.Where(d => d is not null && d.State != DeliveryState.Pending)
				.Select(d => d!.Key.Network)
				.ToList();
			if (sent.Count > 0)
			{
				return new Outcome<Campaign>.Refused($"{string.Join(", ", sent)} already went out (or is going out); what was posted cannot be edited here.");
			}
		}

		var updated = campaign with { Posts = campaign.Posts.Replace(p => p.Id == postId, edited) };
		// The campaign's own rules keep an approved campaign complete and its images described.
		var problems = CampaignRules.All.Validate(updated).Select(v => v.Message).ToList();
		if (problems.Count > 0)
		{
			return new Outcome<Campaign>.Refused(problems);
		}

		if (proposal is not null)
		{
			_ = await review.UpdateAsync(proposal, updated, by, ct);
		}
		else
		{
			await repository.UpdatePostAsync(slug, edited, by, ct);
			scheduling.RequestSync();
		}
		return new Outcome<Campaign>.Done(updated);
	}

	/// <summary>The owner sets how often an approved campaign is proposed again.</summary>
	/// <summary>Changes the accounts an open proposal posts as, on the networks given; only the owner does.</summary>
	public async Task<Outcome<Proposal>> ChooseAccountsAsync(int number, IReadOnlyDictionary<Network, string> chosen, Actor by, CancellationToken ct)
	{
		if (by.Kind != ActorKind.Owner)
		{
			return new Outcome<Proposal>.Refused("Only the owner chooses which of their accounts a campaign posts as.");
		}
		if ((await review.OpenAsync(ct)).FirstOrDefault(p => p.Number == number) is not { } proposal)
		{
			return new Outcome<Proposal>.Refused($"There is no open proposal #{number}.");
		}
		var campaign = proposal.Campaign;
		var problems = new List<string>();
		var kept = campaign.Accounts.Where(a => !chosen.ContainsKey(a.Network)).ToList();
		foreach (var (network, account) in chosen)
		{
			if (!campaign.Networks.Contains(network) || network.IsManual())
			{
				problems.Add($"{campaign.Title} does not post on {network}.");
			}
			else if (!accounts.Connected(network).Contains(account))
			{
				problems.Add($"No {network} account \"{account}\" is connected.");
			}
			else
			{
				kept.Add(new CampaignAccount(network, account));
			}
		}
		if (problems.Count > 0)
		{
			return new Outcome<Proposal>.Refused(problems);
		}
		var updated = campaign with { Accounts = [.. kept.OrderBy(a => a.Network)] };
		return updated.Accounts == campaign.Accounts
			? new Outcome<Proposal>.Done(proposal)
			: new Outcome<Proposal>.Done(await review.UpdateAsync(proposal, updated, by, ct));
	}

	public async Task<Outcome<Campaign>> SetRepeatAsync(string slug, int months, Actor by, CancellationToken ct)
	{
		if (by.Kind != ActorKind.Owner)
		{
			return new Outcome<Campaign>.Refused("Only the owner changes how a campaign repeats.");
		}
		if (Current(slug) is not { } campaign)
		{
			return new Outcome<Campaign>.Refused($"There is no approved campaign \"{slug}\".");
		}
		var updated = campaign with { RepeatMonths = months };
		var problems = CampaignRules.All.Validate(updated).Select(v => v.Message).ToList();
		if (problems.Count > 0)
		{
			return new Outcome<Campaign>.Refused(problems);
		}
		await repository.SetRepeatAsync(slug, months, by, ct);
		scheduling.RequestSync();
		return new Outcome<Campaign>.Done(updated);
	}

	/// <summary>The most an image may weigh: Bluesky refuses anything over a megabyte.</summary>
	public const int MaxImageBytes = 1_000_000;

	/// <summary>The most a video may weigh. Every network that takes video takes this much; videos are kept on the server.</summary>
	public const int MaxVideoBytes = 100_000_000;

	/// <summary>
	/// Adds an image or a video to an open proposal, onto the variants of <paramref name="post"/> for <paramref name="networks"/>
	/// (every network that takes it, when none are named). An image goes into the campaign's <c>media</c> folder on its branch;
	/// without alt text, Claude writes it, and without Claude, alt text is required. A video (MP4) is kept on the server and
	/// fills the post's wanted video slot; its description, when none is given, is the slot's.
	/// </summary>
	public async Task<Outcome<Proposal>> AddMediaAsync(
		string slug, string fileName, byte[] bytes, string post, IReadOnlyList<Network>? networks, string? alt, Actor by, CancellationToken ct)
	{
		var proposal = (await review.OpenAsync(ct)).FirstOrDefault(p => p.Campaign.Slug == slug);
		if (proposal is null)
		{
			return new Outcome<Proposal>.Refused($"There is no open proposal \"{slug}\". Images are added to proposals.");
		}
		var extension = Path.GetExtension(fileName).ToLowerInvariant();
		var stem = Slugs.From(Path.GetFileNameWithoutExtension(fileName));
		var name = $"media/{(stem.Length > 0 ? stem : "image")}{extension}";
		var type = MediaFile.ContentTypeOf(name);
		var problems = new List<string>();
		if (type is null)
		{
			problems.Add("Images are PNG, JPEG, WebP or GIF; videos are MP4.");
		}
		else if (type == "video/mp4" && bytes.Length is 0 or > MaxVideoBytes)
		{
			problems.Add($"A video is at most {MaxVideoBytes / 1_000_000} MB; this one is {bytes.Length / 1_000_000} MB.");
		}
		else if (type != "video/mp4" && bytes.Length is 0 or > MaxImageBytes)
		{
			problems.Add($"An image is at most {MaxImageBytes / 1000} kB (Bluesky's limit); this one is {bytes.Length / 1000} kB.");
		}
		var campaign = proposal.Campaign;
		var target = campaign.Posts.FirstOrDefault(p => p.Id == post);
		if (target is null)
		{
			problems.Add($"{slug} has no post \"{post}\".");
		}
		if (problems.Count > 0)
		{
			return new Outcome<Proposal>.Refused(problems);
		}
		if (type == "video/mp4")
		{
			return await AddVideoAsync(proposal, target!, bytes, networks, alt, by, ct);
		}

		var description = string.IsNullOrWhiteSpace(alt)
			? await drafting.DescribeAsync(new MediaFile(name, type!, bytes, ""), $"{campaign.Title}: {campaign.Brief}", ct)
			: alt.Trim();
		if (description is null)
		{
			return new Outcome<Proposal>.Refused("Describe the image: alt text is required, and no model is set up to write it.");
		}

		var postNetworks = target!.NetworksIn(campaign);
		// A network whose post is the video takes no image.
		var on = (networks is { Count: > 0 } ? networks : postNetworks).Where(n => postNetworks.Contains(n) && Lengths.MediaLimit(n) > 0 && !Lengths.NeedsVideo(n)).ToHashSet();
		var image = new MediaRef(name, description);
		var updated = campaign with
		{
			Posts = campaign.Posts.Replace(p => p.Id == post, target with
			{
				Variants = target.Variants.Select(v => on.Contains(v.Network) && !v.Media.Contains(image) ? v with { Media = WithImage(v.Media, image) } : v).ToValueList()
			})
		};
		var violations = CampaignRules.All.Validate(updated).Select(v => v.Message).ToList();
		if (violations.Count > 0)
		{
			return new Outcome<Proposal>.Refused(violations);
		}
		return new Outcome<Proposal>.Done(await review.UpdateAsync(proposal, updated, by, ct, new Dictionary<string, byte[]> { [name] = bytes }));
	}

	private async Task<Outcome<Proposal>> AddVideoAsync(
		Proposal proposal, Post target, byte[] bytes, IReadOnlyList<Network>? networks, string? alt, Actor by, CancellationToken ct)
	{
		var campaign = proposal.Campaign;
		if (Mp4.Read(bytes) is not { } info)
		{
			return new Outcome<Proposal>.Refused("That is not an MP4 Braggarr can read: it found no video track in it.");
		}
		var postNetworks = target.NetworksIn(campaign);
		var on = (networks is { Count: > 0 } ? networks : postNetworks).Where(n => postNetworks.Contains(n) && Lengths.TakesVideo(n)).ToHashSet();
		if (on.Count == 0)
		{
			return new Outcome<Proposal>.Refused($"None of {target.Id}'s networks takes video here; Bluesky, Mastodon, LinkedIn and X do.");
		}
		var problems = new List<string>();
		foreach (var network in on.Order())
		{
			var (shortest, longest) = Lengths.VideoLength(network);
			if (info.Duration < shortest || (longest is { } most && info.Duration > most))
			{
				problems.Add($"{network} takes videos {(shortest > TimeSpan.Zero ? $"of {shortest.TotalSeconds:0} seconds to" : "up to")} {longest!.Value.TotalMinutes:0} minutes; this one runs {info.Duration:m\\:ss}.");
			}
			var images = target.For(network)?.Media.Where(m => !m.IsVideo).ToList() ?? [];
			if (images.Count > 0)
			{
				problems.Add($"{target.Id} on {network} already has {(images.Count == 1 ? "an image" : "images")}; a post carries one video or images, not both.");
			}
		}
		var slot = target.Variants.Where(v => on.Contains(v.Network)).SelectMany(v => v.Media).FirstOrDefault(m => m.Path == "wanted:video");
		var description = string.IsNullOrWhiteSpace(alt) ? slot?.Alt : alt.Trim();
		if (description is null)
		{
			problems.Add("Describe the video: a description is required, and there is no wanted slot to take one from.");
		}
		if (problems.Count > 0)
		{
			return new Outcome<Proposal>.Refused(problems);
		}

		var video = await videos.SaveAsync(bytes, description!, ct);
		var updated = campaign with
		{
			Posts = campaign.Posts.Replace(p => p.Id == target.Id, target with
			{
				Variants = target.Variants.Select(v => on.Contains(v.Network) ? v with { Media = [video] } : v).ToValueList()
			})
		};
		var violations = CampaignRules.All.Validate(updated).Select(v => v.Message).ToList();
		if (violations.Count > 0)
		{
			return new Outcome<Proposal>.Refused(violations);
		}
		return new Outcome<Proposal>.Done(await review.UpdateAsync(proposal, updated, by, ct));
	}

	/// <summary>An image fills the first wanted image slot, or comes after what is there.</summary>
	private static ValueList<MediaRef> WithImage(ValueList<MediaRef> media, MediaRef image)
	{
		var slot = media.FirstOrDefault(m => m.Path == MediaRef.Wanted(new WantedMedia(MediaKind.Image, "")).Path);
		return slot is null ? media.Add(image) : media.Replace(m => ReferenceEquals(m, slot), image);
	}

	/// <summary>Adds a post to an open proposal. What the other posts carry (images, where they go) stays as it is.</summary>
	public async Task<Outcome<Proposal>> AddPostAsync(string slug, PostDraft post, Actor by, CancellationToken ct)
	{
		var proposal = (await review.OpenAsync(ct)).FirstOrDefault(p => p.Campaign.Slug == slug);
		if (proposal is null)
		{
			return new Outcome<Proposal>.Refused($"There is no open proposal \"{slug}\".");
		}
		var c = proposal.Campaign;
		var one = new CampaignDraft(c.Slug, c.Title, c.Project, c.Networks, c.Links, c.Brief, [post with { Id = post.Id ?? $"{c.Posts.Count + 1:00}-post" }])
		{
			Points = c.Points
		};
		var campaign = c with { Posts = c.Posts.Add(Build(one, by).Posts[0]) };
		var problems = CampaignRules.All.Validate(campaign).Select(v => v.Message).ToList();
		if (problems.Count > 0)
		{
			return new Outcome<Proposal>.Refused(problems);
		}
		return new Outcome<Proposal>.Done(await review.UpdateAsync(proposal, campaign, by, ct));
	}

	/// <summary>Merges the pull request, if the domain says this actor may approve this campaign.</summary>
	/// <summary>
	/// Approves a proposal: the stamp (who, what, when, and for the judge from when) goes onto its branch with what it approves,
	/// and the merge brings both to main. Only the owner or the judge, and only a ready proposal.
	/// </summary>
	public async Task<Outcome<Proposal>> ApproveAsync(int number, Actor by, CancellationToken ct, DateTimeOffset? notBefore = null)
	{
		var proposal = (await review.OpenAsync(ct)).FirstOrDefault(p => p.Number == number);
		if (proposal is null)
		{
			return new Outcome<Proposal>.Refused($"There is no open proposal #{number}.");
		}
		var command = new CampaignCommand(CampaignDecision.Approve, by) { At = time.GetUtcNow(), NotBefore = notBefore };
		var events = CampaignDecider.Decide(proposal.Campaign, command);
		if (events.Count == 0)
		{
			return new Outcome<Proposal>.Refused(WhyNot(proposal.Campaign, command));
		}
		if (await JudgedProblemsAsync(proposal.Campaign, by, ct) is { Count: > 0 } judged)
		{
			return new Outcome<Proposal>.Refused(judged);
		}
		if (await BlogRefusedAsync(proposal.Campaign, ct) is { } blog)
		{
			return new Outcome<Proposal>.Refused(blog);
		}
		var stamp = events.Aggregate(proposal.Campaign, CampaignDecider.Evolve).Approval;
		var stamped = await review.UpdateAsync(proposal, proposal.Campaign with { Approval = stamp }, by, ct);
		await review.MergeAsync(stamped, by, ct);
		scheduling.RequestSync();
		return new Outcome<Proposal>.Done(stamped);
	}

	/// <summary>
	/// What the judge's verdict says against approving, when there is a judge and criteria to judge by: no verdict on this
	/// version yet, or a blocking failure, for anyone; and for the judge itself, any failure at all.
	/// </summary>
	private async Task<IReadOnlyList<string>> JudgedProblemsAsync(Campaign campaign, Actor by, CancellationToken ct)
	{
		IReadOnlyList<Criterion> criteria;
		try
		{
			criteria = judges.Current is null ? [] : await repository.CriteriaAsync(ct);
		}
		catch (FormatException e)
		{
			return [$"The judge's criteria cannot be read, so nothing is approved until they are fixed. {e.Message}"];
		}
		if (criteria.Count == 0)
		{
			return by.Kind == ActorKind.Judge ? ["The judge approves only against criteria, and there are none (judges/ in the posts repo)."] : [];
		}
		if (!campaign.JudgedAsIs(Criteria.Digest(criteria)))
		{
			return ["The judge has not judged this version yet. It does within a minute of a change; approve after that."];
		}
		var judgement = campaign.Judgement!;
		if (judgement.Blocked)
		{
			return
			[
				"The judge blocks it:",
				.. judgement.Findings.Where(f => !f.Passed && f.Blocks).Select(f => $"{f.Criterion}, {f.Post} on {f.Network}: {f.Reason}")
			];
		}
		return by.Kind == ActorKind.Judge && !judgement.AllPassed
			? ["The judge approves by itself only when every criterion passes, and some warned."]
			: [];
	}

	/// <summary>Approves again a campaign changed on main since it was approved: a new stamp for what it says now.</summary>
	public async Task<Outcome<Campaign>> ReapproveAsync(string slug, Actor by, CancellationToken ct, DateTimeOffset? notBefore = null) =>
		Current(slug) is { } campaign
			? await ReapproveAsync(campaign, by, notBefore, ct)
			: new Outcome<Campaign>.Refused($"There is no approved campaign \"{slug}\".");

	/// <summary>Approves a campaign on main again as it is given, through the same rules and the judge's gate.</summary>
	private async Task<Outcome<Campaign>> ReapproveAsync(Campaign campaign, Actor by, DateTimeOffset? notBefore, CancellationToken ct)
	{
		var slug = campaign.Slug;
		var command = new CampaignCommand(CampaignDecision.Reapprove, by) { At = time.GetUtcNow(), NotBefore = notBefore };
		var events = CampaignDecider.Decide(campaign, command);
		if (events.Count == 0)
		{
			return new Outcome<Campaign>.Refused(WhyNot(campaign, command));
		}
		if (await JudgedProblemsAsync(campaign, by, ct) is { Count: > 0 } judged)
		{
			return new Outcome<Campaign>.Refused(judged);
		}
		var approved = events.Aggregate(campaign, CampaignDecider.Evolve);
		await repository.StampAsync(slug, approved.Approval!, ct);
		scheduling.RequestSync();
		return new Outcome<Campaign>.Done(approved);
	}

	public Task<Outcome<Proposal>> RejectAsync(int number, Actor by, CancellationToken ct) =>
		DecideOnProposalAsync(number, new CampaignCommand(CampaignDecision.Reject, by), review.CloseAsync, ct);

	public Task<Outcome<Campaign>> HoldAsync(string slug, Actor by, CancellationToken ct) =>
		DecideOnCampaignAsync(slug, new CampaignCommand(CampaignDecision.Hold, by), held: true, ct);

	public Task<Outcome<Campaign>> ResumeAsync(string slug, Actor by, CancellationToken ct) =>
		DecideOnCampaignAsync(slug, new CampaignCommand(CampaignDecision.Resume, by), held: false, ct);

	private async Task<Outcome<Proposal>> DecideOnProposalAsync(
		int number,
		CampaignCommand command,
		Func<Proposal, Actor, CancellationToken, Task> carryOut,
		CancellationToken ct
	)
	{
		var proposal = (await review.OpenAsync(ct)).FirstOrDefault(p => p.Number == number);
		if (proposal is null)
		{
			return new Outcome<Proposal>.Refused($"There is no open proposal #{number}.");
		}
		if (CampaignDecider.Decide(proposal.Campaign, command).Count == 0)
		{
			return new Outcome<Proposal>.Refused(WhyNot(proposal.Campaign, command));
		}
		await carryOut(proposal, command.By, ct);
		scheduling.RequestSync();
		return new Outcome<Proposal>.Done(proposal);
	}

	private async Task<Outcome<Campaign>> DecideOnCampaignAsync(string slug, CampaignCommand command, bool held, CancellationToken ct)
	{
		if (Current(slug) is not { } campaign)
		{
			return new Outcome<Campaign>.Refused($"There is no approved campaign \"{slug}\".");
		}
		var events = CampaignDecider.Decide(campaign, command);
		if (events.Count == 0)
		{
			return new Outcome<Campaign>.Refused(WhyNot(campaign, command));
		}
		await repository.SetHeldAsync(slug, held, command.By, ct);
		scheduling.RequestSync();
		return new Outcome<Campaign>.Done(events.Aggregate(campaign, CampaignDecider.Evolve));
	}

	private Campaign? Current(string slug) => scheduling.Current.Campaign(slug);

	private static IReadOnlyList<string> WhyNot(Campaign campaign, CampaignCommand command)
	{
		if (command.Decision is CampaignDecision.Approve or CampaignDecision.Reapprove && !command.By.MayApprove)
		{
			return ["Only the owner approves, or the judge when the owner lets it. Agents can propose campaigns, nothing more."];
		}
		if (command.Decision == CampaignDecision.Reapprove && !campaign.NeedsApproval)
		{
			return [$"{campaign.Slug} is approved as it is: nothing changed since, so there is nothing to approve again."];
		}
		if (command.Decision is CampaignDecision.Approve or CampaignDecision.Reapprove && command.NotBefore is { } notBefore && notBefore < command.At)
		{
			return ["An approval cannot let anything go out before the approval itself."];
		}
		if (command.By.Kind != ActorKind.Owner && command.Decision is not (CampaignDecision.Approve or CampaignDecision.Reapprove))
		{
			return ["Only the owner can do that. Agents can propose campaigns, nothing more."];
		}
		if (command.Decision == CampaignDecision.Approve && campaign.IsComplete && !campaign.IsCovered)
		{
			return
			[
				"Every talking point needs a post, and a post on every short network, before it can be approved.",
				.. from i in Enumerable.Range(0, campaign.Points.Count)
				where !campaign.Posts.Any(p => p.Points.Contains(i))
				select $"Point {i + 1} (\"{campaign.Points[i].Text}\") is in no post.",
				.. from i in Enumerable.Range(0, campaign.Points.Count)
				from n in campaign.Networks.Where(n => n.Shape() == NetworkShape.Stream)
				where !campaign.Posts.Any(p => p.Points.Contains(i) && p.NetworksIn(campaign).Contains(n))
				select $"Point {i + 1} (\"{campaign.Points[i].Text}\") has no {n} post."
			];
		}
		if (command.Decision == CampaignDecision.Approve && campaign.IsComplete && !campaign.HasItsVideos)
		{
			return
			[
				"Every YouTube post needs its video before the campaign can be approved.",
				.. from p in campaign.Posts
				from v in p.Variants
				where Lengths.NeedsVideo(v.Network) && !v.Media.Any(m => m.IsStoredVideo)
				select $"{p.Id}: no video for {v.Network} yet."
			];
		}
		if (command.Decision == CampaignDecision.Approve && campaign.IsComplete && !campaign.IsReady)
		{
			return ["Every image needs alt text, and no variant may carry more images than its network takes."];
		}
		if (command.Decision == CampaignDecision.Approve && !campaign.IsComplete)
		{
			return
			[
				"Every post needs a variant that fits for every network before it can be approved.",
				.. from p in campaign.Posts
				from n in p.NetworksIn(campaign)
				let v = p.For(n)
				where v is not { Fits: true }
				select v is null ? $"{p.Id}: no {n} variant." : $"{p.Id}: the {n} variant is {Lengths.Measure(n, v.Text)} long; the limit is {Lengths.Limit(n)}."
			];
		}
		return [$"A {campaign.Status.ToString().ToLowerInvariant()} campaign cannot be {Past(command.Decision)}."];
	}

	private static string Past(CampaignDecision decision) => decision switch
	{
		CampaignDecision.Approve => "approved",
		CampaignDecision.Reject => "rejected",
		CampaignDecision.Hold => "held",
		CampaignDecision.Reapprove => "approved again",
		_ => "resumed"
	};

	private static string SlugOf(CampaignDraft draft) => string.IsNullOrWhiteSpace(draft.Slug) ? Slugs.From(draft.Title) : draft.Slug.Trim();

	/// <summary>Gives every post an id (01-post, 02-post…) so drafted variants can be matched to their posts.</summary>
	private static CampaignDraft WithPostIds(CampaignDraft draft) =>
		draft with { Posts = [.. draft.Posts.Select((p, i) => string.IsNullOrWhiteSpace(p.Id) ? p with { Id = $"{i + 1:00}-post" } : p with { Id = p.Id.Trim() })] };

	/// <summary>
	/// The account each of the campaign's networks posts as, written into the campaign so it is part of what is approved: the
	/// one asked for, else the defaults'. A network with no connected account gets none, and waits like an unconnected one.
	/// </summary>
	private (Campaign Campaign, IReadOnlyList<string> Problems) WithAccounts(Campaign campaign, IReadOnlyDictionary<Network, string> asked)
	{
		var problems = new List<string>();
		var chosen = new List<CampaignAccount>();
		foreach (var network in campaign.Networks.Where(n => !n.IsManual()))
		{
			if (asked.TryGetValue(network, out var wanted) && !string.IsNullOrWhiteSpace(wanted))
			{
				var connected = accounts.Connected(network);
				if (connected.Contains(wanted.Trim()))
				{
					chosen.Add(new CampaignAccount(network, wanted.Trim()));
				}
				else
				{
					problems.Add($"No {network} account \"{wanted.Trim()}\" is connected{(connected.Count == 0 ? "" : $"; connected: {string.Join(", ", connected)}")}.");
				}
			}
			else if (accounts.Choose(network, campaign.Project) is { } choice)
			{
				chosen.Add(new CampaignAccount(network, choice.Account));
			}
		}
		return (campaign with { Accounts = [.. chosen] }, problems);
	}

	internal static Campaign Build(CampaignDraft draft, Actor by)
	{
		var points = draft.Points.Select(p => new TalkingPoint(p.Text.Trim(), p.Wanted)).ToValueList();
		return new(
			Slug: SlugOf(draft),
			Title: draft.Title.Trim(),
			Project: draft.Project.Trim(),
			Status: CampaignStatus.Proposed,
			Networks: draft.Networks.Distinct().Order().ToValueList(),
			Links: draft.Links.ToValueList(),
			Brief: draft.Brief.Trim(),
			CreatedBy: by.ToString(),
			Posts: draft.Posts
				.Select((p, i) =>
				{
					var wanted = p.Points.Where(n => n >= 0 && n < points.Count).SelectMany(n => points[n].Wanted).ToList();
					return new Post(
						string.IsNullOrWhiteSpace(p.Id) ? $"{i + 1:00}-post" : p.Id.Trim(),
						p.At,
						p.Variants.OrderBy(v => v.Key)
							.Select(v => v.Key == Network.Blog
								? new Variant(v.Key, v.Value.Trim(), Blogging.ImagesIn(v.Value.Trim())) { Article = p.Article }
								: new Variant(v.Key, v.Value.Trim(), Wanted(v.Key, wanted)))
							.ToValueList()
					)
					{
						Angle = string.IsNullOrWhiteSpace(p.Angle) ? null : p.Angle.Trim(),
						Networks = p.Networks?.Distinct().Order().ToValueList(),
						Points = [.. p.Points.Distinct().Order()]
					};
				})
				.ToValueList()
		)
		{
			RepeatMonths = draft.RepeatMonths,
			Points = points
		};
	}

	/// <summary>
	/// The slots a variant keeps for the media its points want, on networks that take media: one video if any is wanted (a post
	/// carries one video or images, not both), otherwise images up to the network's limit.
	/// </summary>
	private static ValueList<MediaRef> Wanted(Network network, List<WantedMedia> wanted)
	{
		var limit = Lengths.MediaLimit(network);
		if (limit == 0 || wanted.Count == 0 || (Lengths.NeedsVideo(network) && !wanted.Any(w => w.Kind == MediaKind.Video)))
		{
			return [];
		}
		return Lengths.TakesVideo(network) && wanted.FirstOrDefault(w => w.Kind == MediaKind.Video) is { } video
			? [MediaRef.Wanted(video)]
			: [.. wanted.Where(w => w.Kind == MediaKind.Image).Take(limit).Select(MediaRef.Wanted)];
	}
}
