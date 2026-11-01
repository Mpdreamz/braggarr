using AwesomeAssertions;
using Braggarr.Adapters.Social;
using Braggarr.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Braggarr.Web.Tests;

/// <summary>Settings → Profiles: what each account misses, Claude's suggestion, applying it or copying it.</summary>
public class ProfilesPageTests
{
	private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields) =>
		new(fields.Select(f => KeyValuePair.Create(f.Key, f.Value)).Prepend(KeyValuePair.Create("__RequestVerificationToken", token)));

	[Test]
	public async Task The_owner_sees_what_is_missing_asks_claude_and_applies_a_field_or_copies_it()
	{
		using var factory = new BraggarrFactory();
		var accounts = factory.Services.GetRequiredService<AccountStore>();
		accounts.Set(Network.Bluesky, "me.bsky.social", new BlueskyCredentials("me.bsky.social", "p"));
		accounts.Set(Network.LinkedIn, "Martijn", new LinkedInCredentials("t", "urn:li:person:a", "Martijn", factory.Time.GetUtcNow().AddDays(30)));
		factory.Profiles.Shown[(Network.Bluesky, "me.bsky.social")] = new(Network.Bluesky, "me.bsky.social", "Martijn", null, null, true, false);
		factory.Profiles.Shown[(Network.LinkedIn, "Martijn")] = new(Network.LinkedIn, "Martijn", "Martijn", null, null, true, null);
		factory.Repository.Briefs.Add(new ProjectBrief("heed", "A Mac app for running coding agents.", "Taps you when an agent needs you.", "")
		{
			Links = [new BriefLink("Website", "https://heed.build")]
		});
		var browser = factory.Browser();
		_ = await BraggarrFactory.SignInAsync(browser);

		var page = await browser.GetStringAsync("/Settings/Profiles");
		var token = await BraggarrFactory.FormTokenAsync(browser, "/Settings/Profiles");
		var suggested = await (await browser.PostAsync("/Settings/Profiles?handler=Suggest", Form(token, ("network", "Bluesky"), ("account", "me.bsky.social")))).Content.ReadAsStringAsync();
		var linkedin = await (await browser.PostAsync("/Settings/Profiles?handler=Suggest", Form(token, ("network", "LinkedIn"), ("account", "Martijn")))).Content.ReadAsStringAsync();
		var applied = await (await browser.PostAsync("/Settings/Profiles?handler=Apply",
			Form(token, ("network", "Bluesky"), ("account", "me.bsky.social"), ("field", "Bio"), ("value", "I make heed. heed.build")))).Content.ReadAsStringAsync();

		page.Should().Contain("<strong>bio</strong> <span class=\"quiet small\">No bio").And.Contain("Suggest with Claude");
		suggested.Should().Contain("I make heed.").And.Contain("Apply on Bluesky").And.NotContain("data-copy=\"#bluesky-me-bsky-social-bio\"");
		linkedin.Should().Contain("data-copy=\"#linkedin-Martijn-bio\"").And.NotContain("Apply on LinkedIn");
		applied.Should().Contain("Changed the bio of me.bsky.social on Bluesky.");
		factory.Profiles.Applied.Should().ContainSingle().Which.Should().Be((Network.Bluesky, "me.bsky.social", new Braggarr.Services.ProfileChange(Bio: "I make heed. heed.build")));
		(await factory.Agent().GetAsync("/Settings/Profiles")).StatusCode.Should().NotBe(System.Net.HttpStatusCode.OK);
	}
}
