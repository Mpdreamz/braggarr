namespace Braggarr.Domain;

/// <summary>
/// Every campaign Braggarr knows and every delivery it owes. Rebuilt from the posts repo on each sync: deliveries are owed for each
/// post and network of an approved or held campaign, and kept forever once anything has happened to them: an attempt, an issue.
/// </summary>
public sealed record Schedule(ValueList<Campaign> Campaigns, ValueList<Delivery> Deliveries)
{
	public static Schedule Empty { get; } = new([], []);

	/// <summary>Takes in what the posts repo says now. The order the campaigns arrive in does not matter.</summary>
	public Schedule Sync(IEnumerable<Campaign> campaigns)
	{
		var ordered = campaigns.OrderBy(c => c.Slug, StringComparer.Ordinal).ToValueList();
		var owed = Owed(ordered);
		var kept = Deliveries.Where(d => d.HasHistory || owed.Contains(d.Key)).ToDictionary(d => d.Key);
		foreach (var key in owed.Where(k => !kept.ContainsKey(k)))
		{
			kept[key] = Delivery.New(key);
		}
		return new(ordered, kept.Values.OrderBy(d => d.Key).ToValueList());
	}

	/// <summary>
	/// Pending deliveries whose time has come, of campaigns that may go out now: approved, ready, unchanged since approval, and
	/// past the judge's delay when the judge approved. A post that links the campaign's blog post also waits for that to be
	/// live. In the order they were meant to go out.
	/// </summary>
	public IReadOnlyList<Delivery> Due(DateTimeOffset now) =>
	[
		.. from d in Deliveries
		where d.State == DeliveryState.Pending
		let campaign = Campaigns.FirstOrDefault(c => c.Slug == d.Key.Campaign)
		where campaign is not null && campaign.MayGoOut(now)
		let post = campaign.Posts.FirstOrDefault(p => p.Id == d.Key.Post)
		where post is not null && post.At <= now
		where post.For(d.Key.Network) is not { } variant || !variant.LinksToBlog() || BlogPost(campaign) is { State: DeliveryState.Published }
		orderby post.At, d.Key
		select d
	];

	/// <summary>The delivery of a campaign's blog post, if it has one.</summary>
	public Delivery? BlogPost(Campaign campaign) =>
		campaign.Posts.FirstOrDefault(p => p.NetworksIn(campaign).Contains(Network.Blog)) is { } post
			? Find(new DeliveryKey(campaign.Slug, post.Id, Network.Blog))
			: null;

	/// <summary>Approved campaigns whose time has come to be proposed again.</summary>
	public IReadOnlyList<Campaign> RepeatsDue(DateTimeOffset now) => [.. Campaigns.Where(c => c.RepeatIsDue(Deliveries, now))];

	/// <summary>Failed deliveries with attempts left whose backoff has passed go back to pending. After three, the owner decides.</summary>
	public Schedule Retry(DateTimeOffset now) => this with { Deliveries = Deliveries.Select(d => d.HasAttemptsLeft ? d.Retry(now) : d).ToValueList() };

	/// <summary>At startup: anything still sending was interrupted, and is marked unknown rather than sent again.</summary>
	public Schedule Recover() => this with { Deliveries = Deliveries.Select(d => d.Recover()).ToValueList() };

	public Schedule With(Delivery delivery) => this with { Deliveries = Deliveries.Replace(d => d.Key == delivery.Key, delivery) };

	public Delivery? Find(DeliveryKey key) => Deliveries.FirstOrDefault(d => d.Key == key);

	public Campaign? Campaign(string slug) => Campaigns.FirstOrDefault(c => c.Slug == slug);

	public Variant? Variant(DeliveryKey key) => Campaign(key.Campaign)?.Posts.FirstOrDefault(p => p.Id == key.Post)?.For(key.Network);

	/// <summary>Deliveries something has happened to: an attempt, or an issue. A sync never drops one of these.</summary>
	public int WithHistory => Deliveries.Count(d => d.HasHistory);

	internal static HashSet<DeliveryKey> Owed(IEnumerable<Campaign> campaigns) =>
	[
		.. from c in campaigns
		where c.Status is CampaignStatus.Approved or CampaignStatus.Held
		from p in c.Posts
		from n in p.NetworksIn(c)
		select new DeliveryKey(c.Slug, p.Id, n)
	];
}
