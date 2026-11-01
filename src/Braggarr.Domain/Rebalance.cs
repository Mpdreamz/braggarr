namespace Braggarr.Domain;

/// <summary>
/// How a rebalance spreads posts: never on a rest day, at most <see cref="PerDay"/> posts a day on each network, and within
/// the posting hours, in the owner's time zone.
/// </summary>
public sealed record RebalanceOptions(ValueList<DayOfWeek> RestDays, int PerDay, TimeOnly From, TimeOnly To)
{
	public static RebalanceOptions Default { get; } = new([DayOfWeek.Saturday, DayOfWeek.Sunday], 1, new TimeOnly(9, 0), new TimeOnly(17, 0));

	public const int MaxPerDay = 12;

	/// <summary>What is wrong with these options, in words; empty when they can be planned with.</summary>
	public IReadOnlyList<string> Problems =>
	[
		.. RestDays.Distinct().Count() >= 7 ? ["Leave at least one day of the week to post on."] : Array.Empty<string>(),
		.. PerDay is < 1 or > MaxPerDay ? [$"Posts per day is between 1 and {MaxPerDay}."] : Array.Empty<string>(),
		.. From >= To ? ["The posting hours end after they start."] : Array.Empty<string>()
	];
}

/// <summary>A campaign to rebalance, and the posts of it that stay where they are because something of them already went out.</summary>
public sealed record Rebalanceable(Campaign Campaign, IReadOnlySet<string> Fixed);

/// <summary>A post moved by a rebalance, from its time to its new one.</summary>
public sealed record PostMove(string Campaign, string Post, DateTimeOffset From, DateTimeOffset To);

/// <summary>
/// Spreads posts that have not gone out over the days and hours the owner allows. A post whose time already keeps the rules
/// stays; one that does not moves to the first allowed slot after it. Nothing moves earlier than it was or than now, a
/// campaign's posts that can still move keep their order and at least their spacing (a day at the least), and posts that
/// went out stay where they are and count.
/// </summary>
public static class Rebalancer
{
	/// <summary>How far ahead a post is looked for a place; one that finds none in a year stays where it is.</summary>
	public const int HorizonDays = 366;

	public static IReadOnlyList<PostMove> Plan(IReadOnlyList<Rebalanceable> campaigns, RebalanceOptions options, DateTimeOffset now, TimeZoneInfo zone)
	{
		if (options.Problems.Count > 0)
		{
			return [];
		}
		var used = new Dictionary<(DateOnly, Network), int>();
		var taken = new HashSet<(DateOnly, Network, int)>();
		var slots = Enumerable.Range(0, options.PerDay).Select(i => options.From.Add((options.To - options.From) * i / options.PerDay)).ToArray();

		DateTime Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, zone).DateTime;
		DateTimeOffset At(DateOnly day, TimeOnly time)
		{
			var local = day.ToDateTime(time);
			return new DateTimeOffset(local, zone.GetUtcOffset(local));
		}
		void Take(DateOnly day, IEnumerable<Network> networks, int slot)
		{
			foreach (var network in networks)
			{
				used[(day, network)] = used.GetValueOrDefault((day, network)) + 1;
				_ = taken.Add((day, network, slot));
			}
		}
		int SlotOf(TimeOnly time) => Array.FindLastIndex(slots, s => s <= time) is var i and >= 0 ? i : 0;

		// What went out (or is going out) stays, and takes its place first.
		foreach (var (campaign, fixedPosts) in campaigns)
		{
			foreach (var post in campaign.Posts.Where(p => fixedPosts.Contains(p.Id)))
			{
				var local = Local(post.At);
				Take(DateOnly.FromDateTime(local), post.NetworksIn(campaign), SlotOf(TimeOnly.FromDateTime(local)));
			}
		}

		var moves = new List<PostMove>();
		var order = campaigns
			.Select(c => (c.Campaign, c.Fixed, Movable: c.Campaign.Posts.Where(p => !c.Fixed.Contains(p.Id)).OrderBy(p => p.At).ThenBy(p => p.Id, StringComparer.Ordinal).ToList()))
			.Where(c => c.Movable.Count > 0)
			.OrderBy(c => c.Movable[0].At)
			.ThenBy(c => c.Campaign.Slug, StringComparer.Ordinal);
		foreach (var (campaign, fixedPosts, movable) in order)
		{
			var posts = campaign.Posts.OrderBy(p => p.At).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
			DateTimeOffset? previousPlaced = null;
			DateTimeOffset? previousOriginal = null;
			foreach (var post in posts)
			{
				// A post that went out has its place already; spacing is kept between the posts that can still move, so an overdue
				// post passing one that went out does not move again on the next rebalance.
				if (fixedPosts.Contains(post.Id))
				{
					continue;
				}
				var networks = post.NetworksIn(campaign);
				var earliest = post.At > now ? post.At : now;
				if (previousPlaced is { } placed && previousOriginal is { } original)
				{
					var gap = post.At - original;
					var after = placed + (gap < TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : gap);
					earliest = after > earliest ? after : earliest;
				}
				var to = Place(earliest) ?? post.At;
				previousPlaced = to;
				previousOriginal = post.At;
				if (to != post.At)
				{
					moves.Add(new PostMove(campaign.Slug, post.Id, post.At, to));
				}

				DateTimeOffset? Place(DateTimeOffset from)
				{
					var start = Local(from);
					// Where it is now is kept when it keeps the rules: a rebalance moves only what it has to.
					var here = Local(post.At);
					var hereDay = DateOnly.FromDateTime(here);
					var hereTime = TimeOnly.FromDateTime(here);
					if (post.At >= from && Allowed(hereDay) && hereTime >= options.From && hereTime <= options.To && networks.All(n => used.GetValueOrDefault((hereDay, n)) < options.PerDay))
					{
						Take(hereDay, networks, SlotOf(hereTime));
						return post.At;
					}
					for (var d = 0; d < HorizonDays; d++)
					{
						var day = DateOnly.FromDateTime(start).AddDays(d);
						if (!Allowed(day) || networks.Any(n => used.GetValueOrDefault((day, n)) >= options.PerDay))
						{
							continue;
						}
						for (var s = 0; s < slots.Length; s++)
						{
							var at = At(day, slots[s]);
							if (at < from || networks.Any(n => taken.Contains((day, n, s))))
							{
								continue;
							}
							Take(day, networks, s);
							return at;
						}
					}
					return null;
				}
			}
		}
		return moves;

		bool Allowed(DateOnly day) => !options.RestDays.Contains(day.DayOfWeek);
	}

	/// <summary>The campaign with the moves that are its own applied.</summary>
	public static Campaign Apply(Campaign campaign, IEnumerable<PostMove> moves)
	{
		var mine = moves.Where(m => m.Campaign == campaign.Slug).ToDictionary(m => m.Post, m => m.To);
		return mine.Count == 0 ? campaign : campaign with { Posts = [.. campaign.Posts.Select(p => mine.TryGetValue(p.Id, out var to) ? p with { At = to } : p)] };
	}
}
