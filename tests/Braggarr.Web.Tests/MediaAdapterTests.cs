using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Braggarr.Adapters.Claude;
using Braggarr.Adapters.Social;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Web.Tests;

public class MediaAdapterTests
{
	/// <summary>Timers fire at once and each wait is recorded: no test here sleeps.</summary>
	private readonly InstantTime _clock = new(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));

	private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 9, 9];
	private static readonly MediaFile Card = new("media/card.png", "image/png", Png, "Curb's logo over a diff.");
	private static readonly PublishRequest Request = new(
		new DeliveryKey("curb-1-0", "01-launch", Network.Bluesky), new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.FromHours(1)),
		new Variant(Network.Bluesky, "Curb 1.0 is out.", [new MediaRef("media/card.png", "Curb's logo over a diff.")])
	)
	{ Media = [Card] };

	[Test]
	public async Task Bluesky_uploads_the_image_then_embeds_the_blob_it_got_back_with_its_alt_text()
	{
		var bluesky = new StubHttp((request, _) => request.RequestUri!.AbsolutePath switch
		{
			"/xrpc/com.atproto.server.createSession" => StubHttp.Json(/*lang=json,strict*/ """{"did":"did:plc:me","handle":"me.bsky.social","accessJwt":"jwt"}"""),
			"/xrpc/com.atproto.repo.uploadBlob" => StubHttp.Json(/*lang=json,strict*/ """{"blob":{"$type":"blob","ref":{"$link":"bafkrei"},"mimeType":"image/png","size":6}}"""),
			_ => StubHttp.Json(/*lang=json,strict*/ """{"uri":"at://x","cid":"c"}""")
		});

		_ = await new BlueskyPublisher(new HttpClient(bluesky), new BlueskyCredentials("me.bsky.social", "pw"), _clock).PublishAsync(Request, CancellationToken.None);

		var upload = bluesky.Requests.Single(r => r.Request.RequestUri!.AbsolutePath.EndsWith("uploadBlob", StringComparison.Ordinal));
		upload.Request.Content!.Headers.ContentType!.MediaType.Should().Be("image/png");
		var embed = JsonDocument.Parse(bluesky.Requests.Last().Body).RootElement.GetProperty("record").GetProperty("embed");
		embed.GetProperty("$type").GetString().Should().Be("app.bsky.embed.images");
		var image = embed.GetProperty("images")[0];
		image.GetProperty("alt").GetString().Should().Be("Curb's logo over a diff.");
		image.GetProperty("image").GetProperty("ref").GetProperty("$link").GetString().Should().Be("bafkrei");
	}

	[Test]
	public async Task Mastodon_waits_for_an_image_it_processes_in_the_background_then_attaches_it()
	{
		var checks = 0;
		var mastodon = new StubHttp((request, _) => request.RequestUri!.AbsolutePath switch
		{
			"/api/v2/media" => StubHttp.Json(/*lang=json,strict*/ """{"id":"77","url":null}""", HttpStatusCode.Accepted),
			"/api/v1/media/77" => ++checks < 2 ? new HttpResponseMessage(HttpStatusCode.PartialContent) : StubHttp.Json(/*lang=json,strict*/ """{"id":"77","url":"https://files/77.png"}"""),
			_ => StubHttp.Json(/*lang=json,strict*/ """{"id":"113","url":"https://hachyderm.io/@me/113"}""")
		});

		_ = await new MastodonPublisher(new HttpClient(mastodon), new MastodonCredentials("https://hachyderm.io", "t"), _clock)
			.PublishAsync(Request with { Key = Request.Key with { Network = Network.Mastodon } }, CancellationToken.None);

		var upload = mastodon.Requests.First();
		upload.Body.Should().Contain("Curb's logo over a diff.");
		checks.Should().Be(2);
		JsonDocument.Parse(mastodon.Requests.Last().Body).RootElement.GetProperty("media_ids")[0].GetString().Should().Be("77");
	}

	[Test]
	public async Task Claude_is_shown_the_image_to_describe_it()
	{
		var api = new StubHttp((_, _) => StubHttp.Json(JsonSerializer.Serialize(new
		{
			id = "msg_1",
			type = "message",
			role = "assistant",
			model = "claude-opus-5-5",
			content = new[] { new { type = "text", text = " A diff with one changed line. " } },
			stop_reason = "end_turn",
			stop_sequence = (string?)null,
			usage = new { input_tokens = 1500, output_tokens = 20, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 }
		})));
		var drafter = new ClaudeDrafter(new HttpClient(api), new ClaudeOptions { ApiKey = "sk-ant-test", BaseUrl = "https://claude.test" });

		var (alt, _) = await drafter.DescribeAsync(Card, "Curb 1.0: a C# formatter", CancellationToken.None);

		alt.Should().Be("A diff with one changed line.");
		var content = JsonDocument.Parse(api.Requests.Single().Body).RootElement.GetProperty("messages")[0].GetProperty("content");
		var image = content[0];
		image.GetProperty("type").GetString().Should().Be("image");
		image.GetProperty("source").GetProperty("media_type").GetString().Should().Be("image/png");
		image.GetProperty("source").GetProperty("data").GetString().Should().Be(Convert.ToBase64String(Png));
		content[1].GetProperty("text").GetString().Should().Contain("Curb 1.0: a C# formatter");
	}
}
