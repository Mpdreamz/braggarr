using System.Text.Json;
using System.Text.Json.Nodes;
using Braggarr.Domain;
using Braggarr.Services;
using Microsoft.Extensions.Options;

namespace Braggarr.Adapters.Claude;

/// <summary>Drafts variants with Claude: the cached system prompt, the campaign as data, and the answer as structured JSON.</summary>
public sealed class ClaudeDrafter(HttpClient http, ClaudeOptions options, TimeProvider? time = null) : IDrafter, IDisposable
{
	private readonly ClaudeClient _client = new(http, options.ApiKey, options.BaseUrl, time ?? TimeProvider.System);

	private Services.Usage Cost(ClaudeAnswer answer) =>
		new(options.Model, answer.InputTokens, answer.OutputTokens, answer.CacheReadTokens, answer.CacheWriteTokens);

	/// <summary>One request; an error status is a <see cref="DraftException"/>, as it was with the SDK.</summary>
	private async Task<ClaudeAnswer> AskAsync(JsonObject request, CancellationToken ct)
	{
		try
		{
			return await _client.CreateAsync(request, ct);
		}
		catch (ClaudeApiException e)
		{
			throw new DraftException($"Claude answered with an error: {e.Message}", e);
		}
	}

	/// <summary>A variant as written: its text, and for the blog the article's title, description and tags.</summary>
	private static Variant Read(Network network, JsonElement written)
	{
		var text = written.GetProperty("text").GetString() ?? "";
		if (network != Network.Blog)
		{
			return new Variant(network, text);
		}
		string Field(string name) => written.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
		var tags = written.TryGetProperty("tags", out var list) && list.ValueKind == JsonValueKind.Array
			? list.EnumerateArray().Select(t => t.GetString()?.Trim().ToLowerInvariant() ?? "").Where(t => t.Length > 0).Distinct().ToValueList()
			: [];
		return new Variant(network, text, Blogging.ImagesIn(text)) { Article = new Article(Field("title"), Field("description"), tags) };
	}

	public async Task<DraftResult> DraftAsync(DraftRequest request, CancellationToken ct)
	{
		var response = await AskAsync(new JsonObject
		{
			["model"] = options.Model,
			["max_tokens"] = 16000,
			["system"] = ClaudeClient.System(DraftPrompt.SystemPrompt(request.Voice, request.About)),
			["messages"] = ClaudeClient.User(DraftPrompt.User(request)),
			["output_config"] = ClaudeClient.Output(ClaudeClient.Effort(options.Effort), DraftPrompt.Schema)
		}, ct);

		var usage = Cost(response);
		if (response.StopReason == "refusal")
		{
			throw new DraftException($"Claude declined to write this ({response.StopExplanation ?? "no reason given"}).");
		}
		if (response.StopReason == "max_tokens")
		{
			throw new DraftException("Claude's answer was cut off.");
		}

		return new DraftResult(Parse(response.Text), usage);
	}

	public async Task<(string Alt, Services.Usage Usage)> DescribeAsync(MediaFile image, string context, CancellationToken ct)
	{
		var response = await AskAsync(new JsonObject
		{
			["model"] = options.Model,
			["max_tokens"] = 4000,
			["output_config"] = ClaudeClient.Output("low"),
			["messages"] = new JsonArray(new JsonObject
			{
				["role"] = "user",
				["content"] = new JsonArray(
					new JsonObject
					{
						["type"] = "image",
						["source"] = new JsonObject { ["type"] = "base64", ["data"] = Convert.ToBase64String(image.Bytes), ["media_type"] = image.ContentType }
					},
					new JsonObject { ["type"] = "text", ["text"] = DraftPrompt.Describe(context) })
			})
		}, ct);
		if (response.StopReason is "refusal" or "max_tokens")
		{
			throw new DraftException("Claude did not describe the image.");
		}
		return (response.Text.Trim(), Cost(response));
	}

	public async Task<(CampaignPlan Plan, Services.Usage Usage)> PlanAsync(RepositoryInfo repository, CancellationToken ct)
	{
		var (json, usage) = await StructuredAsync(DraftPrompt.Plan(repository), DraftPrompt.PlanSchema, "plan the campaign", ct);
		try
		{
			var points = DraftPrompt.ReadPoints(json.GetProperty("points"));
			return (new CampaignPlan(json.GetProperty("title").GetString() ?? repository.Name, json.GetProperty("brief").GetString() ?? "", points), usage);
		}
		catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
		{
			throw new DraftException("Claude's plan was not the JSON that was asked for.", e);
		}
	}

	public async Task<(IReadOnlyList<TalkingPoint> Points, Services.Usage Usage)> PointsAsync(PointsRequest request, CancellationToken ct)
	{
		var (json, usage) = await StructuredAsync(DraftPrompt.Points(request), DraftPrompt.PointsSchema, "find the talking points", ct);
		try
		{
			return (DraftPrompt.ReadPoints(json.GetProperty("points")), usage);
		}
		catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
		{
			throw new DraftException("Claude's talking points were not the JSON that was asked for.", e);
		}
	}

	public async Task<(ProjectBrief Brief, Services.Usage Usage)> BriefAsync(RepositoryInfo repository, CancellationToken ct)
	{
		var (json, usage) = await StructuredAsync(DraftPrompt.Brief(repository), DraftPrompt.BriefSchema, "write the brief", ct);
		try
		{
			// A link the material does not contain is one the model made up: it is left out rather than sent to people.
			var material = $"https://github.com/{repository.Repository} {repository.Homepage} {repository.Description} {repository.Readme}";
			return (
				new ProjectBrief(repository.Name, json.GetProperty("tldr").GetString()?.Trim() ?? "", json.GetProperty("apart").GetString()?.Trim() ?? "",
					json.GetProperty("audience").GetString()?.Trim() ?? "")
				{
					Avoid = [.. json.GetProperty("avoid").EnumerateArray().Select(a => a.GetString()?.Trim() ?? "").Where(a => a.Length > 0)],
					Links =
					[
						.. json.GetProperty("links").EnumerateArray()
							.Select(l => new BriefLink(l.GetProperty("label").GetString()?.Trim() ?? "", l.GetProperty("url").GetString()?.Trim() ?? ""))
							.Where(l => l.IsWeb && material.Contains(l.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
					]
				},
				usage
			);
		}
		catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
		{
			throw new DraftException("Claude's brief was not the JSON that was asked for.", e);
		}
	}

	public async Task<(ProfileSuggestion Suggestion, Services.Usage Usage)> ProfileAsync(ProfileRequest request, CancellationToken ct)
	{
		var (json, usage) = await StructuredAsync(DraftPrompt.Profile(request), DraftPrompt.ProfileSchema, "write the profile", ct);
		try
		{
			// A link the briefs do not have is one the model made up, and is left out.
			var link = json.GetProperty("link").ValueKind == JsonValueKind.String ? json.GetProperty("link").GetString()?.Trim() : null;
			var known = request.About.SelectMany(a => a.Links).Select(l => l.Url.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
			return (
				new ProfileSuggestion(
					json.GetProperty("display_name").GetString()?.Trim() ?? "",
					json.GetProperty("bio").GetString()?.Trim() ?? "",
					link is not null && known.Contains(link.TrimEnd('/')) ? link : null,
					json.GetProperty("why").GetString()?.Trim() ?? ""
				),
				usage
			);
		}
		catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
		{
			throw new DraftException("Claude's profile was not the JSON that was asked for.", e);
		}
	}

	public async Task<(string Intro, Services.Usage Usage)> IntroAsync(IntroRequest request, CancellationToken ct)
	{
		var (json, usage) = await StructuredAsync(DraftPrompt.Intro(request), DraftPrompt.IntroSchema, "write the intro", ct);
		try
		{
			var intro = json.GetProperty("intro").GetString()?.Trim() ?? "";
			return intro.Length > 0 ? (intro, usage) : throw new DraftException("Claude's intro was empty.");
		}
		catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
		{
			throw new DraftException("Claude's intro was not the JSON that was asked for.", e);
		}
	}

	/// <summary>One question with a JSON answer of the given shape.</summary>
	private async Task<(JsonElement Json, Services.Usage Usage)> StructuredAsync(string prompt, JsonElement schema, string what, CancellationToken ct)
	{
		var response = await AskAsync(new JsonObject
		{
			["model"] = options.Model,
			["max_tokens"] = 16000,
			["messages"] = ClaudeClient.User(prompt),
			["output_config"] = ClaudeClient.Output(ClaudeClient.Effort(options.Effort), schema)
		}, ct);
		if (response.StopReason is "refusal" or "max_tokens")
		{
			throw new DraftException($"Claude did not {what}.");
		}
		var usage = Cost(response);
		try
		{
			return (JsonDocument.Parse(response.Text).RootElement.Clone(), usage);
		}
		catch (JsonException e)
		{
			throw new DraftException($"Claude's answer when asked to {what} was not JSON.", e);
		}
	}

	public async Task<(ReleaseVerdict Verdict, Services.Usage Usage)> JudgeAsync(string project, Release release, CancellationToken ct)
	{
		var response = await AskAsync(new JsonObject
		{
			["model"] = options.Model,
			["max_tokens"] = 4000,
			["messages"] = ClaudeClient.User(DraftPrompt.Judge(project, release)),
			["output_config"] = ClaudeClient.Output("low", DraftPrompt.VerdictSchema)
		}, ct);
		if (response.StopReason is "refusal" or "max_tokens")
		{
			throw new DraftException("Claude did not judge the release.");
		}
		var usage = Cost(response);
		try
		{
			var json = JsonDocument.Parse(response.Text).RootElement;
			var angle = json.GetProperty("angle").GetString();
			return (new ReleaseVerdict(json.GetProperty("worthy").GetBoolean(), json.GetProperty("reason").GetString() ?? "", string.IsNullOrWhiteSpace(angle) ? null : angle), usage);
		}
		catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
		{
			throw new DraftException("Claude's verdict was not the JSON that was asked for.", e);
		}
	}

	public void Dispose()
	{
		// Nothing of its own to release: the HttpClient belongs to the factory.
	}

	internal static IReadOnlyList<DraftedVariant> Parse(string json)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			return
			[
				.. from v in document.RootElement.GetProperty("variants").EnumerateArray()
				let network = v.GetProperty("network").GetString() ?? ""
				where Networks.TryParse(network, out _)
				select new DraftedVariant(
					v.GetProperty("post").GetString() ?? "",
					Read(Networks.TryParse(network, out var n) ? n : default, v)
				)
			];
		}
		catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
		{
			throw new DraftException("Claude's answer was not the JSON that was asked for.", e);
		}
	}
}

/// <summary>The drafter for the current settings, rebuilt when the key or the model changes; null without a key.</summary>
public sealed class ClaudeDrafters : IDrafters
{
	private readonly IHttpClientFactory _http;
	private ClaudeOptions _built;
	private ClaudeDrafter? _current;

	private readonly TimeProvider _time;

	public ClaudeDrafters(IHttpClientFactory http, IOptionsMonitor<ClaudeOptions> options, TimeProvider time)
	{
		_http = http;
		_time = time;
		_built = options.CurrentValue;
		_current = Build(_built);
		_ = options.OnChange(o =>
		{
			_built = o;
			_current = Build(o);
		});
	}

	public IDrafter? Current => _current;

	public string Model => _built.Model;

	private ClaudeDrafter? Build(ClaudeOptions o) => o.IsConfigured ? new ClaudeDrafter(_http.CreateClient("anthropic"), o, _time) : null;
}
