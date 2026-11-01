using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Braggarr.Adapters.Claude;

namespace Braggarr.Web.Tests;

/// <summary>The Messages API client that replaced the Anthropic SDK, held to what the SDK did: its errors, retries, waits and timeouts.</summary>
public class ClaudeClientTests
{
	private const string Ok = """{"id":"msg_1","type":"message","role":"assistant","model":"m","content":[{"type":"thinking","thinking":"…"},{"type":"text","text":"Hello"},{"type":"text","text":" there"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":12,"output_tokens":3,"cache_read_input_tokens":10}}""";

	private const string Error = """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""";

	private static readonly JsonObject Request = new() { ["model"] = "m", ["max_tokens"] = 16000, ["messages"] = ClaudeClient.User("hi") };

	/// <summary>Waits happen at once and are recorded. An attempt's own timeout (a cancellation source's timer) fires only when told to.</summary>
	private sealed class RetryTime(bool attemptsTimeOut = false) : TimeProvider
	{
		public List<TimeSpan> Waits { get; } = [];

		public List<TimeSpan> Timeouts { get; } = [];

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			if (state is CancellationTokenSource)
			{
				lock (Timeouts)
				{
					Timeouts.Add(dueTime);
				}
				return System.CreateTimer(callback, state, attemptsTimeOut ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, period);
			}
			if (dueTime > TimeSpan.Zero)
			{
				lock (Waits)
				{
					Waits.Add(dueTime);
				}
			}
			return System.CreateTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.Zero, period);
		}
	}

	private static HttpResponseMessage Status(int status, string body = Error, string? retryAfter = null)
	{
		var response = StubHttp.Json(body, (HttpStatusCode)status);
		if (retryAfter is not null)
		{
			response.Headers.TryAddWithoutValidation("retry-after", retryAfter);
		}
		return response;
	}

	[Test]
	public async Task An_answer_is_its_text_blocks_its_stop_reason_and_its_usage()
	{
		var api = new StubHttp((_, _) => StubHttp.Json(Ok));

		var answer = await new ClaudeClient(new HttpClient(api), "sk-ant-test", "https://claude.test", new RetryTime()).CreateAsync(Request, CancellationToken.None);

		answer.Should().Be(new ClaudeAnswer("Hello there", "end_turn", null, 12, 3, 10, 0));
		var (request, body) = api.Requests.Single();
		request.RequestUri!.ToString().Should().Be("https://claude.test/v1/messages");
		request.Headers.Authorization!.ToString().Should().Be("Bearer sk-ant-test");
		request.Headers.GetValues("anthropic-version").Should().Equal(ClaudeClient.ApiVersion);
		request.Content!.Headers.ContentType!.ToString().Should().Be("application/json");
		body.Should().Be("""{"model":"m","max_tokens":16000,"messages":[{"role":"user","content":"hi"}]}""");
	}

	[Test]
	public async Task A_refusal_carries_its_explanation()
	{
		var api = new StubHttp((_, _) => StubHttp.Json("""{"content":[],"stop_reason":"refusal","stop_details":{"explanation":"No."},"usage":{"input_tokens":1,"output_tokens":0}}"""));

		var answer = await new ClaudeClient(new HttpClient(api), "k", null, new RetryTime()).CreateAsync(Request, CancellationToken.None);

		answer.StopReason.Should().Be("refusal");
		answer.StopExplanation.Should().Be("No.");
	}

	[Test]
	[Arguments(400)]
	[Arguments(401)]
	[Arguments(403)]
	[Arguments(404)]
	[Arguments(422)]
	public async Task A_refusal_by_the_api_is_not_retried(int status)
	{
		var api = new StubHttp((_, _) => Status(status));

		var act = () => new ClaudeClient(new HttpClient(api), "k", null, new RetryTime()).CreateAsync(Request, CancellationToken.None);

		var thrown = await act.Should().ThrowAsync<ClaudeApiException>();
		thrown.Which.Message.Should().Be($"Status Code: {(HttpStatusCode)status}\n{Error}");
		api.Requests.Should().ContainSingle();
	}

	[Test]
	[Arguments(408)]
	[Arguments(409)]
	[Arguments(429)]
	[Arguments(500)]
	[Arguments(503)]
	[Arguments(529)]
	public async Task A_busy_or_broken_api_is_tried_three_times_with_growing_waits(int status)
	{
		var api = new StubHttp((_, _) => Status(status));
		var time = new RetryTime();

		var act = () => new ClaudeClient(new HttpClient(api), "k", null, time).CreateAsync(Request, CancellationToken.None);

		var thrown = await act.Should().ThrowAsync<ClaudeApiException>();
		thrown.Which.Message.Should().StartWith($"Status Code: {(status == 529 ? "529" : ((HttpStatusCode)status).ToString())}\n");
		api.Requests.Should().HaveCount(3);
		time.Waits.Should().HaveCount(2);
		time.Waits[0].Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(0.375)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(0.5));
		time.Waits[1].Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(0.75)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(1));
	}

	[Test]
	public async Task A_retry_waits_as_long_as_the_answer_asks_when_that_is_under_a_minute()
	{
		var calls = 0;
		var api = new StubHttp((_, _) => ++calls switch { 1 => Status(429, retryAfter: "7"), 2 => Status(429, retryAfter: "120"), _ => StubHttp.Json(Ok) });
		var time = new RetryTime();

		var answer = await new ClaudeClient(new HttpClient(api), "k", null, time).CreateAsync(Request, CancellationToken.None);

		answer.Text.Should().Be("Hello there");
		time.Waits[0].Should().Be(TimeSpan.FromSeconds(7));
		time.Waits[1].Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1), "two minutes is too long to honour; the backoff applies");
	}

	[Test]
	public async Task A_connection_that_never_works_is_an_io_exception_after_three_tries()
	{
		var calls = 0;
		var api = new StubHttp((_, _) => { calls++; throw new HttpRequestException("Connection refused"); });

		var act = () => new ClaudeClient(new HttpClient(api), "k", null, new RetryTime()).CreateAsync(Request, CancellationToken.None);

		var thrown = await act.Should().ThrowAsync<ClaudeIOException>();
		thrown.Which.Message.Should().Be("I/O exception");
		calls.Should().Be(3);
	}

	[Test]
	public async Task Each_attempt_gets_30_ms_per_output_token_and_three_that_run_out_end_as_a_timeout()
	{
		var slow = new SlowHandler();
		var time = new RetryTime(attemptsTimeOut: true);

		var act = () => new ClaudeClient(new HttpClient(slow), "k", null, time).CreateAsync(Request, CancellationToken.None);

		var thrown = await act.Should().ThrowAsync<TaskCanceledException>();
		thrown.Which.Message.Should().Be("The request was canceled due to the configured client timeout of 00:08:00 elapsing.");
		thrown.Which.InnerException.Should().BeOfType<TimeoutException>();
		slow.Calls.Should().Be(3);
		time.Timeouts.Should().Equal(TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(8));
		ClaudeClient.Timeout(4000).Should().Be(TimeSpan.FromMinutes(2));
		ClaudeClient.Timeout(1).Should().Be(TimeSpan.FromSeconds(30));
	}

	[Test]
	public async Task Cancelling_is_not_a_timeout_and_is_not_retried()
	{
		var slow = new SlowHandler();
		using var cancel = new CancellationTokenSource();

		var call = new ClaudeClient(new HttpClient(slow), "k", null, new RetryTime()).CreateAsync(Request, cancel.Token);
		await slow.Started.Task;
		await cancel.CancelAsync();

		_ = await call.Awaiting(c => c).Should().ThrowAsync<OperationCanceledException>();
		slow.Calls.Should().Be(1);
	}

	/// <summary>An API that never answers: what it gets is cancelled.</summary>
	private sealed class SlowHandler : HttpMessageHandler
	{
		public int Calls;

		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			_ = Interlocked.Increment(ref Calls);
			Started.TrySetResult();
			await Task.Delay(Timeout.Infinite, cancellationToken);
			return new HttpResponseMessage();
		}
	}
}
