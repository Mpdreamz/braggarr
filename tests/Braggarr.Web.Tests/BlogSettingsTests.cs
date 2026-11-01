using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Braggarr.Web.Tests;

/// <summary>Connecting a blog in Settings: its site's repository, read through Braggarr's GitHub App.</summary>
public class BlogSettingsTests
{
	private const string Config = "title: The owner's blog\nurl: https://blog.example.com/\nauthor: { name: Owner }\n";

	private static async Task<string> ConnectAsync(BraggarrFactory factory, string repository)
	{
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);
		var token = await BraggarrFactory.FormTokenAsync(browser, "/Settings?section=networks");
		var response = await browser.PostAsync("/Settings?section=networks&handler=Blog", new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["__RequestVerificationToken"] = token,
			["repository"] = repository,
			["branch"] = "main",
			["folder"] = "posts"
		}));
		return await response.Content.ReadAsStringAsync();
	}

	[Test]
	public async Task A_site_braggarr_can_read_becomes_a_blog_account()
	{
		using var factory = new BraggarrFactory
		{
			GitHubAnswers = r => r.RequestUri!.AbsolutePath == "/repos/owner/blog/contents/braggarr.yml"
				? StubHttp.Json(JsonSerializer.Serialize(new Dictionary<string, string> { ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Config)) }))
				: null
		};

		var page = await ConnectAsync(factory, "https://github.com/owner/blog");

		page.Should().Contain("Connected the blog &quot;The owner&#x27;s blog&quot; (owner/blog). Its posts go to posts/ on main.");
		factory.Services.GetRequiredService<Braggarr.Adapters.Social.AccountStore>().On(Braggarr.Domain.Network.Blog).Should().Equal("owner/blog");
	}

	[Test]
	public async Task A_repository_that_is_not_a_braggarr_site_is_refused()
	{
		using var factory = new BraggarrFactory();

		var page = await ConnectAsync(factory, "owner/not-a-site");

		page.Should().Contain("owner/not-a-site has no braggarr.yml on main");
		factory.Services.GetRequiredService<Braggarr.Adapters.Social.AccountStore>().On(Braggarr.Domain.Network.Blog).Should().BeEmpty();
	}
}
