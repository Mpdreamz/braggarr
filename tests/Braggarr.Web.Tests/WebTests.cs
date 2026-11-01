using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Braggarr.Domain;
using Braggarr.Web.Auth;
using Microsoft.Extensions.Logging;

namespace Braggarr.Web.Tests;

public class WebTests
{
	private static readonly string[] BlueskyOnly = ["bluesky"];

	private static readonly string[] BlueskyAndLinkedIn = ["bluesky", "linkedin"];

	private static readonly string[] BlueskyAndX = ["bluesky", "x"];

	private static object Campaign(string title = "Mermaider 2.0", string[]? networks = null) => new
	{
		title,
		project = "mermaider",
		networks = networks ?? ["bluesky", "mastodon"],
		links = new[] { "https://github.com/nullean/mermaider" },
		brief = "Announce 2.0.",
		posts = new[]
		{
			new { at = "2026-11-03T10:00:00+01:00", variants = new Dictionary<string, string> { ["bluesky"] = "Mermaider 2.0 is out.", ["mastodon"] = "Mermaider 2.0 is out." } }
		}
	};

	[Test]
	public async Task The_api_refuses_a_missing_or_unknown_key()
	{
		using var factory = new BraggarrFactory();

		(await factory.Browser().GetAsync("/api/v1/campaigns")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		(await factory.Agent("brag_not-a-key").GetAsync("/api/v1/campaigns")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		(await factory.Agent().GetAsync("/api/v1/campaigns")).StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Test]
	public async Task An_agents_campaign_becomes_a_proposal_in_its_name()
	{
		using var factory = new BraggarrFactory();

		var response = await factory.Agent().PostAsJsonAsync("/api/v1/campaigns", Campaign());

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		var proposal = factory.Reviews.Open.Should().ContainSingle().Which;
		proposal.Campaign.Status.Should().Be(CampaignStatus.Proposed);
		proposal.Campaign.CreatedBy.Should().Be("agent:release-bot");
		proposal.Campaign.Slug.Should().Be("mermaider-2-0");
	}

	[Test]
	public async Task Talking_points_with_wanted_media_are_laid_out_drafted_and_shown_on_the_proposal()
	{
		using var factory = new BraggarrFactory();

		var response = await factory.Agent().PostAsJsonAsync("/api/v1/campaigns", new
		{
			title = "Curb 1.0",
			project = "curb",
			networks = BlueskyAndLinkedIn,
			brief = "Curb formats C#.",
			start = "2026-11-02T10:00:00+01:00",
			points = new object[]
			{
				new { text = "Keeps diffs small", wanted = new[] { new { kind = "video", description = "A run where one line changes" } } },
				new { text = "Fast" }
			}
		});

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		var view = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
		view.GetProperty("points").GetArrayLength().Should().Be(2);
		view.GetProperty("posts").EnumerateArray().Select(p => p.GetProperty("id").GetString()).Should().Equal("00-overview", "01-keeps-diffs-small", "02-fast");
		var first = view.GetProperty("posts")[1];
		first.GetProperty("networks").EnumerateArray().Select(n => n.GetString()).Should().Equal("bluesky");
		first.GetProperty("points").EnumerateArray().Select(n => n.GetInt32()).Should().Equal(1);
		first.GetProperty("variants")[0].GetProperty("media")[0].GetProperty("wanted").GetBoolean().Should().BeTrue();
		factory.Reviews.Open.Single().Campaign.IsReady.Should().BeTrue();

		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);
		var page = await browser.GetStringAsync("/Campaigns/Proposal?number=1");
		page.Should().Contain("Talking points").And.Contain("Video wanted").And.Contain("A run where one line changes");
	}

	[Test]
	public async Task A_request_with_no_points_brief_or_posts_is_refused_and_so_is_an_unknown_media_kind()
	{
		using var factory = new BraggarrFactory();

		var empty = await factory.Agent().PostAsJsonAsync("/api/v1/campaigns", new { title = "Curb", project = "curb", networks = BlueskyOnly });
		var gif = await factory.Agent().PostAsJsonAsync("/api/v1/campaigns", new
		{
			title = "Curb",
			project = "curb",
			networks = BlueskyOnly,
			points = new[] { new { text = "Small diffs", wanted = new[] { new { kind = "gif", description = "A diff" } } } }
		});

		(await empty.Content.ReadAsStringAsync()).Should().Contain("Give talking points (preferred)");
		(await gif.Content.ReadAsStringAsync()).Should().Contain("an image or a video");
		factory.Reviews.Open.Should().BeEmpty();
	}

	[Test]
	public async Task A_post_id_that_would_write_into_another_campaigns_folder_is_refused()
	{
		using var factory = new BraggarrFactory();

		var response = await factory.Agent().PostAsJsonAsync("/api/v1/campaigns", new
		{
			title = "Curb 1.0",
			project = "curb",
			networks = BlueskyOnly,
			brief = "Curb formats C#.",
			posts = new[] { new { id = "../heed-0-20/01-launch", at = "2026-11-02T10:00:00+01:00", variants = new Dictionary<string, string> { ["bluesky"] = "Out." } } }
		});

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await response.Content.ReadAsStringAsync()).Should().Contain("A post's id is lowercase letters, digits and dashes");
		factory.Reviews.Open.Should().BeEmpty();
	}

	[Test]
	public async Task A_campaign_changed_since_approval_says_so_and_the_owner_approves_it_again()
	{
		using var factory = new BraggarrFactory();
		var campaign = new Campaign("curb-1-0", "Curb 1.0", "curb", CampaignStatus.Approved, [Network.Bluesky], ["https://github.com/nullean/curb"], "", "owner:mpdreamz",
			[new Post("01-launch", factory.Time.GetUtcNow().AddDays(1), [new Variant(Network.Bluesky, "Curb 1.0, edited on main.")])])
		{
			Approval = new Approval("owner:mpdreamz", "the-digest-before-the-edit", factory.Time.GetUtcNow().AddDays(-1))
		};
		factory.Repository.Campaigns.Add(campaign);
		var scheduling = factory.Services.GetRequiredService<Braggarr.Services.SchedulingService>();
		await scheduling.StartAsync(CancellationToken.None);
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);

		var page = await browser.GetStringAsync("/Campaigns/Detail?slug=curb-1-0");
		var token = await BraggarrFactory.FormTokenAsync(browser, "/Campaigns/Detail?slug=curb-1-0");
		_ = await browser.PostAsync("/Campaigns/Detail?handler=Reapprove&slug=curb-1-0", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

		page.Should().Contain("Changed since it was approved by owner:mpdreamz").And.Contain("Approve again");
		factory.Repository.Campaigns.Single().Approval.Should().Be(new Approval("owner:mpdreamz", campaign.Digest, factory.Time.GetUtcNow()));
	}

	/// <summary>The warning a week before a token runs out is by the app's clock: on the real one, 2030 is years away.</summary>
	[Test]
	public async Task A_token_that_runs_out_this_week_is_flagged_by_the_apps_clock()
	{
		using var factory = new BraggarrFactory();
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);
		var accounts = factory.Services.GetRequiredService<Braggarr.Adapters.Social.AccountStore>();
		var token = new Braggarr.Adapters.Social.LinkedInCredentials("t", "urn:li:person:a", "Martijn", factory.Time.GetUtcNow().AddDays(30));
		accounts.Set(Network.LinkedIn, "Martijn", token);

		var later = await browser.GetStringAsync("/Settings?section=networks");
		factory.Time.Advance(TimeSpan.FromDays(27));
		// The session ended too, after two weeks unused, by the same clock.
		(await browser.GetAsync("/Settings?section=networks")).StatusCode.Should().Be(HttpStatusCode.Redirect);
		_ = await BraggarrFactory.SignInAsync(browser);
		var soon = await browser.GetStringAsync("/Settings?section=networks");

		soon.Should().Contain("<span class=\"badge failed\">Martijn,");
		later.Should().Contain("<span class=\"badge published\">Martijn,");
	}

	[Test]
	public async Task A_bad_request_is_answered_with_its_problems_before_anything_is_proposed()
	{
		using var factory = new BraggarrFactory();

		var response = await factory.Agent().PostAsJsonAsync("/api/v1/campaigns", Campaign(title: " ", networks: ["myspace"]));

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().Contain("A campaign needs a title.").And.Contain("Every network is one of");
		factory.Reviews.Open.Should().BeEmpty();
	}

	[Test]
	public async Task No_key_can_approve_there_is_no_route_and_the_pages_want_the_owner()
	{
		using var factory = new BraggarrFactory();
		var agent = factory.Agent();
		_ = await agent.PostAsJsonAsync("/api/v1/campaigns", Campaign());

		(await agent.PostAsync("/api/v1/campaigns/mermaider-2-0/approve", null)).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
		var page = await agent.PostAsync("/Campaigns/Proposal?handler=Approve&number=1", null);
		page.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest);

		factory.Reviews.Merged.Should().BeEmpty();
	}

	[Test]
	public async Task The_owner_approves_on_the_page_and_that_merges()
	{
		using var factory = new BraggarrFactory();
		_ = await factory.Agent().PostAsJsonAsync("/api/v1/campaigns", Campaign());
		var browser = factory.Browser();
		(await BraggarrFactory.SignInAsync(browser)).Should().Be("/");

		var token = await BraggarrFactory.FormTokenAsync(browser, "/Campaigns/Proposal?number=1");
		var response = await browser.PostAsync("/Campaigns/Proposal?handler=Approve&number=1", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

		response.Headers.Location!.OriginalString.Should().Be("/");
		factory.Reviews.Merged.Should().ContainSingle().Which.Should().Be(Actor.Owner("mpdreamz"));
	}

	[Test]
	public async Task Only_the_owners_github_id_signs_in()
	{
		using var factory = new BraggarrFactory { SignInAs = 7 };
		var browser = factory.Browser();

		(await BraggarrFactory.SignInAsync(browser)).Should().Be("/denied");
		(await browser.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.Redirect);
	}

	[Test]
	public async Task The_setup_page_opens_only_with_the_setup_token()
	{
		using var factory = new BraggarrFactory();
		var browser = factory.Browser();

		var refused = await browser.GetAsync("/setup/start?token=guess");

		refused.Headers.Location!.OriginalString.Should().Be("/denied");
		(await browser.GetStringAsync("/setup")).Should().Contain("one-time token");
	}

	[Test]
	public async Task A_form_without_its_anti_forgery_token_is_refused()
	{
		using var factory = new BraggarrFactory();
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);

		var response = await browser.PostAsync("/Settings?handler=CreateKey", new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "sneaky" }));

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		factory.Settings.Current.ApiKeys.Should().NotContain(k => k.Name == "sneaky");
	}

	[Test]
	[Arguments("/setup")]
	[Arguments("/denied")]
	[Arguments("/healthz")]
	public async Task Every_response_carries_the_security_headers(string path)
	{
		using var factory = new BraggarrFactory();

		var response = await factory.Browser().GetAsync(path);

		response.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("script-src 'self'").And.Contain("frame-ancestors 'none'");
		response.Headers.GetValues("X-Frame-Options").Single().Should().Be("DENY");
		response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
	}

	[Test]
	public async Task A_revoked_key_stops_working_at_once()
	{
		using var factory = new BraggarrFactory();
		var agent = factory.Agent();
		(await agent.GetAsync("/api/v1/campaigns")).StatusCode.Should().Be(HttpStatusCode.OK);

		_ = factory.Settings.Update(s => s with { ApiKeys = [] });

		(await agent.GetAsync("/api/v1/campaigns")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Test]
	public async Task Each_key_is_limited_to_sixty_requests_a_minute()
	{
		using var factory = new BraggarrFactory();
		var agent = factory.Agent();

		var statuses = new List<HttpStatusCode>();
		for (var i = 0; i < 61; i++)
		{
			statuses.Add((await agent.GetAsync("/api/v1/calendar")).StatusCode);
		}

		statuses.Take(60).Should().OnlyContain(s => s == HttpStatusCode.OK);
		statuses[^1].Should().Be(HttpStatusCode.TooManyRequests);
	}

	[Test]
	public async Task An_agent_that_sends_only_times_gets_every_variant_drafted()
	{
		using var factory = new BraggarrFactory();

		var response = await factory.Agent().PostAsJsonAsync("/api/v1/campaigns", new
		{
			title = "Heed 0.21",
			project = "heed",
			networks = BlueskyAndX,
			posts = new[] { new { at = "2026-11-04T10:00:00+01:00" }, new { at = "2026-11-11T10:00:00+01:00" } }
		});

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		var campaign = factory.Reviews.Open.Single().Campaign;
		campaign.IsComplete.Should().BeTrue();
		campaign.Posts.Select(p => p.Id).Should().Equal("01-post", "02-post");
		campaign.Posts[1].For(Network.X)!.Text.Should().Be("Heed 0.21 on X.");
	}

	[Test]
	public async Task An_agent_can_ask_for_one_variant_again_with_a_note()
	{
		using var factory = new BraggarrFactory();
		var agent = factory.Agent();
		_ = await agent.PostAsJsonAsync("/api/v1/campaigns", Campaign());

		var response = await agent.PostAsJsonAsync("/api/v1/campaigns/mermaider-2-0/posts/01-post/redraft", new { network = "mastodon", note = "Mention SVG" });

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var post = factory.Reviews.Open.Single().Campaign.Posts.Single();
		post.For(Network.Mastodon)!.Text.Should().Be("Mention SVG (Mastodon)");
		post.For(Network.Bluesky)!.Text.Should().Be("Mermaider 2.0 is out.");
		factory.Reviews.Merged.Should().BeEmpty();
	}

	[Test]
	public async Task An_agent_uploads_an_image_and_only_the_owner_can_see_it()
	{
		using var factory = new BraggarrFactory();
		var agent = factory.Agent();
		_ = await agent.PostAsJsonAsync("/api/v1/campaigns", Campaign());
		using var form = new MultipartFormDataContent
		{
			{ new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]), "file", "Screen Shot.png" },
			{ new StringContent("01-post"), "post" }
		};

		var upload = await agent.PostAsync("/api/v1/campaigns/mermaider-2-0/media", form);

		upload.StatusCode.Should().Be(HttpStatusCode.OK);
		var variant = factory.Reviews.Open.Single().Campaign.Posts.Single().For(Network.Bluesky)!;
		variant.Media.Single().Should().Be(new MediaRef("media/screen-shot.png", "A screenshot of the project."));

		(await agent.GetAsync("/campaigns/mermaider-2-0/media/screen-shot.png")).StatusCode.Should().NotBe(HttpStatusCode.OK);
		(await factory.Browser().GetAsync("/campaigns/mermaider-2-0/media/screen-shot.png")).StatusCode.Should().Be(HttpStatusCode.Redirect);
		var owner = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(owner);
		var image = await owner.GetAsync("/campaigns/mermaider-2-0/media/screen-shot.png");
		image.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
		(await image.Content.ReadAsByteArrayAsync()).Should().Equal(0x89, 0x50, 0x4E, 0x47);
	}

	[Test]
	public async Task An_agent_uploads_a_video_larger_than_an_image_may_be_kept_under_config_media_and_only_the_owner_can_watch_it()
	{
		using var factory = new BraggarrFactory();
		var agent = factory.Agent();
		_ = await agent.PostAsJsonAsync("/api/v1/campaigns", Campaign());
		var mp4 = Braggarr.Services.Tests.Mp4Fixture.Make(1920, 1080, 20, padding: 2_000_000);
		using var form = new MultipartFormDataContent
		{
			{ new ByteArrayContent(mp4), "file", "Run.mp4" },
			{ new StringContent("01-post"), "post" },
			{ new StringContent("A run turning a flowchart into an SVG"), "alt" }
		};

		var upload = await agent.PostAsync("/api/v1/campaigns/mermaider-2-0/media", form);

		upload.StatusCode.Should().Be(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync());
		var video = factory.Reviews.Open.Single().Campaign.Posts.Single().For(Network.Bluesky)!.Media.Single();
		video.IsStoredVideo.Should().BeTrue();
		var name = video.Path[MediaRef.VideoPrefix.Length..];
		File.ReadAllBytes(Path.Combine(factory.ConfigDirectory, "media", name)).Should().Equal(mp4);
		factory.Reviews.Media.Should().BeEmpty("a video never goes into git");

		(await agent.GetAsync($"/videos/{name}")).StatusCode.Should().NotBe(HttpStatusCode.OK);
		var owner = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(owner);
		var watched = await owner.GetAsync($"/videos/{name}");
		watched.Content.Headers.ContentType!.MediaType.Should().Be("video/mp4");
		watched.Headers.AcceptRanges.Should().Contain("bytes");
		(await owner.GetStringAsync("/Campaigns/Proposal?number=1")).Should().Contain($"<video controls preload=\"metadata\" src=\"/videos/{name}\"");
		(await owner.GetAsync("/videos/..%2Fsecrets.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Test]
	public async Task The_owner_edits_a_proposal_on_its_page()
	{
		using var factory = new BraggarrFactory();
		_ = await factory.Agent().PostAsJsonAsync("/api/v1/campaigns", Campaign());
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);
		var token = await BraggarrFactory.FormTokenAsync(browser, "/Campaigns/Proposal?number=1");

		var response = await browser.PostAsync("/Campaigns/Proposal?handler=Edit&number=1", new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["__RequestVerificationToken"] = token,
			["post"] = "01-post",
			["at"] = "2026-11-04T11:30",
			["texts[Bluesky]"] = "Mermaider 2.0: diagrams to SVG, no browser.",
			["texts[Mastodon]"] = "Mermaider 2.0 is out."
		}));

		response.Headers.Location!.OriginalString.Should().Contain("/Campaigns/Proposal?number=1");
		var post = factory.Reviews.Open.Single().Campaign.Posts.Single();
		post.For(Network.Bluesky)!.Text.Should().Be("Mermaider 2.0: diagrams to SVG, no browser.");
		post.At.Should().Be(new DateTimeOffset(2026, 11, 4, 11, 30, 0, TimeSpan.FromHours(1)));
	}

	[Test]
	public async Task Without_trusted_proxies_no_forwarded_header_is_believed()
	{
		using var factory = new BraggarrFactory();
		var browser = factory.Browser();
		browser.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
		browser.DefaultRequestHeaders.Add("X-Forwarded-Host", "evil.example");

		var login = await browser.GetAsync("/login");

		login.Headers.Location!.Query.Should().Contain(Uri.EscapeDataString("http://localhost/login/callback"));
	}

	[Test]
	public async Task An_agent_asks_for_an_introduction_and_it_opens_as_a_proposal_in_the_background()
	{
		using var factory = new BraggarrFactory();

		var response = await factory.Agent().PostAsJsonAsync("/api/v1/projects/introduce", new { repository = "nullean/curb", name = "Curb", networks = BlueskyAndX });

		response.StatusCode.Should().Be(HttpStatusCode.Accepted);
		for (var i = 0; i < 50 && factory.Reviews.Open.Count == 0; i++)
		{
			await Task.Delay(100);
		}
		var campaign = factory.Reviews.Open.Should().ContainSingle().Which.Campaign;
		campaign.Slug.Should().Be("curb-introduction");
		campaign.Posts.Select(p => p.Angle).Should().Equal("What it is", "The CLI");
		campaign.CreatedBy.Should().Be("agent:introducer");
	}

	[Test]
	[Arguments("/", 200)]
	[Arguments("/Calendar", 200)]
	[Arguments("/Calendar?month=-1", 200)]
	[Arguments("/Activity?tab=history", 200)]
	[Arguments("/Activity?tab=events", 200)]
	[Arguments("/Projects", 200)]
	[Arguments("/System", 200)]
	[Arguments("/Settings?section=agents", 200)]
	[Arguments("/Campaigns", 302)]
	[Arguments("/History", 302)]
	public async Task Every_page_answers_the_owner(string page, int status)
	{
		using var factory = new BraggarrFactory();
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);

		var response = await browser.GetAsync(page);

		((int)response.StatusCode).Should().Be(status, await response.Content.ReadAsStringAsync());
	}

	[Test]
	public void Secrets_never_reach_the_log()
	{
		var output = new StringWriter();
		using var provider = new RedactingConsoleProvider(() => ["s3cret-app-password", "gho_user_token"], output);

		provider.CreateLogger("Braggarr.Test").LogWarning(
			new InvalidOperationException("401 for token gho_user_token"),
			"Sign-in with {Password} failed",
			"s3cret-app-password"
		);

		output.ToString().Should().NotContain("s3cret-app-password").And.NotContain("gho_user_token").And.Contain("[redacted]");
	}

	[Test]
	public void A_key_is_found_by_its_hash_only()
	{
		var keys = new[] { new Settings.ApiKeySettings("bot", ApiKeys.Hash("brag_abc"), DateTimeOffset.UnixEpoch) };

		ApiKeys.Find(keys, "brag_abc")!.Name.Should().Be("bot");
		ApiKeys.Find(keys, "brag_abd").Should().BeNull();
		ApiKeys.New().Should().StartWith("brag_").And.HaveLength(48);
	}

	[Test]
	public async Task Every_network_has_its_mark_in_the_sprite_and_the_settings_show_it()
	{
		using var factory = new BraggarrFactory();
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);

		var page = await browser.GetStringAsync("/Settings?section=networks");

		foreach (var network in Networks.All)
		{
			page.Should().Contain($"<symbol id=\"n-{network.Slug()}\"", $"{network} needs its mark");
		}
		page.Should().Contain("<h3><span class=\"net-name\"><svg class=\"net\" aria-hidden=\"true\"><use href=\"#n-youtube\" /></svg>YouTube</span>");
	}

	[Test]
	public async Task The_calendar_shows_a_month_with_what_is_planned_and_what_waits_for_review()
	{
		using var factory = new BraggarrFactory();
		var now = factory.Time.GetUtcNow();
		var first = new DateTimeOffset(now.Year, now.Month, 1, 9, 0, 0, TimeSpan.Zero);
		var planned = new Campaign("curb-1-0", "Curb 1.0", "curb", CampaignStatus.Approved, [Network.Bluesky], ["https://github.com/nullean/curb"], "", "owner:mpdreamz",
			[new Post("01-launch", first.AddDays(3), [new Variant(Network.Bluesky, "Curb 1.0 is out.")])]);
		factory.Repository.Campaigns.Add(planned with { Approval = new Approval("owner:mpdreamz", planned.Digest, now) });
		await factory.Services.GetRequiredService<Braggarr.Services.SchedulingService>().StartAsync(CancellationToken.None);
		var proposal = new
		{
			title = "Mermaider 2.0",
			project = "mermaider",
			networks = new[] { "bluesky" },
			links = new[] { "https://github.com/nullean/mermaider" },
			brief = "Announce 2.0.",
			posts = new[] { new { at = first.AddDays(10), variants = new Dictionary<string, string> { ["bluesky"] = "Mermaider 2.0 is out." } } }
		};
		_ = await factory.Agent().PostAsJsonAsync("/api/v1/campaigns", proposal);
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);

		var month = await browser.GetStringAsync("/Calendar");
		var next = await browser.GetStringAsync("/Calendar?month=1");

		month.Should().Contain(first.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture))
			.And.Contain("class=\"event event-waiting\" href=\"/Campaigns/Detail?slug=curb-1-0\"")
			.And.Contain("class=\"event event-review\" href=\"/Campaigns/Proposal?number=1\"");
		next.Should().NotContain("Curb 1.0").And.NotContain("Mermaider 2.0");
	}
}
