using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Braggarr.Adapters.Social;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Web.Tests;

public class XTests
{
	/// <summary>Timers fire at once and each wait is recorded: no test here sleeps.</summary>
	private readonly InstantTime _clock = new(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));

	private static readonly XCredentials Keys = new("ck", "cs", "at", "ats", "mpdreamz");

	private static PublishRequest Request(params MediaFile[] media) =>
		new(new DeliveryKey("curb-1-0", "01-launch", Network.X), DateTimeOffset.UnixEpoch, new Variant(Network.X, "Curb 1.0 is out.")) { Media = media };

	[Test]
	public void The_signature_matches_the_published_oauth_1_example()
	{
		// The worked example from X's (then Twitter's) "Creating a signature" page.
		var signature = OAuth1.Signature(
			"POST",
			"https://api.twitter.com/1.1/statuses/update.json?include_entities=true",
			[
				new("status", "Hello Ladies + Gentlemen, a signed OAuth request!"),
				new("oauth_consumer_key", "xvz1evFS4wEEPTGEFPHBog"),
				new("oauth_nonce", "kYjzVBB8Y0ZFabxSWbWovY3uYSQ2pTgmZeNu2VS4cg"),
				new("oauth_signature_method", "HMAC-SHA1"),
				new("oauth_timestamp", "1318622958"),
				new("oauth_token", "370773112-GmHxMAgYyLbNEtIKZeRNFsMKPR9EyMZeS9weJAEb"),
				new("oauth_version", "1.0")
			],
			"kAcSOqF21Fu85e7zjz7ZN2U4ZRhfV3WpwPAoE3Z7kBw",
			"LswwdoUaIvS8ltyTt5jkRh4J50vUPVVHtR2YPi5kE"
		);

		signature.Should().Be("hCtSmYh+iHYCEqBWrE7C7hYmtUk=");
	}

	[Test]
	public async Task A_text_post_is_signed_and_links_to_the_status()
	{
		var api = new StubHttp((_, _) => StubHttp.Json("""{"data":{"id":"1850000000000000001","text":"Curb 1.0 is out."}}""", HttpStatusCode.Created));

		var receipt = await new XPublisher(new HttpClient(api), Keys, _clock).PublishAsync(Request(), CancellationToken.None);

		receipt.Should().Be(new Receipt("1850000000000000001", "https://x.com/mpdreamz/status/1850000000000000001"));
		var (request, body) = api.Requests.Single();
		request.RequestUri!.ToString().Should().Be("https://api.x.com/2/tweets");
		request.Headers.Authorization!.Scheme.Should().Be("OAuth");
		request.Headers.Authorization.Parameter.Should().Contain("oauth_consumer_key=\"ck\"").And.Contain("oauth_token=\"at\"").And.Contain("oauth_signature=\"");
		JsonDocument.Parse(body).RootElement.GetProperty("text").GetString().Should().Be("Curb 1.0 is out.");
	}

	[Test]
	public async Task An_image_is_uploaded_described_and_attached()
	{
		var api = new StubHttp((request, _) => request.RequestUri!.AbsolutePath switch
		{
			"/2/media/upload" => StubHttp.Json("""{"data":{"id":"710511363345354753"}}"""),
			"/2/media/metadata" => StubHttp.Json("""{"data":{"id":"710511363345354753"}}"""),
			_ => StubHttp.Json("""{"data":{"id":"1","text":"x"}}""", HttpStatusCode.Created)
		});

		_ = await new XPublisher(new HttpClient(api), Keys, _clock)
			.PublishAsync(Request(new MediaFile("media/card.png", "image/png", [1, 2], "Curb's logo.")), CancellationToken.None);

		api.Requests[0].Body.Should().Contain("name=media_category").And.Contain("tweet_image");
		var alt = JsonDocument.Parse(api.Requests[1].Body).RootElement;
		alt.GetProperty("id").GetString().Should().Be("710511363345354753");
		alt.GetProperty("metadata").GetProperty("alt_text").GetProperty("text").GetString().Should().Be("Curb's logo.");
		JsonDocument.Parse(api.Requests[2].Body).RootElement.GetProperty("media").GetProperty("media_ids")[0].GetString().Should().Be("710511363345354753");
	}

	[Test]
	public async Task A_refusal_can_be_retried_and_a_server_error_is_left_to_the_owner()
	{
		Func<Task> refused = () => new XPublisher(new HttpClient(new StubHttp((_, _) => StubHttp.Json("{}", HttpStatusCode.Forbidden))), Keys, _clock)
			.PublishAsync(Request(), CancellationToken.None);
		Func<Task> lost = () => new XPublisher(new HttpClient(new StubHttp((_, _) => StubHttp.Json("{}", HttpStatusCode.ServiceUnavailable))), Keys, _clock)
			.PublishAsync(Request(), CancellationToken.None);

		_ = await refused.Should().ThrowAsync<PublishException>();
		(await lost.Should().ThrowAsync<HttpRequestException>()).Which.Should().NotBeOfType<PublishException>();
	}
}
