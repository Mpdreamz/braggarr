using Braggarr.Domain;
using Braggarr.Web.Api;
using CsCheck;
using Nullean.Entities.CsCheck;

namespace Braggarr.Web.Tests;

/// <summary>What an agent's request must say before it reaches the services, over every kind of request an agent sends.</summary>
public class CampaignRequestRulesTests
{
	private static readonly Gen<string> Network = Gen.OneOfConst(Networks.All.Select(n => n.Slug()).ToArray());

	private static readonly Gen<WantedRequest> Wanted =
		from kind in Gen.OneOfConst("image", "video")
		from description in Gen.OneOfConst("A diff where one line changes", "A terminal run")
		select new WantedRequest(kind, description);

	private static readonly Gen<PointRequest> Point =
		from text in Gen.OneOfConst("Keeps diffs small", "Fast", "Free and open source")
		from wanted in Wanted.Array[0, 2]
		select new PointRequest(text, wanted.Length == 0 ? null : wanted);

	private static readonly Gen<PostRequest> Post =
		from networks in Network.Array[1, 2]
		from texts in Gen.Bool
		select new PostRequest(null, new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(1)),
			texts ? networks.Distinct().ToDictionary(n => n, n => "Out.") : null, [.. networks.Distinct()]);

	/// <summary>The three ways to say what a campaign is about: points (preferred), a brief alone, or posts.</summary>
	private static readonly Gen<CampaignRequest> Requests =
		from networks in Network.Array[1, 3]
		from shape in Gen.Int[0, 2]
		from points in Point.Array[1, 4]
		from posts in Post.Array[1, 2]
		select new CampaignRequest(
			null, "Curb 1.0", "curb", [.. networks.Distinct()], ["https://github.com/nullean/curb"],
			shape == 1 ? "Curb formats C#." : null,
			shape == 2 ? posts : null,
			null,
			shape == 0 ? points : null
		);

	public static IEnumerable<Func<AxisCase>> Axes() =>
		AutoTest.For(CampaignRequestRules.Instance, Requests)
			.CounterExample(CampaignRequestRules.NamesNetworks, r => r with { Networks = [] })
			.CounterExample(CampaignRequestRules.KnownNetworks, r => r with { Networks = [.. r.Networks, "myspace"] })
			.CounterExample(CampaignRequestRules.SaysSomething, r => r with { Points = null, Posts = null, Brief = " " })
			.CounterExample(
				CampaignRequestRules.PointsSaySomething,
				r => r.Points is { Length: > 0 } points ? r with { Points = [points[0] with { Wanted = [new WantedRequest("gif", "A diff")] }, .. points[1..]] } : r
			)
			.AsDataSource();

	[Test]
	[MethodDataSource(nameof(Axes))]
	public Task EveryDeclaredAxisHolds(AxisCase axis) => axis.RunAsync();
}
