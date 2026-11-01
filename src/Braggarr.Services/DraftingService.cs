using Braggarr.Domain;
using Microsoft.Extensions.Logging;

namespace Braggarr.Services;

/// <summary>
/// Has variants written, and checks each against its network's length rule. One that does not fit is asked for again, with the
/// count and the limit, up to three times in all; what still does not fit is left out, so the proposal shows it as missing rather
/// than carrying a post that would be refused.
/// </summary>
public sealed class DraftingService(IDrafters drafters, ICampaignRepository repository, IUsageLog usage, TimeProvider time, ILogger<DraftingService> logger)
{
	public const int Attempts = 3;

	public bool IsAvailable => drafters.Current is not null;

	/// <summary>The owner's brief for a project, by its name; null when it has none.</summary>
	public async Task<ProjectBrief?> AboutAsync(string project, CancellationToken ct)
	{
		var slug = ProjectBrief.Slug(project);
		return slug.Length == 0 ? null : (await repository.BriefsAsync(ct)).FirstOrDefault(b => ProjectBrief.Slug(b.Project) == slug);
	}

	/// <summary>Writes every variant the draft is missing. Returns the filled draft and, in words, what could not be written.</summary>
	public async Task<(CampaignDraft Draft, IReadOnlyList<string> Problems)> FillAsync(CampaignDraft draft, string? note, CancellationToken ct)
	{
		var targets = (
			from p in draft.Posts
			from n in p.Networks ?? draft.Networks
			where !p.Variants.ContainsKey(n) || string.IsNullOrWhiteSpace(p.Variants[n])
			select new DraftTarget(p.Id!, n)
		).ToList();
		if (targets.Count == 0 || drafters.Current is null)
		{
			return (draft, []);
		}

		var posts = draft.Posts
			.Select(p => new Post(p.Id!, p.At, p.Variants.Where(v => !string.IsNullOrWhiteSpace(v.Value)).Select(v => new Variant(v.Key, v.Value) { Article = v.Key == Network.Blog ? p.Article : null }).ToValueList())
			{
				Angle = p.Angle,
				Networks = p.Networks?.ToValueList(),
				Points = [.. p.Points]
			})
			.ToList();
		var (written, problems) = await WriteAsync(
			new DraftRequest(draft.Title, draft.Project, draft.Brief, draft.Links, posts, targets, null, note) { Points = draft.Points },
			ct
		);

		var filled = draft with
		{
			Posts =
			[
				.. draft.Posts.Select(p => p with
				{
					Variants = p.Variants
						.Where(v => !string.IsNullOrWhiteSpace(v.Value))
						.Concat(written.Where(w => w.Post == p.Id).Select(w => KeyValuePair.Create(w.Variant.Network, w.Variant.Text)))
						.ToDictionary(),
					Article = written.FirstOrDefault(w => w.Post == p.Id && w.Variant.Article is not null)?.Variant.Article ?? p.Article
				})
			]
		};
		return (filled, problems);
	}

	/// <summary>Writes one variant again, with the owner's note, keeping the rest of the campaign as it is.</summary>
	public async Task<Outcome<Campaign>> RedraftAsync(Campaign campaign, string postId, Network network, string? note, CancellationToken ct)
	{
		if (drafters.Current is null)
		{
			return new Outcome<Campaign>.Refused("No model is set up for drafting. Add a Claude API key in Settings.");
		}
		var post = campaign.Posts.FirstOrDefault(p => p.Id == postId);
		if (post is null || !post.NetworksIn(campaign).Contains(network))
		{
			return new Outcome<Campaign>.Refused($"{campaign.Slug} has no {network} variant for {postId}.");
		}
		var target = new DraftTarget(postId, network, Previous: post.For(network)?.Text);
		var (written, problems) = await WriteAsync(
			new DraftRequest(campaign.Title, campaign.Project, campaign.Brief, campaign.Links, campaign.Posts, [target], null, note) { Points = campaign.Points },
			ct
		);
		if (written.Count == 0)
		{
			return new Outcome<Campaign>.Refused(problems);
		}
		var variant = written[0].Variant with { Media = post.For(network)?.Media ?? [] };
		var updated = post with { Variants = post.Variants.Where(v => v.Network != network).Append(variant).OrderBy(v => v.Network).ToValueList() };
		return new Outcome<Campaign>.Done(campaign with { Posts = campaign.Posts.Replace(p => p.Id == postId, updated) });
	}

	/// <summary>Alt text for an image, from the model; null when none is set up or it could not say.</summary>
	public async Task<string?> DescribeAsync(MediaFile image, string context, CancellationToken ct)
	{
		if (drafters.Current is not { } drafter)
		{
			return null;
		}
		try
		{
			var (alt, cost) = await drafter.DescribeAsync(image, context, ct);
			usage.Record(cost, $"describe {image.Path}", time.GetUtcNow());
			alt = alt.Trim();
			return alt.Length is > 0 and <= Lengths.AltLimit ? alt : null;
		}
		catch (DraftException e)
		{
			logger.LogWarning("Describing {Image} failed: {Problem}", image.Path, e.Message);
			return null;
		}
	}

	/// <summary>A planned introduction campaign, or null when no model is set up or it could not plan one.</summary>
	public async Task<CampaignPlan?> PlanAsync(RepositoryInfo repository, CancellationToken ct)
	{
		if (drafters.Current is not { } drafter)
		{
			return null;
		}
		try
		{
			var (plan, cost) = await drafter.PlanAsync(repository, ct);
			usage.Record(cost, $"plan {repository.Repository}", time.GetUtcNow());
			return plan;
		}
		catch (DraftException e)
		{
			logger.LogWarning("Planning an introduction for {Repository} failed: {Problem}", repository.Repository, e.Message);
			return null;
		}
	}

	/// <summary>Talking points for a brief, most important first; null when no model is set up or it could not say.</summary>
	public async Task<IReadOnlyList<TalkingPoint>?> PointsAsync(PointsRequest request, CancellationToken ct)
	{
		if (drafters.Current is not { } drafter || string.IsNullOrWhiteSpace(request.Brief))
		{
			return null;
		}
		try
		{
			var (points, cost) = await drafter.PointsAsync(request with { About = request.About ?? await AboutAsync(request.Project, ct) }, ct);
			usage.Record(cost, $"points {request.Title}", time.GetUtcNow());
			return [.. points.Where(p => !string.IsNullOrWhiteSpace(p.Text)).Take(MaxPoints)];
		}
		catch (DraftException e)
		{
			logger.LogWarning("Finding talking points for {Title} failed: {Problem}", request.Title, e.Message);
			return null;
		}
	}

	/// <summary>The most points a campaign is laid out from: one short post each, two days apart, is two weeks.</summary>
	public const int MaxPoints = 8;

	/// <summary>
	/// The model's call on a release, or null when no model is set up or it could not say: then the release is proposed, since
	/// a release dropped by an error would never be seen.
	/// </summary>
	public async Task<ReleaseVerdict?> JudgeAsync(string project, Release release, CancellationToken ct)
	{
		if (drafters.Current is not { } drafter)
		{
			return null;
		}
		try
		{
			var (verdict, cost) = await drafter.JudgeAsync(project, release, ct);
			usage.Record(cost, $"judge {project} {release.Tag}", time.GetUtcNow());
			return verdict;
		}
		catch (DraftException e)
		{
			logger.LogWarning("Judging {Project} {Tag} failed; proposing it: {Problem}", project, release.Tag, e.Message);
			return null;
		}
	}

	private async Task<(IReadOnlyList<DraftedVariant> Written, IReadOnlyList<string> Problems)> WriteAsync(DraftRequest request, CancellationToken ct)
	{
		var drafter = drafters.Current!;
		var voice = await repository.VoiceAsync(ct);
		var about = request.About ?? await AboutAsync(request.Project, ct);
		var written = new List<DraftedVariant>();
		var pending = request.Targets.ToList();
		var problems = new List<string>();

		for (var attempt = 1; attempt <= Attempts && pending.Count > 0; attempt++)
		{
			DraftResult result;
			try
			{
				result = await drafter.DraftAsync(request with { Targets = pending, Voice = voice, About = about }, ct);
			}
			catch (DraftException e)
			{
				logger.LogWarning("Drafting failed: {Problem}", e.Message);
				problems.Add($"Drafting failed: {e.Message}");
				return (written, problems);
			}
			usage.Record(result.Usage, $"draft {request.Title}", time.GetUtcNow());

			var retry = new List<DraftTarget>();
			foreach (var target in pending)
			{
				var returned = result.Variants.FirstOrDefault(v => v.Post == target.Post && v.Variant.Network == target.Network)?.Variant;
				var text = returned?.Text?.Trim();
				if (text is null || text.Length == 0)
				{
					retry.Add(target with { Problem = "No text came back for this one." });
				}
				else if (target.Network == Network.Blog && returned!.Article is not { Fits: true })
				{
					retry.Add(target with
					{
						Problem = $"The article needs a title of 1 to {Blogging.TitleLimit} characters, a description of 1 to {Blogging.DescriptionLimit} and lowercase tags.",
						Previous = text
					});
				}
				else if (!Lengths.Fits(target.Network, text))
				{
					retry.Add(target with
					{
						Problem = $"It is {Lengths.Measure(target.Network, text)} long as {target.Network} counts it; the limit is {Lengths.Limit(target.Network)}. Shorten it.",
						Previous = text
					});
				}
				else
				{
					written.Add(new DraftedVariant(target.Post, target.Network == Network.Blog ? returned! with { Text = text, Media = Blogging.ImagesIn(text) } : new Variant(target.Network, text)));
				}
			}
			pending = retry;
		}

		problems.AddRange(pending.Select(t => $"{t.Post}: no {t.Network} variant that fits after {Attempts} tries. {t.Problem}"));
		return (written, problems);
	}
}
