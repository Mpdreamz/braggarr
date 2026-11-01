using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Braggarr.Adapters.Git;
using Microsoft.Extensions.Options;

namespace Braggarr.Adapters.GitHub;

public sealed class GitHubException(string message, HttpStatusCode? status = null) : Exception(message)
{
	public HttpStatusCode? Status { get; } = status;
}

/// <summary>The few GitHub REST calls Braggarr makes on the posts repo, authenticated as the app's installation.</summary>
public sealed class GitHubClient(HttpClient http, IGitHubToken token, IOptionsMonitor<GitHubAppOptions> app, IOptionsMonitor<PostsRepositoryOptions> repo)
{
	/// <summary>
	/// What GitHub's JSON is read and written as: a type in <see cref="GitHubJson"/>, generated, so no reflection. A type missing
	/// there fails the first call that uses it, in the tests.
	/// </summary>
	internal static JsonTypeInfo<T> Info<T>() =>
		(JsonTypeInfo<T>?)GitHubJson.Default.GetTypeInfo(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T)} is not in GitHubJson.");

	private static JsonTypeInfo Info(object body) =>
		GitHubJson.Default.GetTypeInfo(body.GetType()) ?? throw new InvalidOperationException($"{body.GetType()} is not in GitHubJson.");

	public string Repository => repo.CurrentValue.Repository;

	internal static void AddHeaders(HttpRequestMessage request)
	{
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
		request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Braggarr", "1.0"));
	}

	public Task<T> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, ct);

	public Task<T> PostAsync<T>(string path, object body, CancellationToken ct) => SendAsync<T>(HttpMethod.Post, path, body, ct);

	public Task<T> PatchAsync<T>(string path, object body, CancellationToken ct) => SendAsync<T>(HttpMethod.Patch, path, body, ct);

	public Task<T> PutAsync<T>(string path, object body, CancellationToken ct) => SendAsync<T>(HttpMethod.Put, path, body, ct);

	public async Task DeleteAsync(string path, CancellationToken ct)
	{
		using var response = await RawAsync(HttpMethod.Delete, path, null, ct);
		if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
		{
			throw await ProblemAsync(response, path, ct);
		}
	}

	/// <summary>A path under the posts repo: <c>Repo("pulls")</c> is <c>repos/owner/name/pulls</c>.</summary>
	public string Repo(string path) => $"repos/{Repository}/{path}";

	private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
	{
		using var response = await RawAsync(method, path, body, ct);
		if (!response.IsSuccessStatusCode)
		{
			throw await ProblemAsync(response, path, ct);
		}
		return (await response.Content.ReadFromJsonAsync(Info<T>(), ct))!;
	}

	private async Task<HttpResponseMessage> RawAsync(HttpMethod method, string path, object? body, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(method, new Uri(new Uri(app.CurrentValue.ApiBase.TrimEnd('/') + "/"), path));
		AddHeaders(request);
		if (await token.GetAsync(ct) is { } t)
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
		}
		if (body is not null)
		{
			request.Content = JsonContent.Create(body, Info(body));
		}
		return await http.SendAsync(request, ct);
	}

	private static async Task<GitHubException> ProblemAsync(HttpResponseMessage response, string path, CancellationToken ct) =>
		new($"GitHub answered {(int)response.StatusCode} for {path}: {await response.Content.ReadAsStringAsync(ct)}", response.StatusCode);
}

/// <summary>Every type Braggarr sends to or reads from GitHub, in GitHub's snake_case.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubAppToken.Installation))]
[JsonSerializable(typeof(GitHubAppToken.AccessToken))]
[JsonSerializable(typeof(GitHubAppToken.JwtPayload))]
[JsonSerializable(typeof(GitHubReviews.Ref))]
[JsonSerializable(typeof(GitHubReviews.Commit))]
[JsonSerializable(typeof(GitHubReviews.ShaRef))]
[JsonSerializable(typeof(GitHubReviews.Pull))]
[JsonSerializable(typeof(GitHubReviews.Pull[]))]
[JsonSerializable(typeof(GitHubReviews.MergeResult))]
[JsonSerializable(typeof(GitHubReviews.Comment), TypeInfoPropertyName = "ReviewComment")]
[JsonSerializable(typeof(GitHubReviews.TreeListing))]
[JsonSerializable(typeof(GitHubReviews.Blob))]
[JsonSerializable(typeof(GitHubReviews.NewTree))]
[JsonSerializable(typeof(GitHubReviews.TreeChange))]
[JsonSerializable(typeof(GitHubReviews.NewBlob))]
[JsonSerializable(typeof(GitHubReviews.NewCommit))]
[JsonSerializable(typeof(GitHubReviews.NewRef))]
[JsonSerializable(typeof(GitHubReviews.MoveRef))]
[JsonSerializable(typeof(GitHubReviews.NewPull))]
[JsonSerializable(typeof(GitHubReviews.Merge))]
[JsonSerializable(typeof(GitHubReviews.NewComment), TypeInfoPropertyName = "ReviewNewComment")]
[JsonSerializable(typeof(GitHubReviews.PullState))]
[JsonSerializable(typeof(GitHubManualPosts.Issue))]
[JsonSerializable(typeof(GitHubManualPosts.Comment), TypeInfoPropertyName = "IssueComment")]
[JsonSerializable(typeof(GitHubManualPosts.NewIssue))]
[JsonSerializable(typeof(GitHubManualPosts.NewComment), TypeInfoPropertyName = "IssueNewComment")]
[JsonSerializable(typeof(GitHubManualPosts.IssueState))]
[JsonSerializable(typeof(GitHubReleases.Item[]))]
[JsonSerializable(typeof(GitHubRepositories.Repo))]
[JsonSerializable(typeof(Blog.BlogPublisher.Contents))]
[JsonSerializable(typeof(GitHubRepositories.Readme))]
[JsonSerializable(typeof(GitHubProjectFacts.Repo), TypeInfoPropertyName = "FactsRepo")]
[JsonSerializable(typeof(GitHubProjectFacts.GraphQuery))]
[JsonSerializable(typeof(GitHubProjectFacts.GraphAnswer))]
[JsonSerializable(typeof(Blog.GitHubAboutSites.ForceRef))]
internal sealed partial class GitHubJson : JsonSerializerContext;
