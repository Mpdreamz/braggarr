namespace Braggarr.Domain;

/// <summary>
/// Lays a campaign's talking points out as posts, by the shape of each network: on the short networks every point gets a post of
/// its own, one every few days from the start; LinkedIn and Reddit get one post with the top points (all of them, when no short
/// network carries the rest); Hacker News a title for the first; YouTube each point that wants a video, with that point's post.
/// The blog gets an article with every point, first: the other posts start once its site has had time to build, so they can
/// link it. The posts have no text yet: drafting writes it, to each post's points.
/// </summary>
public static class Layout
{
	public const int LongPostPoints = 5;

	public static readonly TimeSpan DefaultSpacing = TimeSpan.FromDays(2);

	public static ValueList<Post> Lay(IReadOnlyList<TalkingPoint> points, IReadOnlyList<Network> networks, DateTimeOffset start, TimeSpan spacing)
	{
		if (points.Count == 0 || networks.Count == 0)
		{
			return [];
		}
		var shorts = networks.Where(n => n.Shape() == NetworkShape.Stream).Order().ToList();
		var longs = networks.Where(n => n.Shape() == NetworkShape.Article).Order().ToList();
		var titles = networks.Where(n => n.Shape() == NetworkShape.Title).Order().ToList();
		var videos = networks.Where(n => n.Shape() == NetworkShape.Video).Order().ToList();
		var articles = networks.Where(n => n.Shape() == NetworkShape.Longform).Order().ToList();
		var posts = new List<Post>();

		if (articles.Count > 0)
		{
			posts.Add(new Post("00-blog", start, []) { Networks = [.. articles], Points = [.. Enumerable.Range(0, points.Count)], Angle = "Every point, as an article" });
			start += Blogging.DeployMargin;
		}

		if (longs.Count > 0)
		{
			// With short networks to carry the rest, the long post takes the top points; without them, it is the only place any
			// point is said, so it takes them all.
			var top = shorts.Count > 0 ? Math.Min(points.Count, LongPostPoints) : points.Count;
			posts.Add(new Post("00-overview", start, []) { Networks = [.. longs], Points = [.. Enumerable.Range(0, top)], Angle = "The top points together" });
		}
		for (var i = 0; i < points.Count; i++)
		{
			// Hacker News gets the first point, as a title, with the short networks' first post; YouTube each point that wants a video.
			var wantsVideo = points[i].Wanted.Any(w => w.Kind == MediaKind.Video);
			List<Network> on = [.. shorts, .. i == 0 ? titles : [], .. wantsVideo ? videos : []];
			on.Sort();
			if (on.Count == 0)
			{
				continue;
			}
			posts.Add(new Post(PostId(i, points[i].Text), start + (spacing * i), []) { Networks = [.. on], Points = [i], Angle = points[i].Text });
		}
		return posts.ToValueList();
	}

	private static string PostId(int index, string point)
	{
		var stem = Slugs.From(point);
		stem = stem.Length > 30 ? stem[..30].TrimEnd('-') : stem;
		return $"{index + 1:00}-{(stem.Length > 0 ? stem : "point")}";
	}
}
