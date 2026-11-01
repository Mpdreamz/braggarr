using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Braggarr.Adapters.Claude;

/// <summary>Claude answered with an error status. The message is the status and the body, as the Anthropic SDK wrote it.</summary>
public sealed class ClaudeApiException(HttpStatusCode status, string body) : Exception($"Status Code: {status}\n{body}")
{
	public HttpStatusCode Status { get; } = status;
}

/// <summary>Claude could not be reached after the retries: the connection failed every time.</summary>
public sealed class ClaudeIOException(Exception inner) : Exception("I/O exception", inner);

/// <summary>What Claude answered: the text of its text blocks, why it stopped, and what it cost.</summary>
public sealed record ClaudeAnswer(string Text, string? StopReason, string? StopExplanation, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens);

/// <summary>
/// The Messages API, for the few requests Braggarr makes, without the Anthropic SDK, which is not AOT-safe yet
/// (anthropics/anthropic-sdk-csharp#79). It keeps what the SDK did:
/// <list type="bullet">
/// <item>Each attempt gets 30 ms per output token asked for, never less than 30 seconds.</item>
/// <item>A 408, 409, 429 or 5xx, a failed connection or a timed-out attempt is retried twice: after <c>retry-after</c> when the
/// answer says, else after half a second, then a second, less up to a quarter at random.</item>
/// <item>An error status ends in <see cref="ClaudeApiException"/>; a connection that never worked in <see cref="ClaudeIOException"/>;
/// attempts that all ran out of time in a <see cref="TaskCanceledException"/>.</item>
/// </list>
/// Requests are written as the caller builds them, so their fields stay in the caller's order.
/// </summary>
public sealed class ClaudeClient(HttpClient http, string? apiKey, string? baseUrl, TimeProvider time)
{
	/// <summary>The API version the SDK this replaces sent.</summary>
	public const string ApiVersion = "2026-10-21";

	public const int MaxRetries = 2;

	public async Task<ClaudeAnswer> CreateAsync(JsonObject request, CancellationToken ct)
	{
		var body = request.ToJsonString();
		var timeout = Timeout(request["max_tokens"]?.GetValue<int>() ?? 0);
		for (var attempt = 0; ; attempt++)
		{
			using var attemptTimeout = new CancellationTokenSource(timeout, time);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, attemptTimeout.Token);
			using var message = Request(body);
			HttpResponseMessage response;
			try
			{
				response = await http.SendAsync(message, linked.Token);
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested && attemptTimeout.IsCancellationRequested)
			{
				if (attempt < MaxRetries)
				{
					await Task.Delay(Backoff(attempt, null), time, ct);
					continue;
				}
				throw new TaskCanceledException($"The request was canceled due to the configured client timeout of {timeout} elapsing.", new TimeoutException());
			}
			catch (HttpRequestException e)
			{
				if (attempt < MaxRetries)
				{
					await Task.Delay(Backoff(attempt, null), time, ct);
					continue;
				}
				throw new ClaudeIOException(e);
			}

			using (response)
			{
				var text = await response.Content.ReadAsStringAsync(ct);
				if (response.IsSuccessStatusCode)
				{
					return Read(text);
				}
				if (attempt < MaxRetries && Retries(response.StatusCode))
				{
					await Task.Delay(Backoff(attempt, response.Headers), time, ct);
					continue;
				}
				throw new ClaudeApiException(response.StatusCode, text);
			}
		}
	}

	/// <summary>How long one attempt may take: 30 ms per output token, at least 30 seconds.</summary>
	public static TimeSpan Timeout(int maxTokens) => TimeSpan.FromMilliseconds(Math.Max(30_000, maxTokens * 30L));

	public static bool Retries(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests || (int)status >= 500;

	/// <summary>The wait before retry <paramref name="attempt"/> + 1: what the answer asks for, if it asks for under a minute, else exponential with jitter.</summary>
	public static TimeSpan Backoff(int attempt, HttpResponseHeaders? headers)
	{
		if (headers is not null)
		{
			if (headers.TryGetValues("retry-after-ms", out var ms) && double.TryParse(ms.First(), NumberStyles.Float, CultureInfo.InvariantCulture, out var millis)
				&& millis is > 0 and < 60_000)
			{
				return TimeSpan.FromMilliseconds(millis);
			}
			if (headers.TryGetValues("retry-after", out var after) && double.TryParse(after.First(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
				&& seconds is > 0 and < 60)
			{
				return TimeSpan.FromSeconds(seconds);
			}
		}
		var delay = Math.Min(0.5 * Math.Pow(2, attempt), 8);
		return TimeSpan.FromSeconds(delay * (1 - (0.25 * Random.Shared.NextDouble())));
	}

	private HttpRequestMessage Request(string body)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri((baseUrl ?? "https://api.anthropic.com").TrimEnd('/') + "/"), "v1/messages"))
		{
			Content = new StringContent(body, global::System.Text.Encoding.UTF8, "application/json")
		};
		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
		request.Headers.Add("anthropic-version", ApiVersion);
		return request;
	}

	internal static ClaudeAnswer Read(string json)
	{
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		var text = string.Concat(
			from block in root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array ? content.EnumerateArray() : Enumerable.Empty<JsonElement>()
			where block.TryGetProperty("type", out var type) && type.GetString() == "text"
			select block.GetProperty("text").GetString()
		);
		var usage = root.GetProperty("usage");
		return new ClaudeAnswer(
			text,
			root.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String ? stop.GetString() : null,
			root.TryGetProperty("stop_details", out var details) && details.ValueKind == JsonValueKind.Object
				&& details.TryGetProperty("explanation", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null,
			usage.GetProperty("input_tokens").GetInt64(),
			usage.GetProperty("output_tokens").GetInt64(),
			Tokens(usage, "cache_read_input_tokens"),
			Tokens(usage, "cache_creation_input_tokens")
		);
	}

	private static long Tokens(JsonElement usage, string name) =>
		usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

	/// <summary>The cached system prompt: one text block, marked for the prompt cache.</summary>
	public static JsonArray System(string text) =>
		new(new JsonObject { ["type"] = "text", ["text"] = text, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } });

	/// <summary>One user message of plain text.</summary>
	public static JsonArray User(string text) => new(new JsonObject { ["role"] = "user", ["content"] = text });

	/// <summary>How hard to think, and, given a schema, the JSON the answer must be.</summary>
	public static JsonObject Output(string effort, JsonElement? schema = null)
	{
		var output = new JsonObject { ["effort"] = effort };
		if (schema is { } shape)
		{
			output["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = JsonNode.Parse(shape.GetRawText()) };
		}
		return output;
	}

	/// <summary>The effort setting as the API names it: low, medium (anything else) or high.</summary>
	public static string Effort(string setting) => setting switch { "low" => "low", "high" => "high", _ => "medium" };
}
