using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Braggarr.Adapters;
using Braggarr.Adapters.Config;
using Braggarr.Adapters.Social;
using Braggarr.Domain;
using Braggarr.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Nullean.Entities;

namespace Braggarr.Web.Tests;

public class LinkedInThreadsTests
{
	/// <summary>Timers fire at once and each wait is recorded: no test here sleeps.</summary>
	private readonly InstantTime _clock = new(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));

	private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
	private static readonly LinkedInCredentials LinkedIn = new("li-token", "urn:li:person:abc", "Martijn", Now.AddDays(60));
	private static readonly ThreadsCredentials Threads = new("th-token", "1789", "mpdreamz", Now.AddDays(60), Now);

	private static PublishRequest Request(Network network, string text, params MediaFile[] media) =>
		new(new DeliveryKey("curb-1-0", "01-launch", network), Now, new Variant(network, text)) { Media = media };

	[Test]
	public async Task LinkedIn_posts_as_the_person_with_the_versioned_headers_and_escaped_text()
	{
		var api = new StubHttp((_, _) =>
		{
			var created = new HttpResponseMessage(HttpStatusCode.Created);
			created.Headers.Add("x-restli-id", "urn:li:share:7000");
			return created;
		});

		var receipt = await new LinkedInPublisher(new HttpClient(api), LinkedIn, _clock).PublishAsync(Request(Network.LinkedIn, "Curb (1.0) is out, by @me. #DotNet"), CancellationToken.None);

		receipt.Should().Be(new Receipt("urn:li:share:7000", "https://www.linkedin.com/feed/update/urn:li:share:7000/"));
		var (request, body) = api.Requests.Single();
		request.RequestUri!.ToString().Should().Be("https://api.linkedin.com/rest/posts");
		request.Headers.GetValues("LinkedIn-Version").Single().Should().Be("202610");
		request.Headers.GetValues("X-Restli-Protocol-Version").Single().Should().Be("2.0.0");
		var json = JsonDocument.Parse(body).RootElement;
		json.GetProperty("author").GetString().Should().Be("urn:li:person:abc");
		json.GetProperty("commentary").GetString().Should().Be(@"Curb \(1.0\) is out, by \@me. #DotNet");
		json.GetProperty("visibility").GetString().Should().Be("PUBLIC");
	}

	[Test]
	public async Task LinkedIn_uploads_each_image_and_uses_multi_image_for_more_than_one()
	{
		var uploads = 0;
		var api = new StubHttp((request, _) => request.RequestUri!.ToString() switch
		{
			"https://api.linkedin.com/rest/images?action=initializeUpload" =>
				StubHttp.Json(JsonSerializer.Serialize(new { value = new { uploadUrl = $"https://upload.example/{++uploads}", image = $"urn:li:image:{uploads}" } })),
			var u when u.StartsWith("https://upload.example/", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.Created),
			_ => Created()
		});
		MediaFile one = new("media/a.png", "image/png", [1], "First."), two = new("media/b.jpg", "image/jpeg", [2], "Second.");

		_ = await new LinkedInPublisher(new HttpClient(api), LinkedIn, _clock).PublishAsync(Request(Network.LinkedIn, "Two shots.", one, two), CancellationToken.None);

		api.Requests.Count(r => r.Request.Method == HttpMethod.Put).Should().Be(2);
		var images = JsonDocument.Parse(api.Requests.Last().Body).RootElement.GetProperty("content").GetProperty("multiImage").GetProperty("images");
		images.EnumerateArray().Select(i => (i.GetProperty("id").GetString(), i.GetProperty("altText").GetString()))
			.Should().Equal(("urn:li:image:1", "First."), ("urn:li:image:2", "Second."));
	}

	[Test]
	public async Task A_linkedin_refusal_can_be_retried_but_a_server_error_leaves_it_to_the_owner()
	{
		var refused = new LinkedInPublisher(new HttpClient(new StubHttp((_, _) => StubHttp.Json("{}", HttpStatusCode.UnprocessableEntity))), LinkedIn, _clock);
		var broken = new LinkedInPublisher(new HttpClient(new StubHttp((_, _) => StubHttp.Json("{}", HttpStatusCode.InternalServerError))), LinkedIn, _clock);

		_ = await ((Func<Task>)(() => refused.PublishAsync(Request(Network.LinkedIn, "x"), CancellationToken.None))).Should().ThrowAsync<PublishException>();
		var unknown = await ((Func<Task>)(() => broken.PublishAsync(Request(Network.LinkedIn, "x"), CancellationToken.None))).Should().ThrowAsync<HttpRequestException>();
		unknown.Which.Should().NotBeOfType<PublishException>();
	}

	[Test]
	public async Task LinkedIn_refuses_webp_before_uploading_anything()
	{
		var api = new StubHttp((_, _) => Created());

		var act = () => new LinkedInPublisher(new HttpClient(api), LinkedIn, _clock)
			.PublishAsync(Request(Network.LinkedIn, "x", new MediaFile("media/a.webp", "image/webp", [1], "A.")), CancellationToken.None);

		_ = await act.Should().ThrowAsync<PublishException>().WithMessage("*PNG, JPEG and GIF*");
		api.Requests.Should().BeEmpty();
	}

	[Test]
	public async Task Threads_creates_a_container_waits_for_it_publishes_it_and_links_to_it()
	{
		var checks = 0;
		var api = new StubHttp((request, _) => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
		{
			("POST", "/v1.0/1789/threads") => StubHttp.Json("""{"id":"c1"}"""),
			("GET", "/v1.0/c1") => StubHttp.Json(++checks < 2 ? """{"status":"IN_PROGRESS"}""" : """{"status":"FINISHED"}"""),
			("POST", "/v1.0/1789/threads_publish") => StubHttp.Json("""{"id":"m9"}"""),
			("GET", "/v1.0/m9") => StubHttp.Json("""{"id":"m9","permalink":"https://www.threads.com/@mpdreamz/post/abc"}"""),
			_ => new HttpResponseMessage(HttpStatusCode.NotFound)
		});
		var publisher = new ThreadsPublisher(new HttpClient(api), Threads, _clock);

		var receipt = await publisher.PublishAsync(Request(Network.Threads, "Curb 1.0 is out."), CancellationToken.None);

		receipt.Should().Be(new Receipt("m9", "https://www.threads.com/@mpdreamz/post/abc"));
		api.Requests[0].Body.Should().Contain("media_type=TEXT").And.Contain("text=Curb+1.0+is+out.");
		api.Requests.Single(r => r.Request.RequestUri!.AbsolutePath.EndsWith("threads_publish", StringComparison.Ordinal)).Body.Should().Contain("creation_id=c1");
		checks.Should().Be(2);
	}

	[Test]
	public async Task Threads_is_certain_until_it_is_asked_to_publish()
	{
		Func<Task> refusedContainer = () => new ThreadsPublisher(new HttpClient(new StubHttp((_, _) => StubHttp.Json("{}", HttpStatusCode.InternalServerError))), Threads, _clock)
			.PublishAsync(Request(Network.Threads, "x"), CancellationToken.None);
		var publishBroke = new StubHttp((request, _) => request.RequestUri!.AbsolutePath switch
		{
			"/v1.0/1789/threads" => StubHttp.Json("""{"id":"c1"}"""),
			"/v1.0/c1" => StubHttp.Json("""{"status":"FINISHED"}"""),
			_ => StubHttp.Json("{}", HttpStatusCode.BadGateway)
		});
		Func<Task> lostAnswer = () => new ThreadsPublisher(new HttpClient(publishBroke), Threads, _clock)
			.PublishAsync(Request(Network.Threads, "x"), CancellationToken.None);

		_ = await refusedContainer.Should().ThrowAsync<PublishException>();
		(await lostAnswer.Should().ThrowAsync<HttpRequestException>()).Which.Should().NotBeOfType<PublishException>();
	}

	[Test]
	public async Task The_threads_token_is_refreshed_once_a_day_old_with_under_30_days_left()
	{
		var (keeper, accounts, time, threadsApi, _) = Keeper();
		accounts.Set(Network.Threads, Threads.Username, Threads with { IssuedAt = Now.AddDays(-20), ExpiresAt = Now.AddDays(40) });

		await keeper.CheckAsync(CancellationToken.None);
		threadsApi.Requests.Should().BeEmpty("it is old enough to refresh, but 40 days are left");

		time.Advance(TimeSpan.FromDays(11));
		await keeper.CheckAsync(CancellationToken.None);

		threadsApi.Requests.Single().Request.RequestUri!.ToString().Should().Contain("refresh_access_token?grant_type=th_refresh_token&access_token=th-token");
		var refreshed = accounts.Get<ThreadsCredentials>(Network.Threads, Threads.Username)!;
		refreshed.AccessToken.Should().Be("th-fresh");
		refreshed.ExpiresAt.Should().Be(time.GetUtcNow().AddDays(60));
	}

	[Test]
	public async Task A_threads_token_younger_than_a_day_is_left_alone_even_close_to_expiry()
	{
		var (keeper, accounts, _, threadsApi, _) = Keeper();
		accounts.Set(Network.Threads, Threads.Username, Threads with { IssuedAt = Now.AddHours(-2), ExpiresAt = Now.AddDays(10) });

		await keeper.CheckAsync(CancellationToken.None);

		threadsApi.Requests.Should().BeEmpty();
	}

	[Test]
	public async Task The_owner_hears_a_week_ahead_that_the_linkedin_token_runs_out_once_a_day()
	{
		var (keeper, accounts, time, _, notifier) = Keeper();
		accounts.Set(Network.LinkedIn, LinkedIn.Name, LinkedIn with { ExpiresAt = Now.AddDays(10) });

		await keeper.CheckAsync(CancellationToken.None);
		notifier.Sent.Should().BeEmpty();

		time.Advance(TimeSpan.FromDays(4));
		await keeper.CheckAsync(CancellationToken.None);
		await keeper.CheckAsync(CancellationToken.None);
		notifier.Sent.Should().ContainSingle().Which.Title.Should().Be($"The LinkedIn token for {LinkedIn.Name} runs out in 6 days");

		time.Advance(TimeSpan.FromDays(7));
		await keeper.CheckAsync(CancellationToken.None);
		notifier.Sent.Last().Title.Should().Be($"The LinkedIn token for {LinkedIn.Name} ran out");
	}

	private static HttpResponseMessage Created()
	{
		var created = new HttpResponseMessage(HttpStatusCode.Created);
		created.Headers.Add("x-restli-id", "urn:li:share:1");
		return created;
	}

	private static (TokenKeeper Keeper, AccountStore Accounts, ManualTimeProvider Time, StubHttp Threads, Notices Notifier) Keeper()
	{
		var time = new ManualTimeProvider(Now);
		var secrets = new SecretStore(new EphemeralDataProtectionProvider(), new System.IO.Abstractions.TestingHelpers.MockFileSystem(), "/config/secrets.json");
		var threads = new StubHttp((_, _) => StubHttp.Json("""{"access_token":"th-fresh","token_type":"bearer","expires_in":5184000}"""));
		var notifier = new Notices();
		var accounts = new AccountStore(secrets);
		var keeper = new TokenKeeper(accounts, new Clients(threads), notifier, time, NullLogger<TokenKeeper>.Instance);
		return (keeper, accounts, time, threads, notifier);
	}

	private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
	{
		public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
	}

	private sealed class Notices : INotifier
	{
		public List<Notice> Sent { get; } = [];

		public Task NotifyAsync(Notice notice, CancellationToken ct)
		{
			Sent.Add(notice);
			return Task.CompletedTask;
		}
	}
}
