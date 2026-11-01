using AwesomeAssertions;
using Braggarr.Adapters.Git;
using Braggarr.Adapters.GitHub;
using Braggarr.Services;
using Microsoft.Extensions.Options;

namespace Braggarr.Web.Tests;

public class GitHubRepositoriesTests
{
	private sealed class NoReleases : IReleases
	{
		public Task<IReadOnlyList<Release>> LatestAsync(string repository, CancellationToken ct) => Task.FromResult<IReadOnlyList<Release>>([]);
	}

	private sealed class Token : IGitHubToken
	{
		public Task<string?> GetAsync(CancellationToken ct) => Task.FromResult<string?>("t");
	}

	private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
	{
		public T CurrentValue => value;

		public T Get(string? name) => value;

		public IDisposable? OnChange(Action<T, string?> listener) => null;
	}

	[Test]
	public async Task The_readme_is_decoded_from_githubs_wrapped_base64_and_a_missing_one_is_empty()
	{
		var readme = Convert.ToBase64String("# Curb\nFormats C#."u8.ToArray());
		var wrapped = readme[..8] + "\\n" + readme[8..];
		var api = new StubHttp((request, _) => request.RequestUri!.AbsolutePath switch
		{
			"/repos/nullean/curb" => StubHttp.Json("""{"description":"A formatter.","homepage":"","topics":["csharp"]}"""),
			"/repos/nullean/curb/readme" => StubHttp.Json($$"""{"content":"{{wrapped}}"}"""),
			"/repos/nullean/bare" => StubHttp.Json("""{"description":null,"homepage":null,"topics":null}"""),
			_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
		});
		var github = new GitHubClient(new HttpClient(api), new Token(), new Monitor<GitHubAppOptions>(new()), new Monitor<PostsRepositoryOptions>(new()));
		var repositories = new GitHubRepositories(github, new NoReleases());

		var curb = await repositories.ReadAsync("nullean/curb", "Curb", CancellationToken.None);
		var bare = await repositories.ReadAsync("nullean/bare", "Bare", CancellationToken.None);

		curb.Readme.Should().Be("# Curb\nFormats C#.");
		curb.Homepage.Should().BeNull();
		curb.Topics.Should().Equal("csharp");
		bare.Readme.Should().BeEmpty();
		bare.Description.Should().BeEmpty();
	}
}
