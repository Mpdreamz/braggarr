using System.Security.Claims;
using Braggarr.Domain;
using Braggarr.Services;
using Braggarr.Web.Auth;
using Nullean.Entities.AspNetCore;

namespace Braggarr.Web.Api;

/// <summary>
/// <c>/api/v1</c>, for agents with a key. Everything an agent sends becomes a proposal: there is no endpoint that approves, merges
/// or publishes, so no key can be given that power.
/// </summary>
public static partial class CampaignApi
{
	[System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
	private static partial System.Text.RegularExpressions.Regex RepositoryName();

	public static void MapCampaignApi(this WebApplication app)
	{
		var api = app.MapGroup("/api/v1").RequireAuthorization(Roles.AgentPolicy).RequireRateLimiting("per-key").DisableAntiforgery();

		_ = api.MapGet("/campaigns", async (CampaignService campaigns, SchedulingService scheduling, CancellationToken ct) =>
		{
			var proposals = await campaigns.ProposalsAsync(ct);
			return Results.Ok(proposals.Select(p => CampaignView.From(p.Campaign, scheduling.Current, p))
				.Concat(scheduling.Current.Campaigns.Select(c => CampaignView.From(c, scheduling.Current, null))));
		});

		_ = api.MapGet("/campaigns/{slug}", async (string slug, CampaignService campaigns, SchedulingService scheduling, CancellationToken ct) =>
		{
			if (scheduling.Current.Campaign(slug) is { } live)
			{
				return Results.Ok(CampaignView.From(live, scheduling.Current, null));
			}
			var proposal = (await campaigns.ProposalsAsync(ct)).FirstOrDefault(p => p.Campaign.Slug == slug);
			return proposal is null ? Results.NotFound() : Results.Ok(CampaignView.From(proposal.Campaign, scheduling.Current, proposal));
		});

		_ = api.MapPost("/campaigns", async (CampaignRequest request, ClaimsPrincipal user, CampaignService campaigns, SchedulingService scheduling, CancellationToken ct) =>
			await campaigns.ProposeAsync(request.ToDraft(), Agent(user), ct) switch
			{
				Outcome<Proposal>.Done(var p) => Results.Created($"/api/v1/campaigns/{p.Campaign.Slug}", CampaignView.From(p.Campaign, scheduling.Current, p)),
				Outcome<Proposal>.Refused(var reasons) => Refused(reasons),
				_ => Results.StatusCode(500)
			})
			.ValidateWith<RouteHandlerBuilder, CampaignRequest>(CampaignRequestRules.All);

		// One article about a public project, as a small campaign of its own the owner reviews.
		_ = api.MapPost("/blog", async (BlogRequest request, ClaimsPrincipal user, CampaignService campaigns, SchedulingService scheduling, CancellationToken ct) =>
			await campaigns.ProposeAsync(request.ToDraft(campaigns.NextMorning()), Agent(user), ct) switch
			{
				Outcome<Proposal>.Done(var p) => Results.Created($"/api/v1/campaigns/{p.Campaign.Slug}", CampaignView.From(p.Campaign, scheduling.Current, p)),
				Outcome<Proposal>.Refused(var reasons) => Refused(reasons),
				_ => Results.StatusCode(500)
			})
			.ValidateWith<RouteHandlerBuilder, BlogRequest>(BlogRequestRules.All);

		_ = api.MapMethods("/campaigns/{slug}", ["PATCH"], async (string slug, CampaignRequest request, ClaimsPrincipal user, CampaignService campaigns,
			SchedulingService scheduling, CancellationToken ct) =>
			await campaigns.UpdateProposalAsync(slug, request.ToDraft(), Agent(user), ct) switch
			{
				Outcome<Proposal>.Done(var p) => Results.Ok(CampaignView.From(p.Campaign, scheduling.Current, p)),
				Outcome<Proposal>.Refused(var reasons) => Refused(reasons),
				_ => Results.StatusCode(500)
			})
			.ValidateWith<RouteHandlerBuilder, CampaignRequest>(CampaignRequestRules.All);

		_ = api.MapPost("/campaigns/{slug}/posts", async (string slug, PostRequest post, ClaimsPrincipal user, CampaignService campaigns,
			SchedulingService scheduling, CancellationToken ct) =>
			await campaigns.AddPostAsync(slug, post.ToDraft(), Agent(user), ct) switch
			{
				Outcome<Proposal>.Done(var p) => Results.Ok(CampaignView.From(p.Campaign, scheduling.Current, p)),
				Outcome<Proposal>.Refused(var reasons) => Refused(reasons),
				_ => Results.StatusCode(500)
			});

		_ = api.MapPost("/campaigns/{slug}/posts/{post}/redraft", async (string slug, string post, RedraftRequest request, ClaimsPrincipal user,
			CampaignService campaigns, SchedulingService scheduling, CancellationToken ct) =>
		{
			if (!Networks.TryParse(request.Network, out var network))
			{
				return Refused([$"\"{request.Network}\" is not a network."]);
			}
			return await campaigns.RedraftAsync(slug, post, network, request.Note, Agent(user), ct) switch
			{
				Outcome<Proposal>.Done(var p) => Results.Ok(CampaignView.From(p.Campaign, scheduling.Current, p)),
				Outcome<Proposal>.Refused(var reasons) => Refused(reasons),
				_ => Results.StatusCode(500)
			};
		});

		// An image or an MP4 video for one post of a proposal: multipart with "file", "post", optionally "networks" (comma separated) and "alt".
		_ = api.MapPost("/campaigns/{slug}/media", async (string slug, IFormFile file, [Microsoft.AspNetCore.Mvc.FromForm] string post,
			[Microsoft.AspNetCore.Mvc.FromForm] string? networks, [Microsoft.AspNetCore.Mvc.FromForm] string? alt, ClaimsPrincipal user,
			CampaignService campaigns, SchedulingService scheduling, CancellationToken ct) =>
		{
			var bytes = await ReadAsync(file, ct);
			if (bytes is null)
			{
				return Refused([$"An image is at most {CampaignService.MaxImageBytes / 1000} kB, a video {CampaignService.MaxVideoBytes / 1_000_000} MB."]);
			}
			var on = (networks ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(n => Networks.TryParse(n, out var network) ? network : (Network?)null).OfType<Network>().ToList();
			return await campaigns.AddMediaAsync(slug, file.FileName, bytes, post, on, alt, Agent(user), ct) switch
			{
				Outcome<Proposal>.Done(var p) => Results.Ok(CampaignView.From(p.Campaign, scheduling.Current, p)),
				Outcome<Proposal>.Refused(var reasons) => Refused(reasons),
				_ => Results.StatusCode(500)
			};
		}).DisableAntiforgery()
			.WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxUploadBytes))
			.WithFormOptions(multipartBodyLengthLimit: MaxUploadBytes);

		// An introduction campaign for a repository, planned and drafted in the background: answered at once with 202.
		_ = api.MapPost("/projects/introduce", (IntroduceRequest request, Introducer introducer, Hosting.BackgroundWork work) =>
		{
			if (!RepositoryName().IsMatch(request.Repository ?? ""))
			{
				return Refused(["Name the repository as owner/name."]);
			}
			var networks = (request.Networks ?? []).Select(n => Networks.TryParse(n, out var network) ? network : (Network?)null).OfType<Network>().ToList();
			if (networks.Count == 0)
			{
				return Refused(["Name at least one network."]);
			}
			var name = string.IsNullOrWhiteSpace(request.Name) ? request.Repository![(request.Repository!.IndexOf('/') + 1)..] : request.Name.Trim();
			work.Enqueue($"Introducing {request.Repository}", ct => introducer.ProposeAsync(request.Repository!, name, networks, ct));
			return Results.Accepted(value: new Accepted($"Planning an introduction of {request.Repository}; it opens as a proposal when it is ready."));
		});

		// What is already planned, so an agent can pick times that do not crowd others.
		_ = api.MapGet("/calendar", (DateTimeOffset? from, DateTimeOffset? to, SchedulingService scheduling, TimeProvider time) =>
		{
			var start = from ?? time.GetUtcNow().AddDays(-1);
			var end = to ?? start.AddDays(30);
			return Results.Ok<IEnumerable<CalendarEntry>>(
				from c in scheduling.Current.Campaigns
				from p in c.Posts
				where p.At >= start && p.At <= end
				orderby p.At
				select new CalendarEntry(c.Slug, p.Id, p.At, c.Status.ToString().ToLowerInvariant(), p.NetworksIn(c).Select(n => n.Slug()))
			);
		});
	}

	/// <summary>The most an upload may weigh on the wire: the largest video, and the form around it.</summary>
	public const long MaxUploadBytes = CampaignService.MaxVideoBytes + 1_000_000;

	/// <summary>The upload's bytes, or null when it is larger than an image (or, for an MP4, a video) may be.</summary>
	internal static async Task<byte[]?> ReadAsync(IFormFile file, CancellationToken ct)
	{
		var video = MediaFile.ContentTypeOf(file.FileName) == "video/mp4";
		if (file.Length > (video ? CampaignService.MaxVideoBytes : CampaignService.MaxImageBytes))
		{
			return null;
		}
		using var buffer = new MemoryStream();
		await file.CopyToAsync(buffer, ct);
		return buffer.ToArray();
	}

	private static Actor Agent(ClaimsPrincipal user) => Actor.Agent(user.Identity?.Name ?? "unknown");

	private static IResult Refused(IReadOnlyList<string> reasons) =>
		Results.ValidationProblem(new Dictionary<string, string[]> { ["campaign"] = [.. reasons] }, title: "The campaign was not proposed.");
}

public sealed record IntroduceRequest(string? Repository, string? Name, string[]? Networks);

/// <summary>What an agent is told when its request goes on in the background.</summary>
public sealed record Accepted(string Message);

/// <summary>One post already planned, on the calendar an agent reads.</summary>
public sealed record CalendarEntry(string Campaign, string Post, DateTimeOffset At, string Status, IEnumerable<string> Networks);

/// <summary>An attached file, or a slot still wanted (<c>wanted:image</c>, <c>wanted:video</c>) with what it should show as its alt.</summary>
public sealed record MediaView(string Path, string Alt, bool Wanted);

/// <remarks><see cref="Limit"/> is 0 on the blog, which has none.</remarks>
public sealed record VariantView(string Network, string Text, int Length, int Limit, bool Fits, IReadOnlyList<MediaView> Media, string? Delivery, string? Url)
{
	/// <summary>A blog post's title and the rest; only there for the blog.</summary>
	[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
	public ArticleView? Article { get; init; }
}

public sealed record ArticleView(string Title, string Description, IReadOnlyList<string> Tags, string Slug, string? Cover, string? CoverAlt);

/// <summary>A post: where it goes, which points it covers (from 1), and its variants.</summary>
public sealed record PostView(string Id, DateTimeOffset At, string? Angle, IReadOnlyList<string> Networks, IReadOnlyList<int> Points, IReadOnlyList<VariantView> Variants);

public sealed record WantedView(string Kind, string Description);

public sealed record PointView(int Number, string Text, IReadOnlyList<WantedView> Wanted);

/// <summary>
/// The judge's verdict: <see cref="OfThisVersion"/> is false once the campaign changed since, and the judge reads it again.
/// A failure that <see cref="FindingView.Blocks"/> stops approval until the post is changed.
/// </summary>
public sealed record JudgementView(bool OfThisVersion, bool Blocked, bool AllPassed, string Model, DateTimeOffset At, IReadOnlyList<FindingView> Findings);

public sealed record FindingView(string Criterion, string Post, string Network, bool Passed, bool Blocks, string Reason);

public sealed record CampaignView(
	string Slug,
	string Title,
	string Project,
	string Status,
	IReadOnlyList<string> Networks,
	IReadOnlyList<string> Links,
	string Brief,
	string CreatedBy,
	int? PullRequest,
	string? PullRequestUrl,
	IReadOnlyList<PostView> Posts
)
{
	public int RepeatMonths { get; init; }

	/// <summary>The talking points, most important first, numbered from 1 as posts name them.</summary>
	public IReadOnlyList<PointView> Points { get; init; } = [];

	/// <summary>The account it posts as on each network, by network name.</summary>
	public IReadOnlyDictionary<string, string> Accounts { get; init; } = new Dictionary<string, string>();

	/// <summary>What the judge said about the campaign; null when it has not judged it.</summary>
	public JudgementView? Judgement { get; init; }

	public static CampaignView From(Campaign c, Schedule schedule, Proposal? proposal) =>
		new(
			c.Slug, c.Title, c.Project, c.Status.ToString().ToLowerInvariant(), [.. c.Networks.Select(n => n.Slug())], [.. c.Links], c.Brief, c.CreatedBy,
			proposal?.Number, proposal?.Url,
			[
				.. c.Posts.Select(p => new PostView(
					p.Id,
					p.At,
					p.Angle,
					[.. p.NetworksIn(c).Select(n => n.Slug())],
					[.. p.Points.Select(i => i + 1)],
					[
						.. p.Variants.Select(v =>
						{
							var delivery = proposal is null ? schedule.Find(new DeliveryKey(c.Slug, p.Id, v.Network)) : null;
							return new VariantView(
								v.Network.Slug(), v.Text, Lengths.Measure(v.Network, v.Text), v.Network == Network.Blog ? 0 : Lengths.Limit(v.Network), v.Fits && v.MediaFits,
								[.. v.Media.Select(m => new MediaView(m.Path, m.Alt, m.IsWanted))],
								delivery?.State.ToString().ToLowerInvariant(), delivery?.Receipt?.Url
							)
							{
								Article = v.Article is { } a ? new ArticleView(a.Title, a.Description, [.. a.Tags], a.Path, a.Cover?.Path, a.Cover?.Alt) : null
							};
						})
					]
				))
			]
		)
		{
			RepeatMonths = c.RepeatMonths,
			Accounts = c.Accounts.ToDictionary(a => a.Network.Slug(), a => a.Account),
			Judgement = c.Judgement is { } j
				? new JudgementView(j.Digest == c.Digest, j.Blocked, j.AllPassed, j.Model, j.At,
					[.. j.Findings.Select(f => new FindingView(f.Criterion, f.Post, f.Network.Slug(), f.Passed, f.Blocks, f.Reason))])
				: null,
			Points = [.. c.Points.Select((t, i) => new PointView(i + 1, t.Text, [.. t.Wanted.Select(w => new WantedView(w.Kind.ToString().ToLowerInvariant(), w.Description))]))]
		};
}
