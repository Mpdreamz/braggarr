using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Braggarr.Adapters.Config;
using Braggarr.Adapters.Social;
using Braggarr.Domain;
using Braggarr.Services;
using Braggarr.Services.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace Braggarr.Web.Tests;

/// <summary>YouTube against a stub that answers as Google's docs say (October 2026): OAuth refresh, then a resumable upload.</summary>
public class YouTubeTests
{
	private const string Session = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&upload_id=abc";

	private static readonly YouTubeCredentials Credentials = new("client-id", "client-secret", "refresh-token", "Nullean");

	private readonly InstantTime _clock = new(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));

	private static MediaFile Video() =>
		new($"video:sha256-{new string('a', 64)}.mp4", "video/mp4", Mp4Fixture.Make(1080, 1920, 20, padding: 3000), "A run");

	private static PublishRequest Request(string text = "Curb formats a file where one line changes\n\nCurb keeps C# diffs small.\nhttps://github.com/nullean/curb") =>
		new(new DeliveryKey("curb-1-0", "01-run", Network.YouTube), DateTimeOffset.UnixEpoch, new Variant(Network.YouTube, text)) { Media = [Video()] };

	/// <summary>Google: a token per refresh, a session per start, and whatever <paramref name="upload"/> says to each PUT.</summary>
	private static StubHttp Google(Func<HttpRequestMessage, int, HttpResponseMessage> upload, List<string>? tokens = null) => new((request, body) =>
	{
		if (request.RequestUri!.AbsoluteUri == GoogleOAuth.TokenUrl)
		{
			tokens?.Add(body);
			return StubHttp.Json($$"""{"access_token":"access-{{tokens?.Count ?? 1}}","expires_in":3599,"token_type":"Bearer"}""");
		}
		if (request.Method == HttpMethod.Post && request.RequestUri.AbsoluteUri.StartsWith("https://www.googleapis.com/upload/youtube/v3/videos", StringComparison.Ordinal))
		{
			var ok = new HttpResponseMessage(HttpStatusCode.OK);
			ok.Headers.Location = new Uri(Session);
			return ok;
		}
		return request.Method == HttpMethod.Put ? upload(request, (int)(request.Content?.Headers.ContentLength ?? 0)) : new HttpResponseMessage(HttpStatusCode.NotFound);
	});

	private static HttpResponseMessage Created() => StubHttp.Json("""{"kind":"youtube#video","id":"dQw4w9WgXcQ"}""", HttpStatusCode.Created);

	[Test]
	public async Task A_post_is_one_public_upload_titled_by_its_first_line()
	{
		var tokens = new List<string>();
		var google = Google((_, _) => Created(), tokens);

		var receipt = await new YouTubePublisher(new HttpClient(google), Credentials, _clock).PublishAsync(Request(), CancellationToken.None);

		receipt.Url.Should().Be("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
		tokens.Single().Should().Contain("grant_type=refresh_token").And.Contain("refresh_token=refresh-token").And.Contain("client_secret=client-secret");
		var start = google.Requests.Single(r => r.Request.Method == HttpMethod.Post && r.Request.RequestUri!.Host == "www.googleapis.com");
		start.Request.RequestUri!.Query.Should().Contain("uploadType=resumable").And.Contain("part=snippet,status");
		start.Request.Headers.GetValues("X-Upload-Content-Type").Single().Should().Be("video/mp4");
		start.Request.Headers.GetValues("X-Upload-Content-Length").Single().Should().Be(Video().Bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
		start.Request.Headers.Authorization!.Parameter.Should().Be("access-1");
		var resource = JsonDocument.Parse(start.Body).RootElement;
		resource.GetProperty("snippet").GetProperty("title").GetString().Should().Be("Curb formats a file where one line changes");
		resource.GetProperty("snippet").GetProperty("description").GetString().Should().Be("Curb keeps C# diffs small.\nhttps://github.com/nullean/curb");
		resource.GetProperty("snippet").GetProperty("categoryId").GetString().Should().Be(YouTubePublisher.Category);
		resource.GetProperty("status").GetProperty("privacyStatus").GetString().Should().Be("public");
		google.Requests.Single(r => r.Request.Method == HttpMethod.Put).Request.RequestUri!.AbsoluteUri.Should().Be(Session);
	}

	[Test]
	public async Task The_access_token_is_kept_for_its_hour_by_the_apps_clock_and_then_refreshed()
	{
		var tokens = new List<string>();
		var clock = new Nullean.Entities.ManualTimeProvider(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
		var publisher = new YouTubePublisher(new HttpClient(Google((_, _) => Created(), tokens)), Credentials, clock);

		_ = await publisher.PublishAsync(Request(), CancellationToken.None);
		clock.Advance(TimeSpan.FromMinutes(50));
		_ = await publisher.PublishAsync(Request(), CancellationToken.None);
		clock.Advance(TimeSpan.FromMinutes(9));
		_ = await publisher.PublishAsync(Request(), CancellationToken.None);

		tokens.Should().HaveCount(2, "the second post used the same token; the third came within a minute of its end");
	}

	[Test]
	public async Task A_dropped_upload_is_resumed_from_where_youtube_says_it_got()
	{
		var length = Video().Bytes.Length;
		var puts = 0;
		var google = Google((request, sent) => ++puts switch
		{
			1 => throw new HttpRequestException("connection reset"),
			2 => Incomplete(lastByte: 999),
			_ => Created()
		});

		var receipt = await new YouTubePublisher(new HttpClient(google), Credentials, _clock).PublishAsync(Request(), CancellationToken.None);

		receipt.RemoteId.Should().Be("dQw4w9WgXcQ");
		var status = google.Requests.Where(r => r.Request.Method == HttpMethod.Put).ElementAt(1).Request;
		status.Content!.Headers.ContentRange!.ToString().Should().Be($"bytes */{length}");
		var resumed = google.Requests.Where(r => r.Request.Method == HttpMethod.Put).ElementAt(2).Request;
		resumed.Content!.Headers.ContentRange!.ToString().Should().Be($"bytes 1000-{length - 1}/{length}");
		resumed.Content.Headers.ContentLength.Should().Be(length - 1000);
		_clock.Waits.Should().Equal([TimeSpan.FromSeconds(2)], "it backs off on the app's clock before asking");
	}

	[Test]
	public async Task An_answer_lost_after_the_last_byte_is_found_by_asking_and_nothing_is_uploaded_twice()
	{
		var puts = 0;
		var google = Google((_, _) => ++puts == 1 ? throw new HttpRequestException("timeout") : Created());

		var receipt = await new YouTubePublisher(new HttpClient(google), Credentials, _clock).PublishAsync(Request(), CancellationToken.None);

		receipt.RemoteId.Should().Be("dQw4w9WgXcQ");
		google.Requests.Count(r => r.Request.Method == HttpMethod.Post && r.Request.RequestUri!.Host == "www.googleapis.com").Should().Be(1);
		google.Requests.Where(r => r.Request.Method == HttpMethod.Put).Skip(1).Should().OnlyContain(r => r.Request.Content!.Headers.ContentLength == 0);
	}

	[Test]
	public async Task When_youtube_cannot_be_asked_either_the_delivery_is_unknown_not_refused()
	{
		var google = Google((_, _) => throw new HttpRequestException("no route"));

		var publishing = () => new YouTubePublisher(new HttpClient(google), Credentials, _clock).PublishAsync(Request(), CancellationToken.None);

		var thrown = (await publishing.Should().ThrowAsync<HttpRequestException>()).Which;
		thrown.Should().NotBeOfType<PublishException>();
	}

	[Test]
	public async Task Refusals_before_the_video_exists_are_safe_to_retry()
	{
		var refusedStart = new StubHttp((request, _) => request.RequestUri!.AbsoluteUri == GoogleOAuth.TokenUrl
			? StubHttp.Json("""{"access_token":"a","expires_in":3599}""")
			: StubHttp.Json("""{"error":{"code":403,"message":"quotaExceeded"}}""", HttpStatusCode.Forbidden));
		var neverCompletes = Google((_, _) => Incomplete(lastByte: 10));
		var revoked = new StubHttp((_, _) => StubHttp.Json("""{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", HttpStatusCode.BadRequest));

		var start = () => new YouTubePublisher(new HttpClient(refusedStart), Credentials, _clock).PublishAsync(Request(), CancellationToken.None);
		var stuck = () => new YouTubePublisher(new HttpClient(neverCompletes), Credentials, _clock).PublishAsync(Request(), CancellationToken.None);
		var signIn = () => new YouTubePublisher(new HttpClient(revoked), Credentials, _clock).PublishAsync(Request(), CancellationToken.None);
		var noVideo = () => new YouTubePublisher(new HttpClient(neverCompletes), Credentials, _clock).PublishAsync(Request() with { Media = [] }, CancellationToken.None);

		(await start.Should().ThrowAsync<PublishException>()).WithMessage("*quotaExceeded*");
		(await stuck.Should().ThrowAsync<PublishException>()).WithMessage("*incomplete*");
		(await signIn.Should().ThrowAsync<PublishException>()).WithMessage("*Connect YouTube again*");
		(await noVideo.Should().ThrowAsync<PublishException>()).WithMessage("*needs its video*");
	}

	[Test]
	public async Task Connecting_goes_to_googles_consent_and_keeps_the_refresh_token_it_brings_back()
	{
		using var factory = new BraggarrFactory();
		var channel = new StubHttp((request, body) => request.RequestUri!.AbsoluteUri == GoogleOAuth.TokenUrl
			? StubHttp.Json(body.Contains("authorization_code", StringComparison.Ordinal)
				? """{"access_token":"a1","refresh_token":"the-refresh-token","expires_in":3599}"""
				: """{"access_token":"a2","expires_in":3599}""")
			: StubHttp.Json("""{"items":[{"snippet":{"title":"Nullean"}}]}"""));
		factory.YouTube = channel;
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);
		factory.Services.GetRequiredService<SecretStore>().Set(Publishers.YouTubeClientSecret, new YouTubeClient("client-id", "client-secret"));

		// A forged answer first: its state is not the one this browser was given.
		_ = await browser.GetAsync("/settings/youtube/connect");
		var forged = await browser.GetAsync("/settings/youtube/callback?code=c&state=not-the-state");
		var consent = await browser.GetAsync("/settings/youtube/connect");
		var to = consent.Headers.Location!;
		var query = System.Web.HttpUtility.ParseQueryString(to.Query);
		var back = await browser.GetAsync($"/settings/youtube/callback?code=the-code&state={query["state"]}");

		to.GetLeftPart(UriPartial.Path).Should().Be(GoogleOAuth.AuthorizeUrl);
		query["access_type"].Should().Be("offline");
		query["prompt"].Should().Be("consent");
		query["redirect_uri"].Should().Be("http://localhost/settings/youtube/callback");
		query["scope"].Should().Contain("youtube.upload");
		forged.Headers.Location!.OriginalString.Should().EndWith("youtube=state");
		back.Headers.Location!.OriginalString.Should().EndWith("youtube=connected");
		channel.Requests.First(r => r.Body.Contains("authorization_code", StringComparison.Ordinal)).Body.Should().Contain("code=the-code");
		factory.Services.GetRequiredService<AccountStore>().Get<YouTubeCredentials>(Network.YouTube, "Nullean")
			.Should().Be(new YouTubeCredentials("client-id", "client-secret", "the-refresh-token", "Nullean"));
		(await browser.GetStringAsync("/Settings?section=networks&youtube=connected")).Should().Contain("Connected the YouTube channel.").And.Contain("Nullean");
		var agent = await factory.Agent().GetAsync("/settings/youtube/connect");
		agent.Headers.Location?.Host.Should().NotBe("accounts.google.com", "an agent's key is sent to sign in, never to Google's consent");
	}

	private static HttpResponseMessage Incomplete(long lastByte)
	{
		var incomplete = new HttpResponseMessage((HttpStatusCode)308);
		incomplete.Headers.Add("Range", $"bytes=0-{lastByte}");
		return incomplete;
	}
}
