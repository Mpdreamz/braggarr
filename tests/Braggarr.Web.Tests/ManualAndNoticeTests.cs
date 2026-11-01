using AwesomeAssertions;
using Braggarr.Adapters;
using Braggarr.Adapters.GitHub;
using Braggarr.Domain;
using Braggarr.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Braggarr.Web.Tests;

public class ManualAndNoticeTests
{
	private static readonly Campaign Curb = new(
		"curb-1-0", "Curb 1.0", "curb", CampaignStatus.Approved, [Network.Reddit, Network.HackerNews], ["https://github.com/nullean/curb"], "", "owner:me", []
	);

	[Test]
	public void Hacker_news_gets_one_submit_link_with_the_title_and_the_url_from_the_text()
	{
		var links = GitHubManualPosts.SubmitLinks(Curb, new Variant(Network.HackerNews, "Show HN: Curb, a C# formatter\nhttps://curb.example/start"));

		links.Should().Equal(("Submit to Hacker News", "https://news.ycombinator.com/submitlink?u=https%3A%2F%2Fcurb.example%2Fstart&t=Show%20HN%3A%20Curb%2C%20a%20C%23%20formatter"));
	}

	[Test]
	public void Reddit_gets_one_link_per_subreddit_named_and_falls_back_to_the_campaigns_link()
	{
		var links = GitHubManualPosts.SubmitLinks(Curb, new Variant(Network.Reddit, "I made a C# formatter that keeps diffs small\n\nBody.\n\nr/dotnet r/csharp"));

		links.Select(l => l.Label).Should().Equal("Submit to r/dotnet", "Submit to r/csharp");
		links[0].Url.Should().StartWith("https://www.reddit.com/r/dotnet/submit?url=https%3A%2F%2Fgithub.com%2Fnullean%2Fcurb&title=I%20made");
	}

	[Test]
	public async Task Ntfy_gets_the_title_the_priority_the_tap_and_at_most_three_buttons()
	{
		var ntfy = new StubHttp((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
		var notifier = new NtfyNotifier(new HttpClient(ntfy), new Monitor(new NtfyOptions { Server = "https://ntfy.example", Topic = "braggarr-long-random" }), NullLogger<NtfyNotifier>.Instance);

		await notifier.NotifyAsync(
			new Notice(NoticeLevel.Problem, "Curb 1.0: Bluesky needs you", "No answer.", "https://braggarr/Campaigns")
			{
				Actions = [new("Submit, now", "https://a"), new("Issue", "https://b"), new("Third", "https://c"), new("Fourth", "https://d")]
			},
			CancellationToken.None
		);

		var (request, body) = ntfy.Requests.Single();
		request.RequestUri!.ToString().Should().Be("https://ntfy.example/braggarr-long-random");
		body.Should().Be("No answer.");
		request.Headers.GetValues("Title").Single().Should().Be("Curb 1.0: Bluesky needs you");
		request.Headers.GetValues("Priority").Single().Should().Be("high");
		request.Headers.GetValues("Click").Single().Should().Be("https://braggarr/Campaigns");
		request.Headers.GetValues("Actions").Single().Should().Be("view, Submit now, https://a; view, Issue, https://b; view, Third, https://c");
	}

	[Test]
	public async Task Without_a_topic_a_notice_only_goes_to_the_log()
	{
		var ntfy = new StubHttp((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
		var notifier = new NtfyNotifier(new HttpClient(ntfy), new Monitor(new NtfyOptions()), NullLogger<NtfyNotifier>.Instance);

		await notifier.NotifyAsync(new Notice(NoticeLevel.Info, "t", "m"), CancellationToken.None);

		ntfy.Requests.Should().BeEmpty();
	}

	private sealed class Monitor(NtfyOptions value) : IOptionsMonitor<NtfyOptions>
	{
		public NtfyOptions CurrentValue => value;

		public NtfyOptions Get(string? name) => value;

		public IDisposable? OnChange(Action<NtfyOptions, string?> listener) => null;
	}
}
