using System.Text;
using System.Text.RegularExpressions;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Adapters.GitHub;

/// <summary>
/// Reddit and Hacker News posts as issues on the posts repo: the reviewed title and text, and a link that opens the network's
/// submit form already filled in. Closing the issue tells Braggarr it is done.
/// </summary>
public sealed partial class GitHubManualPosts(GitHubClient github) : IManualPosts
{
	public async Task<ManualPost> OpenAsync(Campaign campaign, Post post, Variant variant, CancellationToken ct)
	{
		var issue = await github.PostAsync<Issue>(
			github.Repo("issues"),
			new NewIssue($"Post to {Name(variant.Network)}: {Lengths.Title(variant.Text)}", Body(campaign, variant), ["braggarr", variant.Network.Slug()]),
			ct
		);
		return new ManualPost(issue.Number, issue.HtmlUrl, [.. SubmitLinks(campaign, variant).Select(l => new SubmitLink(l.Label, l.Url))]);
	}

	public async Task CloseAsync(int issue, CancellationToken ct)
	{
		_ = await github.PostAsync<Comment>(github.Repo($"issues/{issue}/comments"), new NewComment("Posted, said the owner in Braggarr."), ct);
		_ = await github.PatchAsync<Issue>(github.Repo($"issues/{issue}"), new IssueState("closed", "completed"), ct);
	}

	public async Task<IReadOnlySet<int>> ClosedAsync(IReadOnlyCollection<int> issues, CancellationToken ct)
	{
		var closed = new HashSet<int>();
		foreach (var number in issues)
		{
			if ((await github.GetAsync<Issue>(github.Repo($"issues/{number}"), ct)).State == "closed")
			{
				_ = closed.Add(number);
			}
		}
		return closed;
	}

	/// <summary>
	/// The links that open the network's submit form. For Reddit, lines like <c>r/dotnet</c> in the variant name the subreddits;
	/// the link to submit is the first URL in the variant, or else the campaign's first link.
	/// </summary>
	public static IReadOnlyList<(string Label, string Url)> SubmitLinks(Campaign campaign, Variant variant)
	{
		var title = Lengths.Title(variant.Text);
		var url = Url().Match(variant.Text) is { Success: true } m ? m.Value : campaign.Links.Count > 0 ? campaign.Links[0] : "";
		if (variant.Network == Network.HackerNews)
		{
			return [("Submit to Hacker News", $"https://news.ycombinator.com/submitlink?u={q(url)}&t={q(title)}")];
		}
		var subreddits = Subreddit().Matches(variant.Text).Select(s => s.Groups["name"].Value).Distinct().ToArray();
		return subreddits.Length == 0
			? [("Submit to Reddit", $"https://www.reddit.com/submit?url={q(url)}&title={q(title)}")]
			: [.. subreddits.Select(s => ($"Submit to r/{s}", $"https://www.reddit.com/r/{s}/submit?url={q(url)}&title={q(title)}"))];

		static string q(string value) => Uri.EscapeDataString(value);
	}

	private static string Body(Campaign campaign, Variant variant)
	{
		var body = new StringBuilder();
		_ = body.Append("It is time to post **").Append(campaign.Title).Append("** to ").Append(Name(variant.Network)).Append(".\n\n");
		foreach (var (label, url) in SubmitLinks(campaign, variant))
		{
			_ = body.Append("- [").Append(label).Append("](").Append(url).Append(")\n");
		}
		_ = body.Append("\nThe reviewed text:\n\n```\n").Append(variant.Text).Append("\n```\n\n");
		_ = body.Append("Stay around for the first hour to answer comments. Close this issue once it is posted.");
		return body.ToString();
	}

	private static string Name(Network network) => network == Network.HackerNews ? "Hacker News" : network.ToString();

	[GeneratedRegex(@"https?://\S+")]
	private static partial Regex Url();

	[GeneratedRegex(@"(?:^|\s)r/(?<name>[A-Za-z0-9_]{2,21})\b")]
	private static partial Regex Subreddit();

	internal sealed record Issue(int Number, string State, string HtmlUrl);

	internal sealed record Comment(long Id);

	internal sealed record NewIssue(string Title, string Body, IReadOnlyList<string> Labels);

	internal sealed record NewComment(string Body);

	internal sealed record IssueState(string State, string StateReason);
}
