using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Braggarr.Adapters.Social;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Web.Tests;

public class PublisherTests
{
	/// <summary>Timers fire at once and each wait is recorded: no test here sleeps.</summary>
	private readonly InstantTime _clock = new(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));

	private static readonly DateTimeOffset At = new(2026, 11, 2, 9, 30, 0, TimeSpan.FromHours(1));

	private static readonly PublishRequest Request = new(
		new DeliveryKey("curb-1-0", "01-launch", Network.Bluesky),
		At,
		new Variant(Network.Bluesky, "Curb 1.0 ✨ is out: https://github.com/nullean/curb.")
	);

	private static StubHttp Bluesky(Func<string, HttpResponseMessage>? createRecord = null) =>
		new((request, _) => request.RequestUri!.AbsolutePath switch
		{
			"/xrpc/com.atproto.server.createSession" => StubHttp.Json("""{"did":"did:plc:me","handle":"me.bsky.social","accessJwt":"jwt"}"""),
			"/xrpc/com.atproto.repo.createRecord" => createRecord?.Invoke("") ?? StubHttp.Json("""{"uri":"at://did:plc:me/app.bsky.feed.post/x","cid":"c"}"""),
			_ => new HttpResponseMessage(HttpStatusCode.NotFound)
		});

	[Test]
	public async Task Bluesky_posts_with_a_link_facet_in_utf8_bytes_under_a_stable_record_key()
	{
		var stub = Bluesky();
		var publisher = new BlueskyPublisher(new HttpClient(stub), new BlueskyCredentials("me.bsky.social", "app-password"), _clock);

		var receipt = await publisher.PublishAsync(Request, CancellationToken.None);

		var body = JsonDocument.Parse(stub.Requests.Last().Body).RootElement;
		var rkey = body.GetProperty("rkey").GetString()!;
		rkey.Should().MatchRegex("^[234567a-z]{13}$").And.Be(BlueskyPublisher.RecordKey(Request.Key, At));
		var link = body.GetProperty("record").GetProperty("facets")[0];
		// "Curb 1.0 ✨ is out: " is 21 bytes in UTF-8 (the sparkle takes 3), though only 19 characters.
		link.GetProperty("index").GetProperty("byteStart").GetInt32().Should().Be(21);
		link.GetProperty("features")[0].GetProperty("uri").GetString().Should().Be("https://github.com/nullean/curb");
		receipt.Url.Should().Be($"https://bsky.app/profile/me.bsky.social/post/{rkey}");
	}

	[Test]
	public void The_record_key_differs_per_delivery_and_never_per_attempt()
	{
		var key = Request.Key;

		BlueskyPublisher.RecordKey(key, At).Should().Be(BlueskyPublisher.RecordKey(key, At));
		BlueskyPublisher.RecordKey(key, At).Should().NotBe(BlueskyPublisher.RecordKey(key with { Post = "02-follow-up" }, At));
		BlueskyPublisher.RecordKey(key, At).Should().NotBe(BlueskyPublisher.RecordKey(key with { Campaign = "heed-0-20" }, At));
	}

	[Test]
	public async Task A_retry_of_a_post_that_went_out_counts_as_published()
	{
		var stub = Bluesky(_ => StubHttp.Json("""{"error":"InvalidRequest","message":"Record already exists"}""", HttpStatusCode.BadRequest));
		var publisher = new BlueskyPublisher(new HttpClient(stub), new BlueskyCredentials("me.bsky.social", "app-password"), _clock);

		var receipt = await publisher.PublishAsync(Request, CancellationToken.None);

		receipt.RemoteId.Should().StartWith("at://did:plc:me/app.bsky.feed.post/");
	}

	[Test]
	public async Task A_refusal_is_a_publish_exception_so_the_delivery_is_retried_not_marked_unknown()
	{
		var stub = Bluesky(_ => StubHttp.Json("""{"error":"InternalServerError"}""", HttpStatusCode.InternalServerError));
		var publisher = new BlueskyPublisher(new HttpClient(stub), new BlueskyCredentials("me.bsky.social", "app-password"), _clock);

		var act = () => publisher.PublishAsync(Request, CancellationToken.None);

		_ = await act.Should().ThrowAsync<PublishException>();
	}

	[Test]
	public async Task Mastodon_sends_the_same_idempotency_key_on_every_attempt()
	{
		var stub = new StubHttp((_, _) => StubHttp.Json("""{"id":"113","url":"https://hachyderm.io/@me/113"}"""));
		var publisher = new MastodonPublisher(new HttpClient(stub), new MastodonCredentials("https://hachyderm.io", "token"), _clock);
		var request = Request with { Key = Request.Key with { Network = Network.Mastodon } };

		_ = await publisher.PublishAsync(request, CancellationToken.None);
		var receipt = await publisher.PublishAsync(request, CancellationToken.None);

		var keys = stub.Requests.Select(r => r.Request.Headers.GetValues("Idempotency-Key").Single()).ToArray();
		keys.Should().HaveCount(2).And.OnlyContain(k => k == keys[0]);
		keys[0].Should().Contain("curb-1-0/01-launch/mastodon");
		receipt.Url.Should().Be("https://hachyderm.io/@me/113");
	}
}
