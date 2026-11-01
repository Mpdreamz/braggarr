using CsCheck;

namespace Braggarr.Domain.Tests;

/// <summary>Generated domain values in every state, so every rule has something to attack.</summary>
public static class Gens
{
	public static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1));

	public static readonly Actor Owner = Actor.Owner("mpdreamz");

	public static readonly Actor Agent = Actor.Agent("release-bot");

	public static readonly Gen<Actor> Actors = Gen.OneOfConst(Owner, Agent, Actor.System);

	public static readonly Gen<Network> Network = Gen.Enum<Network>();

	public static readonly Gen<ValueList<Network>> CampaignNetworks =
		Gen.Enum<Network>().HashSet[1, 3].Select(set => set.Order().ToValueList());

	private static readonly string[] Texts =
	[
		"Curb 1.0 is out: a C# formatter that keeps your diffs small. https://github.com/nullean/curb",
		"New in heed: review a branch without leaving the keyboard.",
		"Mermaider renders diagrams to SVG without a browser."
	];

	/// <summary>The first text from <paramref name="start"/> on that fits the network: a Hacker News title is short, the others take any.</summary>
	public static string TextFor(Network network, int start) =>
		Enumerable.Range(start, Texts.Length).Select(i => Texts[i % Texts.Length]).First(t => Lengths.Fits(network, t));

	/// <summary>An uploaded video, for the YouTube variants of generated campaigns.</summary>
	public static readonly MediaRef Video = MediaRef.Video(new string('a', 64), "A terminal run where one line changes");

	/// <summary>A blog post's title, description and tags, as drafted.</summary>
	public static readonly Article Article = new("Curb 1.0, from the start", "What curb does to a diff, and why it stays small.", ["dotnet", "formatting"]);

	/// <summary>A variant as drafted: YouTube's carries its video, the blog's its article.</summary>
	public static Variant Drafted(Network network, string text) =>
		new(network, text, Lengths.NeedsVideo(network) ? [Video] : []) { Article = network == Domain.Network.Blog ? Article : null };

	/// <summary>A drafted variant that links the campaign's blog post, when it fits with the link.</summary>
	public static Variant Linking(Network network, string text) =>
		network != Domain.Network.Blog && Lengths.Fits(network, $"{text} {Blogging.Placeholder}") ? Drafted(network, $"{text} {Blogging.Placeholder}") : Drafted(network, text);

	/// <summary>A post to the networks given; a campaign's blog post is its first, so only the first goes to the blog.</summary>
	public static Post Post(string id, ValueList<Network> networks, int hour, bool complete, string[] texts, bool first = true)
	{
		var on = first ? networks : networks.Where(n => n != Domain.Network.Blog).ToValueList();
		var post = new Post(
			id,
			Start.AddHours(hour),
			on.Select(n => Drafted(n, texts[networks.ToList().IndexOf(n)])).Take(complete ? on.Count : Math.Max(0, on.Count - 1)).ToValueList()
		);
		return on.Count == networks.Count ? post : post with { Networks = on };
	}

	public static Gen<Campaign> Campaign(string slug) =>
		from networks in CampaignNetworks
		from postCount in Gen.Int[1, 3]
		from complete in Gen.Bool
		from status in Gen.Enum<CampaignStatus>()
		from edited in Gen.Int[0, 3].Select(i => i == 0)
		from named in Gen.Bool
		let whole = complete || status is CampaignStatus.Approved or CampaignStatus.Held
		from hours in Gen.Int[0, 48].Array[postCount]
		from starts in Gen.Int[0, 2].Array[networks.Count]
		let texts = networks.Select((n, i) => TextFor(n, starts[i])).ToArray()
		select Stamped(new Campaign(
			slug, "Curb 1.0", "curb", status, networks, ["https://github.com/nullean/curb"], "Announce curb 1.0.", "agent:release-bot",
			hours.Select((hour, i) => Post($"{i + 1:00}-post", networks, hour, whole, texts, first: i == 0)).Where(p => (p.Networks ?? networks).Count > 0).ToValueList()
		)
		{ Accounts = named ? [.. networks.Where(n => !n.IsManual()).Select(n => new CampaignAccount(n, "nullean"))] : [] }, edited);

	/// <summary>
	/// An approved or held campaign carries the owner's approval: of what it says, or, when <paramref name="edited"/>, of what
	/// it said before an edit. A proposal carries none.
	/// </summary>
	public static Campaign Stamped(Campaign c, bool edited = false) =>
		c.Status is CampaignStatus.Approved or CampaignStatus.Held
			? c with { Approval = new Approval(Owner.ToString(), edited ? "the-digest-before-an-edit" : c.Digest, Start.AddDays(-1)) }
			: c;

	/// <summary>A campaign laid out from talking points, drafted: posts that each go to some of its networks.</summary>
	public static Gen<Campaign> FromPoints(string slug) =>
		from networks in CampaignNetworks
		from many in Gen.Int[1, 4]
		from status in Gen.OneOfConst(CampaignStatus.Proposed, CampaignStatus.Approved, CampaignStatus.Held)
			// Hacker News and YouTube alone say one thing each: a title, a video. More points than that could never be approved.
		let narrow = networks.All(n => n.Shape() is NetworkShape.Title or NetworkShape.Video)
		let count = narrow ? 1 : many
		// The first point wants an image (a video, when the campaign is YouTube and Hacker News only); the second a video, so
		// YouTube gets a post.
		let points = Enumerable.Range(0, count)
			.Select(i => new TalkingPoint($"Point {i + 1}", i switch
			{
				0 when narrow && networks.Contains(Domain.Network.YouTube) => [new WantedMedia(MediaKind.Video, "a run")],
				0 => [new WantedMedia(MediaKind.Image, "a diff")],
				1 => [new WantedMedia(MediaKind.Video, "a run")],
				_ => []
			}))
			.ToValueList()
		// With a blog, the posts after it link it, as drafting would.
		let blog = networks.Contains(Domain.Network.Blog)
		let posts = Layout.Lay(points, networks, Start, Layout.DefaultSpacing)
			.Select(p => p with { Variants = [.. p.Networks!.Select((n, i) => blog ? Linking(n, TextFor(n, i)) : Drafted(n, TextFor(n, i)))] })
			.ToValueList()
		select Stamped(new Campaign(slug, "Curb 1.0", "curb", status, networks, [], "", "agent:release-bot", posts) { Points = points });

	public static readonly Gen<Campaign> Campaigns = Gen.OneOfConst("curb-1-0", "heed-0-20", "mermaider-svg").SelectMany(slug => Gen.OneOf(Campaign(slug), FromPoints(slug)));

	public static readonly Gen<Receipt> Receipts = Gen.Int[1, 999].Select(i => new Receipt($"id-{i}", $"https://bsky.app/profile/me/post/{i}"));

	/// <summary>A random walk of a delivery's own operations: most do nothing from the state they meet, which is a case too.</summary>
	private static readonly Gen<Delivery> Walks =
		from network in Network
		from steps in Gen.Int[0, 9].Array[0, 6]
		from receipt in Receipts
		select steps.Aggregate(
			Delivery.New(new DeliveryKey("curb-1-0", "01-launch", network)),
			(d, step) => step switch
			{
				0 => d.Begin(Start),
				1 => d.Succeed(receipt),
				2 => d.Fail("503 from the server"),
				3 => d.Retry(Start.AddHours(1)),
				4 => d.Recover(),
				5 => d.OpenManual(42),
				6 => d.CloseManual(),
				7 => d.Commit(receipt),
				8 => d.GoLive(),
				_ => d.TimeOut()
			}
		);

	/// <summary>
	/// What really happens to a delivery, round after round an hour apart: sent and answered, refused and retried, lost in a
	/// crash and settled by the owner; or, on Reddit and Hacker News, an issue opened and closed. Reaches the states a random
	/// walk rarely does: a second attempt, a retry after a crash.
	/// </summary>
	private static readonly Gen<Delivery> Stories =
		from network in Network
		from rounds in Gen.Int[0, 6].Array[0, 4]
		from receipt in Receipts
		select rounds.Select((r, i) => (r, at: Start.AddHours(i))).Aggregate(
			Delivery.New(new DeliveryKey("curb-1-0", "01-launch", network)),
			(d, x) => d.IsManual
				? (x.r % 2 == 0 ? d.OpenManual(42) : d.OpenManual(42).CloseManual())
				: d.IsBlog
				? x.r switch
				{
					0 => d.Begin(x.at),
					1 => d.Begin(x.at).Commit(receipt),
					2 => d.Begin(x.at).Commit(receipt).GoLive(),
					3 => d.Begin(x.at).Fail("409 from GitHub").Retry(x.at.AddMinutes(30)),
					4 => d.Begin(x.at).Recover(),
					5 => d.Begin(x.at).Commit(receipt).TimeOut().ConfirmMissing(),
					_ => d.Begin(x.at).Commit(receipt).TimeOut().ConfirmPublished(receipt)
				}
				: x.r switch
				{
					0 => d.Begin(x.at),
					1 => d.Begin(x.at).Succeed(receipt),
					2 => d.Begin(x.at).Fail("503 from the server"),
					3 => d.Begin(x.at).Fail("503 from the server").Retry(x.at.AddMinutes(30)),
					4 => d.Begin(x.at).Recover(),
					5 => d.Begin(x.at).Recover().ConfirmMissing(),
					_ => d.Begin(x.at).Recover().ConfirmPublished(receipt)
				}
		);

	/// <summary>Deliveries reached by running their own operations, so each one is a state the lifecycle really reaches.</summary>
	public static readonly Gen<Delivery> Deliveries = Gen.OneOf(Walks, Stories);

	/// <summary>A delivery of an older campaign that failed three times: the schedule must leave it to the owner.</summary>
	public static readonly Delivery Exhausted = Enumerable.Range(0, Delivery.MaxAttempts).Aggregate(
		Delivery.New(new DeliveryKey("aaa-older", "01-launch", Domain.Network.Mastodon)),
		(d, i) => d.Retry(Start.AddDays(i)).Begin(Start.AddDays(i)).Fail("503")
	);

	/// <summary>Schedules synced from generated campaigns, with some deliveries moved on, and one that ran out of attempts.</summary>
	public static readonly Gen<Schedule> Schedules =
		from campaigns in Gen.Select(Campaign("curb-1-0"), Campaign("heed-0-20"), (a, b) => new[] { a, b })
		from progress in Gen.Int[0, 7].Array[0, 8]
		from receipt in Receipts
		select progress.Select((step, i) => (step, i)).Aggregate(
			new Schedule([], [Exhausted]).Sync(campaigns),
			(s, x) => s.With(Advance(s.Deliveries[x.i % s.Deliveries.Count], x.step, receipt))
		);

	private static Delivery Advance(Delivery d, int step, Receipt receipt) => step switch
	{
		0 => d.Begin(Start),
		1 => d.Begin(Start).Succeed(receipt),
		2 => d.Begin(Start).Fail("timeout"),
		// Waiting again, with a history: refused once and retried, or lost in a crash and found missing by the owner.
		3 => d.Begin(Start).Fail("timeout").Retry(Start.AddHours(1)),
		4 => d.Begin(Start).Recover().ConfirmMissing(),
		// A blog post committed, and live: what a post that links it waits for.
		5 => d.Begin(Start).Commit(receipt).GoLive(),
		// Lost in a crash: the owner's to settle (a blog post goes back to waiting instead).
		6 => d.Begin(Start).Recover(),
		_ => d.OpenManual(7)
	};
}
