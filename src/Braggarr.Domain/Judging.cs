namespace Braggarr.Domain;

/// <summary>
/// One thing the owner wants every proposal checked for, in their own words: a file under <c>judges/</c> in the posts repo. A
/// failure of a blocking criterion stops approval; a warning is shown and keeps the judge from approving by itself.
/// </summary>
public sealed record Criterion(string Name, string Prompt, bool Blocks)
{
	/// <summary>The networks it is about; every network when null.</summary>
	public ValueList<Network>? Networks { get; init; }

	public bool AppliesTo(Network network) => Networks is null || Networks.Contains(network);
}

/// <summary>What the judge said about one criterion for one variant (a post on a network).</summary>
public sealed record Finding(string Criterion, string Post, Network Network, bool Passed, bool Blocks, string Reason);

/// <summary>
/// The judge's verdict on one version of a campaign, against one version of the criteria: both fingerprints, so a change to
/// either means it is judged again. Not part of what goes out, so it is not in the campaign's digest.
/// </summary>
public sealed record Judgement(string Digest, string Criteria, DateTimeOffset At, string Model, ValueList<Finding> Findings)
{
	/// <summary>A blocking criterion failed somewhere: nobody approves it as it is.</summary>
	public bool Blocked => Findings.Any(f => !f.Passed && f.Blocks);

	/// <summary>Every criterion passed everywhere, warnings included: what the judge needs to approve by itself.</summary>
	public bool AllPassed => Findings.All(f => f.Passed);
}

public static class Criteria
{
	/// <summary>A fingerprint of the criteria: their names, prompts, whether they block and where they apply.</summary>
	public static string Digest(IEnumerable<Criterion> criteria)
	{
		var text = string.Join('\u001e', criteria.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c =>
			$"{c.Name}\u001f{c.Blocks}\u001f{(c.Networks is null ? "*" : string.Join(',', c.Networks))}\u001f{c.Prompt}"));
		return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
	}

	/// <summary>
	/// What a complete judgement covers: every criterion, for every variant on a network it applies to. A judge that says
	/// nothing about one of these has not judged it.
	/// </summary>
	public static IReadOnlyList<(Criterion Criterion, string Post, Network Network)> Expected(Campaign campaign, IEnumerable<Criterion> criteria) =>
	[
		.. from c in criteria
		from p in campaign.Posts
		from v in p.Variants
		where c.AppliesTo(v.Network)
		select (c, p.Id, v.Network)
	];
}
