using System.IO.Abstractions.TestingHelpers;
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Braggarr.Adapters;
using Braggarr.Adapters.Social;
using Braggarr.Domain;
using Braggarr.Services;
using Braggarr.Services.Tests;

namespace Braggarr.Web.Tests;

/// <summary>Each network's video upload, against a stub that answers as its docs say (October 2026).</summary>
public class VideoPublisherTests
{
	/// <summary>Timers fire at once and each wait is recorded: no test here sleeps.</summary>
	private readonly InstantTime _clock = new(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));

	private static readonly DateTimeOffset At = new(2026, 11, 2, 9, 30, 0, TimeSpan.FromHours(1));

	private static MediaFile Video(int padding = 0)
	{
		var bytes = Mp4Fixture.Make(1080, 1920, 20, padding: padding);
		return new MediaFile($"video:sha256-{new string('a', 64)}.mp4", "video/mp4", bytes, "A run turning a flowchart into an SVG") { Video = Mp4.Read(bytes) };
	}

	private static PublishRequest Request(Network network, MediaFile video) =>
		new(new DeliveryKey("mermaider-3-0", "01-launch", network), At, new Variant(network, "Diagrams without a browser.")) { Media = [video] };

	[Test]
	public async Task Bluesky_sends_the_video_to_the_video_service_for_the_accounts_pds_and_embeds_the_blob_with_its_shape()
	{
		var stub = new StubHttp((request, _) => (request.RequestUri!.Host, request.RequestUri.AbsolutePath) switch
		{
			(_, "/xrpc/com.atproto.server.createSession") => StubHttp.Json("""
				{"did":"did:plc:me","handle":"me.bsky.social","accessJwt":"jwt",
				 "didDoc":{"service":[{"id":"#atproto_pds","type":"AtprotoPersonalDataServer","serviceEndpoint":"https://morel.us-east.host.bsky.network"}]}}
				"""),
			(_, "/xrpc/com.atproto.server.getServiceAuth") => StubHttp.Json("""{"token":"service-token"}"""),
			("video.bsky.app", "/xrpc/app.bsky.video.uploadVideo") => StubHttp.Json("""{"jobId":"job-1","did":"did:plc:me","state":"JOB_STATE_ENCODING"}"""),
			("video.bsky.app", "/xrpc/app.bsky.video.getJobStatus") => StubHttp.Json(
				"""{"jobStatus":{"jobId":"job-1","state":"JOB_STATE_COMPLETED","blob":{"$type":"blob","ref":{"$link":"bafkvideo"},"mimeType":"video/mp4","size":9}}}"""),
			(_, "/xrpc/com.atproto.repo.createRecord") => StubHttp.Json("""{"uri":"at://did:plc:me/app.bsky.feed.post/x","cid":"c"}"""),
			_ => new HttpResponseMessage(HttpStatusCode.NotFound)
		});
		var publisher = new BlueskyPublisher(new HttpClient(stub), new BlueskyCredentials("me.bsky.social", "app-password"), _clock);

		_ = await publisher.PublishAsync(Request(Network.Bluesky, Video()), CancellationToken.None);

		var auth = stub.Requests.Single(r => r.Request.RequestUri!.AbsolutePath.EndsWith("getServiceAuth", StringComparison.Ordinal)).Request.RequestUri!.Query;
		Uri.UnescapeDataString(auth).Should().Contain("aud=did:web:morel.us-east.host.bsky.network").And.Contain("lxm=com.atproto.repo.uploadBlob");
		var upload = stub.Requests.Single(r => r.Request.RequestUri!.AbsolutePath.EndsWith("uploadVideo", StringComparison.Ordinal)).Request;
		upload.Headers.Authorization!.Parameter.Should().Be("service-token");
		Uri.UnescapeDataString(upload.RequestUri!.Query).Should().Contain("did=did:plc:me");
		var embed = JsonDocument.Parse(stub.Requests.Last().Body).RootElement.GetProperty("record").GetProperty("embed");
		embed.GetProperty("$type").GetString().Should().Be("app.bsky.embed.video");
		embed.GetProperty("video").GetProperty("ref").GetProperty("$link").GetString().Should().Be("bafkvideo");
		embed.GetProperty("alt").GetString().Should().Be("A run turning a flowchart into an SVG");
		embed.GetProperty("aspectRatio").GetProperty("width").GetInt32().Should().Be(1080);
		embed.GetProperty("aspectRatio").GetProperty("height").GetInt32().Should().Be(1920);
		_clock.Waits.Should().Equal([TimeSpan.FromSeconds(2)], "one wait for the processing job, by the app's clock");
	}

	[Test]
	public async Task A_video_bluesky_cannot_process_is_a_refusal_and_nothing_is_posted()
	{
		var stub = new StubHttp((request, _) => request.RequestUri!.AbsolutePath switch
		{
			"/xrpc/com.atproto.server.createSession" => StubHttp.Json("""{"did":"did:plc:me","handle":"me.bsky.social","accessJwt":"jwt"}"""),
			"/xrpc/com.atproto.server.getServiceAuth" => StubHttp.Json("""{"token":"t"}"""),
			"/xrpc/app.bsky.video.uploadVideo" => StubHttp.Json("""{"jobId":"job-1","state":"JOB_STATE_ENCODING"}"""),
			"/xrpc/app.bsky.video.getJobStatus" => StubHttp.Json("""{"jobStatus":{"jobId":"job-1","state":"JOB_STATE_FAILED","error":"unsupported codec"}}"""),
			_ => new HttpResponseMessage(HttpStatusCode.NotFound)
		});
		var publisher = new BlueskyPublisher(new HttpClient(stub), new BlueskyCredentials("me.bsky.social", "app-password"), _clock);

		var publishing = () => publisher.PublishAsync(Request(Network.Bluesky, Video()), CancellationToken.None);

		(await publishing.Should().ThrowAsync<PublishException>()).WithMessage("*unsupported codec*");
		stub.Requests.Should().NotContain(r => r.Request.RequestUri!.AbsolutePath.EndsWith("createRecord", StringComparison.Ordinal));
	}

	[Test]
	public async Task LinkedIn_puts_each_byte_range_it_hands_out_and_finalizes_with_the_etags_in_order()
	{
		var video = Video(padding: 10_000);
		var half = video.Bytes.Length / 2;
		var parts = new List<byte[]>();
		var statusChecks = 0;
		var api = new StubHttp((request, body) =>
		{
			var path = request.RequestUri!.PathAndQuery;
			if (path.StartsWith("/rest/videos?action=initializeUpload", StringComparison.Ordinal))
			{
				return StubHttp.Json($$$"""
					{"value":{"video":"urn:li:video:V1","uploadToken":"tok","uploadInstructions":[
						{"uploadUrl":"https://upload.linkedin.test/1","firstByte":0,"lastByte":{{{half - 1}}}},
						{"uploadUrl":"https://upload.linkedin.test/2","firstByte":{{{half}}},"lastByte":{{{video.Bytes.Length - 1}}}}]}}
					""");
			}
			if (request.RequestUri.Host == "upload.linkedin.test")
			{
				parts.Add(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());
				var ok = new HttpResponseMessage(HttpStatusCode.OK);
				ok.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"etag-{request.RequestUri.AbsolutePath.TrimStart('/')}\"");
				return ok;
			}
			if (path.StartsWith("/rest/videos?action=finalizeUpload", StringComparison.Ordinal))
			{
				return new HttpResponseMessage(HttpStatusCode.OK);
			}
			if (path.StartsWith("/rest/videos/urn", StringComparison.Ordinal))
			{
				return StubHttp.Json(++statusChecks < 2 ? """{"id":"urn:li:video:V1","status":"PROCESSING"}""" : """{"id":"urn:li:video:V1","status":"AVAILABLE"}""");
			}
			var created = new HttpResponseMessage(HttpStatusCode.Created);
			created.Headers.Add("x-restli-id", "urn:li:share:7001");
			return created;
		});
		var credentials = new LinkedInCredentials("li-token", "urn:li:person:abc", "Martijn", At.AddDays(60));

		_ = await new LinkedInPublisher(new HttpClient(api), credentials, _clock).PublishAsync(Request(Network.LinkedIn, video), CancellationToken.None);

		parts.Should().HaveCount(2);
		parts[0].Should().Equal(video.Bytes[..half]);
		parts[1].Should().Equal(video.Bytes[half..]);
		var init = JsonDocument.Parse(api.Requests[0].Body).RootElement.GetProperty("initializeUploadRequest");
		init.GetProperty("owner").GetString().Should().Be("urn:li:person:abc");
		init.GetProperty("fileSizeBytes").GetInt64().Should().Be(video.Bytes.Length);
		var finalize = JsonDocument.Parse(api.Requests.Single(r => r.Request.RequestUri!.Query.Contains("finalizeUpload", StringComparison.Ordinal)).Body).RootElement;
		finalize.GetProperty("finalizeUploadRequest").GetProperty("uploadedPartIds").EnumerateArray().Select(e => e.GetString())
			.Should().Equal("\"etag-1\"", "\"etag-2\"");
		var post = JsonDocument.Parse(api.Requests.Last().Body).RootElement;
		post.GetProperty("content").GetProperty("media").GetProperty("id").GetString().Should().Be("urn:li:video:V1");
		_clock.Waits.Should().Equal([TimeSpan.FromSeconds(2)], "it waits while LinkedIn processes, by the app's clock");
	}

	[Test]
	public async Task X_uploads_a_video_in_indexed_chunks_waits_for_processing_and_posts_it()
	{
		var video = Video(padding: XPublisher.ChunkBytes + 1000);
		var statusChecks = 0;
		var api = new StubHttp((request, _) =>
		{
			var path = request.RequestUri!.AbsolutePath;
			return path switch
			{
				"/2/media/upload/initialize" => StubHttp.Json("""{"data":{"id":"777","media_key":"7_777","expires_after_secs":86400}}"""),
				"/2/media/upload/777/append" => new HttpResponseMessage(HttpStatusCode.OK),
				"/2/media/upload/777/finalize" => StubHttp.Json("""{"data":{"id":"777","processing_info":{"state":"pending","check_after_secs":1}}}"""),
				"/2/media/upload" when request.Method == HttpMethod.Get => StubHttp.Json(++statusChecks < 2
					? """{"data":{"id":"777","processing_info":{"state":"in_progress","check_after_secs":1}}}"""
					: """{"data":{"id":"777","processing_info":{"state":"succeeded"}}}"""),
				"/2/media/metadata" => StubHttp.Json("{}"),
				"/2/tweets" => StubHttp.Json("""{"data":{"id":"9001","text":"x"}}""", HttpStatusCode.Created),
				_ => new HttpResponseMessage(HttpStatusCode.NotFound)
			};
		});

		var receipt = await new XPublisher(new HttpClient(api), new XCredentials("ck", "cs", "at", "ats", "mpdreamz"), _clock)
			.PublishAsync(Request(Network.X, video), CancellationToken.None);

		var init = JsonDocument.Parse(api.Requests[0].Body).RootElement;
		init.GetProperty("media_type").GetString().Should().Be("video/mp4");
		init.GetProperty("media_category").GetString().Should().Be("tweet_video");
		init.GetProperty("total_bytes").GetInt64().Should().Be(video.Bytes.Length);
		var appends = api.Requests.Where(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/append", StringComparison.Ordinal)).ToList();
		appends.Should().HaveCount(2);
		appends[1].Body.Should().Contain("name=segment_index").And.Contain("\r\n\r\n1\r\n");
		statusChecks.Should().Be(2);
		_clock.Waits.Should().Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], "as long as X's check_after_secs says");
		api.Requests.Where(r => r.Request.Method == HttpMethod.Get).Select(r => r.Request.RequestUri!.Query).Distinct()
			.Should().Equal("?command=STATUS&media_id=777");
		JsonDocument.Parse(api.Requests.Last().Body).RootElement.GetProperty("media").GetProperty("media_ids")[0].GetString().Should().Be("777");
		receipt.Url.Should().Be("https://x.com/mpdreamz/status/9001");
	}

	[Test]
	public async Task A_video_x_could_not_process_is_a_refusal_before_anything_is_posted()
	{
		var api = new StubHttp((request, _) => request.RequestUri!.AbsolutePath switch
		{
			"/2/media/upload/initialize" => StubHttp.Json("""{"data":{"id":"777"}}"""),
			"/2/media/upload/777/append" => new HttpResponseMessage(HttpStatusCode.OK),
			"/2/media/upload/777/finalize" => StubHttp.Json("""{"data":{"id":"777","processing_info":{"state":"failed"}}}"""),
			_ => new HttpResponseMessage(HttpStatusCode.NotFound)
		});

		var publishing = () => new XPublisher(new HttpClient(api), new XCredentials("ck", "cs", "at", "ats"), _clock)
			.PublishAsync(Request(Network.X, Video()), CancellationToken.None);

		_ = await publishing.Should().ThrowAsync<PublishException>();
		api.Requests.Should().NotContain(r => r.Request.RequestUri!.AbsolutePath == "/2/tweets");
	}

	[Test]
	public async Task Mastodon_waits_for_a_video_to_be_processed_before_posting_it()
	{
		var checks = 0;
		var stub = new StubHttp((request, _) => request.RequestUri!.AbsolutePath switch
		{
			"/api/v2/media" => StubHttp.Json("""{"id":"m1","url":null}""", HttpStatusCode.Accepted),
			"/api/v1/media/m1" => ++checks < 2
				? new HttpResponseMessage(HttpStatusCode.PartialContent)
				: StubHttp.Json("""{"id":"m1","url":"https://files.example/m1.mp4"}"""),
			"/api/v1/statuses" => StubHttp.Json("""{"id":"s1","url":"https://hachyderm.io/@me/s1"}"""),
			_ => new HttpResponseMessage(HttpStatusCode.NotFound)
		});

		_ = await new MastodonPublisher(new HttpClient(stub), new MastodonCredentials("https://hachyderm.io", "token"), _clock)
			.PublishAsync(Request(Network.Mastodon, Video()), CancellationToken.None);

		checks.Should().Be(2);
		_clock.Waits.Should().Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)], "a video is checked every two seconds");
		stub.Requests[0].Body.Should().Contain("video/mp4").And.Contain("A run turning a flowchart into an SVG");
		JsonDocument.Parse(stub.Requests.Last().Body).RootElement.GetProperty("media_ids")[0].GetString().Should().Be("m1");
	}

	[Test]
	public async Task The_store_names_a_video_by_its_bytes_keeps_one_copy_and_reads_nothing_outside_its_folder()
	{
		var fs = new MockFileSystem();
		var store = new FileVideoStore(fs, "/config/media");
		var bytes = Mp4Fixture.Make(1280, 720, 5);

		var first = await store.SaveAsync(bytes, "A run", CancellationToken.None);
		var second = await store.SaveAsync(bytes, "Another description", CancellationToken.None);

		first.Path.Should().Be(second.Path).And.MatchRegex("^video:sha256-[0-9a-f]{64}\\.mp4$");
		fs.Directory.GetFiles("/config/media").Should().ContainSingle();
		(await store.ReadAsync(first.Path, CancellationToken.None)).Should().Equal(bytes);
		store.PathOf("video:../../config/secrets.json").Should().BeNull();
		(await store.ReadAsync($"video:sha256-{new string('c', 64)}.mp4", CancellationToken.None)).Should().BeNull();
	}

	[Test]
	public async Task A_video_reached_through_a_link_is_not_read()
	{
		var fs = new MockFileSystem();
		fs.AddFile("/elsewhere/secret.mp4", new MockFileData(Mp4Fixture.Make(1280, 720, 5)));
		var name = $"sha256-{new string('d', 64)}.mp4";
		fs.AddDirectory("/config/media");
		_ = fs.File.CreateSymbolicLink($"/config/media/{name}", "/elsewhere/secret.mp4");
		var store = new FileVideoStore(fs, "/config/media");

		(await store.ReadAsync($"video:{name}", CancellationToken.None)).Should().BeNull();
	}
}
