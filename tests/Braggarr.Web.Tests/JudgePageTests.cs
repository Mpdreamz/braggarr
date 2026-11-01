using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Braggarr.Domain;
using Braggarr.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Braggarr.Web.Tests;

/// <summary>What the owner and agents see of the judge, and the owner's settings for it.</summary>
public class JudgePageTests
{
	private static readonly Criterion Honest = new("honest", "Claims nothing the links do not show.", Blocks: true);
	private static readonly Criterion Hashtags = new("hashtags", "No more than two hashtags.", Blocks: false);

	private static object Campaign() => new
	{
		title = "Mermaider 2.0",
		project = "mermaider",
		networks = new[] { "bluesky" },
		links = new[] { "https://github.com/nullean/mermaider" },
		brief = "Announce 2.0.",
		posts = new[] { new { at = "2030-11-03T10:00:00+01:00", variants = new Dictionary<string, string> { ["bluesky"] = "Mermaider 2.0 is out. #dotnet #oss #csharp" } } }
	};

	private static async Task<(BraggarrFactory Factory, HttpClient Browser)> JudgedAsync(Func<JudgeRequest, IReadOnlyList<Finding>>? says = null)
	{
		var factory = new BraggarrFactory();
		factory.Judges.On = true;
		if (says is not null)
		{
			factory.Judges.Says = says;
		}
		factory.Repository.Criteria.AddRange([Honest, Hashtags]);
		_ = await factory.Agent().PostAsJsonAsync("/api/v1/campaigns", Campaign());
		await factory.Services.GetRequiredService<JudgingService>().CheckAsync(CancellationToken.None);
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);
		return (factory, browser);
	}

	private static IReadOnlyList<Finding> HashtagsWarn(JudgeRequest r) =>
		[.. Criteria.Expected(r.Campaign, r.Criteria).Select(e => new Finding(e.Criterion.Name, e.Post, e.Network, e.Criterion != Hashtags, false, e.Criterion == Hashtags ? "Three hashtags." : "Fine."))];

	private static IReadOnlyList<Finding> HonestBlocks(JudgeRequest r) =>
		[.. Criteria.Expected(r.Campaign, r.Criteria).Select(e => new Finding(e.Criterion.Name, e.Post, e.Network, e.Criterion != Honest, false, e.Criterion == Honest ? "Says 2x faster; no link shows it." : "Fine."))];

	[Test]
	public async Task The_proposal_shows_each_criterion_and_why_one_failed()
	{
		var (factory, browser) = await JudgedAsync(HashtagsWarn);
		using var owned = factory;

		var page = await browser.GetStringAsync("/Campaigns/Proposal?number=1");

		page.Should().Contain("<span class=\"badge pass\">passes</span>").And.Contain("<strong>honest</strong>")
			.And.Contain("<span class=\"badge warn\">warns</span>").And.Contain("Three hashtags.").And.Contain("stub-judge");
	}

	[Test]
	public async Task Approving_what_the_judge_blocks_is_refused_on_the_page_with_the_reason()
	{
		var (factory, browser) = await JudgedAsync(HonestBlocks);
		using var owned = factory;

		var token = await BraggarrFactory.FormTokenAsync(browser, "/Campaigns/Proposal?number=1");
		var response = await browser.PostAsync("/Campaigns/Proposal?handler=Approve&number=1", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		(await response.Content.ReadAsStringAsync()).Should().Contain("The judge blocks it").And.Contain("Says 2x faster; no link shows it.");
		factory.Reviews.Merged.Should().BeEmpty();
	}

	[Test]
	public async Task An_agent_reads_the_verdict_through_the_api()
	{
		var (factory, _) = await JudgedAsync(HonestBlocks);
		using var owned = factory;

		var json = JsonDocument.Parse(await factory.Agent().GetStringAsync("/api/v1/campaigns/mermaider-2-0")).RootElement;

		var judgement = json.GetProperty("judgement");
		judgement.GetProperty("ofThisVersion").GetBoolean().Should().BeTrue();
		judgement.GetProperty("blocked").GetBoolean().Should().BeTrue();
		judgement.GetProperty("findings").EnumerateArray().Should().Contain(f => f.GetProperty("criterion").GetString() == "honest" && !f.GetProperty("passed").GetBoolean());
	}

	[Test]
	public async Task The_settings_list_the_criteria_and_keep_the_judges_own_choices()
	{
		var (factory, browser) = await JudgedAsync();
		using var owned = factory;
		factory.Services.GetRequiredService<Braggarr.Adapters.Config.SecretStore>().Set(Braggarr.Adapters.Claude.ClaudeOptions.ApiKeySecret, "sk-ant-test");

		var page = await browser.GetStringAsync("/Settings?section=judge");
		var token = await BraggarrFactory.FormTokenAsync(browser, "/Settings?section=judge");
		_ = await browser.PostAsync("/Settings?section=judge&handler=Judge", new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["__RequestVerificationToken"] = token, ["autoApprove"] = "true", ["delayHours"] = "6"
		}));
		// The judge's model and effort are with the rest of Claude's.
		_ = await browser.PostAsync("/Settings?section=claude&handler=Claude", new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["__RequestVerificationToken"] = token, ["model"] = "claude-opus-5-5", ["effort"] = "medium", ["judgeModel"] = "claude-sonnet-5-5", ["judgeEffort"] = "high"
		}));
		var refused = await browser.PostAsync("/Settings?section=claude&handler=Claude", new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["__RequestVerificationToken"] = token, ["model"] = "claude-opus-5-5", ["effort"] = "medium", ["judgeModel"] = "gpt-5"
		}));
		var tooLong = await browser.PostAsync("/Settings?section=judge&handler=Judge", new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["__RequestVerificationToken"] = token, ["autoApprove"] = "true", ["delayHours"] = "1000"
		}));
		var claude = await browser.GetStringAsync("/Settings?section=drafting");

		page.Should().Contain("<strong>honest</strong>").And.Contain("Claims nothing the links do not show.").And.Contain("<span class=\"badge warn\">warns</span>");
		var s = factory.Settings.Current;
		(s.JudgeModel, s.JudgeEffort, s.JudgeAutoApprove, s.JudgeDelayHours).Should().Be(("claude-sonnet-5-5", "high", true, 6));
		(await refused.Content.ReadAsStringAsync()).Should().Contain("Choose one of the listed models");
		(await tooLong.Content.ReadAsStringAsync()).Should().Contain("between 0 hours and two weeks");
		claude.Should().Contain("<h3>Judging</h3>", "an old link to drafting lands on Claude's settings");
		factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<JudgingOptions>>().CurrentValue.Delay.Should().Be(TimeSpan.FromHours(6));
	}
}
