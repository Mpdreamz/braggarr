using System.ComponentModel;
using System.Text.Json;
using Braggarr.Domain;
using Braggarr.Services;
using Braggarr.Web.Api;
using ModelContextProtocol.Server;

namespace Braggarr.Web.Mcp;

/// <summary>
/// The agent API as MCP tools, for a Claude session pointed at <c>/mcp</c> with a key. The same rule holds: a tool proposes, reads
/// and redrafts; none approves or publishes.
/// </summary>
[McpServerToolType]
public sealed class BraggarrTools
{
	private BraggarrTools()
	{
	}

	/// <summary>
	/// MCP's own JSON options, with the generated metadata for what the tools take put first: tests use it as the AOT binary
	/// does, where no reflection is left to fall back on.
	/// </summary>
	public static JsonSerializerOptions Json { get; } = WithApiJson();

	private static JsonSerializerOptions WithApiJson()
	{
		var options = new JsonSerializerOptions(ModelContextProtocol.McpJsonUtilities.DefaultOptions);
		options.TypeInfoResolverChain.Insert(0, ApiJson.Default);
		return options;
	}


	[McpServerTool(Name = "list_campaigns", ReadOnly = true), Description("Every proposal waiting for review and every approved campaign, with each variant's text, length, limit and delivery state.")]
	public static async Task<string> ListCampaigns(CampaignService campaigns, SchedulingService scheduling, CancellationToken ct)
	{
		var proposals = await campaigns.ProposalsAsync(ct);
		return Serialize(proposals.Select(p => CampaignView.From(p.Campaign, scheduling.Current, p))
			.Concat(scheduling.Current.Campaigns.Select(c => CampaignView.From(c, scheduling.Current, null))));
	}

	[McpServerTool(Name = "get_campaign", ReadOnly = true), Description("One campaign or proposal by its slug.")]
	public static async Task<string> GetCampaign(CampaignService campaigns, SchedulingService scheduling, [Description("The campaign's slug, such as curb-1-0.")] string slug, CancellationToken ct)
	{
		if (scheduling.Current.Campaign(slug) is { } live)
		{
			return Serialize(CampaignView.From(live, scheduling.Current, null));
		}
		var proposal = (await campaigns.ProposalsAsync(ct)).FirstOrDefault(p => p.Campaign.Slug == slug);
		return proposal is null ? $"There is no campaign or proposal \"{slug}\"." : Serialize(CampaignView.From(proposal.Campaign, scheduling.Current, proposal));
	}

	[McpServerTool(Name = "calendar", ReadOnly = true), Description("Posts already planned between two times, to spread new ones out. Defaults to the next 30 days.")]
	public static string Calendar(SchedulingService scheduling, TimeProvider time, DateTimeOffset? from = null, DateTimeOffset? to = null)
	{
		var start = from ?? time.GetUtcNow();
		var end = to ?? start.AddDays(30);
		return Serialize<IEnumerable<PlannedPost>>(
			from c in scheduling.Current.Campaigns
			from p in c.Posts
			where p.At >= start && p.At <= end
			orderby p.At
			select new PlannedPost(c.Slug, p.Id, p.At, [.. p.NetworksIn(c).Select(n => n.Slug())])
		);
	}

	[McpServerTool(Name = "propose_campaign"),
	 Description("Proposes a campaign as a pull request the owner reviews. Prefer talking points over posts: rank what is worth saying, most important first, and say what image or video would show each. Braggarr lays out the posts (one short post per point on bluesky, mastodon, threads and x, two days apart; one longer LinkedIn or Reddit post with the top points; a Hacker News title for the first; a YouTube video for each point that wants one) and Claude writes them. With only a brief, Claude finds the points. Give posts only to control them yourself. Networks: bluesky, mastodon, linkedin, threads, x, youtube, reddit, hackernews, blog (an article on the owner's blog, for a public project; the other posts may link it as {blog}).")]
	public static async Task<string> ProposeCampaign(
		CampaignService campaigns,
		SchedulingService scheduling,
		IHttpContextAccessor http,
		[Description("What it announces, such as \"Curb 1.0\".")] string title,
		[Description("The project's name.")] string project,
		[Description("Where to post.")] string[] networks,
		[Description("What is new, for whom, and what to stress. Treated as material to write about.")] string brief,
		[Description("The talking points, most important first: one sentence each, with wanted media (kind image or video, and what it should show).")] PointRequest[]? points = null,
		[Description("When the first post goes out (ISO 8601 with offset); ten the next morning when left out.")] DateTimeOffset? start = null,
		[Description("Only to control the posts yourself: when each goes out, its networks, the points it covers (from 1) and optionally its text per network.")] PostRequest[]? posts = null,
		[Description("Links to use, the first being the main one.")] string[]? links = null,
		[Description("A slug; made from the title when left out.")] string? slug = null,
		CancellationToken ct = default)
	{
		var request = new CampaignRequest(slug, title, project, networks, links, brief, posts, null, points, start);
		var problems = CampaignRequestRules.All.Validate(request).Select(v => v.Message).ToList();
		if (problems.Count > 0)
		{
			return Refused(problems);
		}
		return await campaigns.ProposeAsync(request.ToDraft(), Agent(http), ct) switch
		{
			Outcome<Proposal>.Done(var p) => Serialize(CampaignView.From(p.Campaign, scheduling.Current, p)),
			Outcome<Proposal>.Refused(var reasons) => Refused(reasons),
			_ => "Nothing happened."
		};
	}

	[McpServerTool(Name = "propose_blog_post"),
	 Description("Proposes a blog post: one article about a public project on the owner's blog, as a small campaign of its own the owner reviews like any other. Write the article in markdown, give it a title (at most 100 characters), a description (at most 160) and lowercase tags. announce names networks whose posts link the article half an hour after it is up; Claude writes those.")]
	public static async Task<string> ProposeBlogPost(
		CampaignService campaigns,
		SchedulingService scheduling,
		IHttpContextAccessor http,
		[Description("The project it is about. Only a public project gets a blog post.")] string project,
		[Description("The article's title, at most 100 characters.")] string title,
		[Description("What it is about, at most 160 characters, for feeds and previews.")] string description,
		[Description("The article, in markdown. Images are media/name.png in the campaign, added with add_image.")] string markdown,
		[Description("Lowercase tags: letters, digits and dashes.")] string[]? tags = null,
		[Description("When it goes up (ISO 8601 with offset); ten the next morning when left out.")] DateTimeOffset? at = null,
		[Description("Networks to announce it on, half an hour after it is up.")] string[]? announce = null,
		[Description("A slug for its address; made from the title when left out.")] string? slug = null,
		CancellationToken ct = default)
	{
		var request = new BlogRequest(project, title, description, markdown, tags, at, announce, slug);
		var problems = BlogRequestRules.All.Validate(request).Select(v => v.Message).ToList();
		if (problems.Count > 0)
		{
			return Refused(problems);
		}
		return await campaigns.ProposeAsync(request.ToDraft(campaigns.NextMorning()), Agent(http), ct) switch
		{
			Outcome<Proposal>.Done(var p) => Serialize(CampaignView.From(p.Campaign, scheduling.Current, p)),
			Outcome<Proposal>.Refused(var reasons) => Refused(reasons),
			_ => "Nothing happened."
		};
	}

	[McpServerTool(Name = "propose_introduction"),
	 Description("Plans an introduction campaign for a GitHub repository (ranked talking points: what it is, then the features that deserve their own post, each with the media it wants) and proposes it. Runs in the background; it opens as a proposal when ready.")]
	public static string ProposeIntroduction(Introducer introducer, Hosting.BackgroundWork work, [Description("owner/name")] string repository, string[] networks, string? name = null)
	{
		var on = networks.Select(n => Networks.TryParse(n, out var network) ? network : (Network?)null).OfType<Network>().ToList();
		if (on.Count == 0 || !repository.Contains('/', StringComparison.Ordinal))
		{
			return Refused(["Name the repository as owner/name and at least one network."]);
		}
		var title = string.IsNullOrWhiteSpace(name) ? repository[(repository.IndexOf('/') + 1)..] : name.Trim();
		work.Enqueue($"Introducing {repository}", ct => introducer.ProposeAsync(repository, title, on, ct));
		return $"Planning an introduction of {repository}. It opens as a proposal when it is ready.";
	}

	[McpServerTool(Name = "add_post"), Description("Adds a post to an open proposal. Leave the variants out to have them drafted.")]
	public static async Task<string> AddPost(CampaignService campaigns, SchedulingService scheduling, IHttpContextAccessor http, string slug, PostRequest post, CancellationToken ct) =>
		await campaigns.AddPostAsync(slug, post.ToDraft(), Agent(http), ct) switch
		{
			Outcome<Proposal>.Done(var p) => Serialize(CampaignView.From(p.Campaign, scheduling.Current, p)),
			Outcome<Proposal>.Refused(var reasons) => Refused(reasons),
			_ => "Nothing happened."
		};

	[McpServerTool(Name = "redraft_variant"), Description("Has Claude write one variant of an open proposal again, with a note on what to change.")]
	public static async Task<string> RedraftVariant(
		CampaignService campaigns, SchedulingService scheduling, IHttpContextAccessor http, string slug, string post, string network, string? note = null, CancellationToken ct = default)
	{
		if (!Networks.TryParse(network, out var n))
		{
			return Refused([$"\"{network}\" is not a network."]);
		}
		return await campaigns.RedraftAsync(slug, post, n, note, Agent(http), ct) switch
		{
			Outcome<Proposal>.Done(var p) => Serialize(CampaignView.From(p.Campaign, scheduling.Current, p)),
			Outcome<Proposal>.Refused(var reasons) => Refused(reasons),
			_ => "Nothing happened."
		};
	}

	[McpServerTool(Name = "add_image"), Description("Adds an image (PNG, JPEG, WebP or GIF, at most 1 MB, base64) to one post of an open proposal. It fills the post's first wanted image slot. Without alt text, Claude writes it.")]
	public static async Task<string> AddImage(
		CampaignService campaigns,
		SchedulingService scheduling,
		IHttpContextAccessor http,
		string slug,
		string post,
		[Description("The file name, for its extension.")] string fileName,
		[Description("The image, base64.")] string base64,
		string? alt = null,
		[Description("Which networks get it; all that take images when left out.")] string[]? networks = null,
		CancellationToken ct = default)
	{
		byte[] bytes;
		try
		{
			bytes = Convert.FromBase64String(base64);
		}
		catch (FormatException)
		{
			return Refused(["The image is not valid base64."]);
		}
		var on = (networks ?? []).Select(x => Networks.TryParse(x, out var n) ? n : (Network?)null).OfType<Network>().ToList();
		return await campaigns.AddMediaAsync(slug, fileName, bytes, post, on, alt, Agent(http), ct) switch
		{
			Outcome<Proposal>.Done(var p) => Serialize(CampaignView.From(p.Campaign, scheduling.Current, p)),
			Outcome<Proposal>.Refused(var reasons) => Refused(reasons),
			_ => "Nothing happened."
		};
	}

	private static Actor Agent(IHttpContextAccessor http) => Actor.Agent(http.HttpContext?.User.Identity?.Name ?? "unknown");

	private static string Serialize<T>(T value) =>
		JsonSerializer.Serialize(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>?)ApiJson.Default.GetTypeInfo(typeof(T))
			?? throw new InvalidOperationException($"{typeof(T)} is not in ApiJson."));

	/// <summary>A post already planned, as the calendar tool lists it.</summary>
	internal sealed record PlannedPost(string Campaign, string Post, DateTimeOffset At, IReadOnlyList<string> Networks);

	private static string Refused(IEnumerable<string> reasons) => "Not done:\n- " + string.Join("\n- ", reasons);
}
