using AwesomeAssertions;
using Braggarr.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

/// <summary>Reading, suggesting and changing the owner's profiles.</summary>
public class ProfileServiceTests
{
	private static readonly Actor Owner = Actor.Owner("mpdreamz");

	private static readonly ProjectBrief Heed = new("heed", "A Mac app for running coding agents.", "Taps you only when an agent needs you.", "")
	{
		Links = [new BriefLink("Website", "https://heed.build")]
	};

	private static readonly ProjectBrief Curb = new("curb", "A C# formatter.", "Reads all of your .editorconfig.", "")
	{
		Links = [new BriefLink("Website", "https://curb.nullean.net")]
	};

	private static (ProfileService Service, FakeProfiles Profiles, FakeAccounts Accounts, FakeDrafter Drafter, FakeUsage Usage, FakeRepository Repository) Create(bool claude = true)
	{
		var profiles = new FakeProfiles();
		var accounts = new FakeAccounts();
		accounts.Accounts[Network.Bluesky] = ["me.bsky.social", "nullean.bsky.social"];
		accounts.Accounts[Network.LinkedIn] = ["Martijn"];
		profiles.Shown[(Network.Bluesky, "me.bsky.social")] = new(Network.Bluesky, "me.bsky.social", "Martijn", null, null, true, false);
		profiles.Shown[(Network.Bluesky, "nullean.bsky.social")] = new(Network.Bluesky, "nullean.bsky.social", null, null, null, false, false);
		profiles.Shown[(Network.LinkedIn, "Martijn")] = new(Network.LinkedIn, "Martijn", "Martijn", null, null, true, null);
		var drafter = new FakeDrafter();
		var usage = new FakeUsage();
		var repository = new FakeRepository { Voice = "Plain." };
		repository.Briefs.AddRange([Heed, Curb]);
		var service = new ProfileService(profiles, accounts, repository, new FakeDrafters(claude ? drafter : null), usage,
			new ManualTimeProvider(SchedulerWorld.Start), NullLogger<ProfileService>.Instance);
		return (service, profiles, accounts, drafter, usage, repository);
	}

	[Test]
	public async Task Every_connected_account_is_read_and_checked_and_one_that_cannot_be_read_says_why()
	{
		var (service, _, accounts, _, _, _) = Create();
		accounts.Accounts[Network.X] = ["gone"];

		var views = await service.AllAsync(CancellationToken.None);

		views.Select(v => (v.Network, v.Account)).Should().Contain([(Network.Bluesky, "me.bsky.social"), (Network.Bluesky, "nullean.bsky.social"), (Network.LinkedIn, "Martijn"), (Network.X, "gone")]);
		views.Single(v => v.Account == "nullean.bsky.social").Findings.Should().Contain(f => f.Field == ProfileField.DisplayName && !f.Ok);
		views.Single(v => v.Account == "gone").Problem.Should().Contain("would not say");
	}

	[Test]
	public async Task Claude_writes_from_the_briefs_of_the_projects_the_account_posts_for_and_nothing_changes()
	{
		var (service, profiles, accounts, drafter, usage, _) = Create();
		accounts.ProjectDefaults[(Network.Bluesky, "curb")] = "nullean.bsky.social";
		drafter.Profile = new ProfileSuggestion("Nullean", "Fast .NET tools.", "https://curb.nullean.net", "It had no bio.");

		var outcome = await service.SuggestAsync(Network.Bluesky, "nullean.bsky.social", CancellationToken.None);
		_ = await service.SuggestAsync(Network.Bluesky, "me.bsky.social", CancellationToken.None);

		outcome.Should().BeOfType<Outcome<ProfileSuggestion>.Done>();
		drafter.ProfilesAsked[0].About.Should().Equal(Curb);
		drafter.ProfilesAsked[0].Voice.Should().Be("Plain.");
		drafter.ProfilesAsked[1].About.Should().Equal([Heed], "curb goes to the other account; me gets the rest");
		usage.Calls.Select(c => c.Purpose).Should().Equal("profile bluesky nullean.bsky.social", "profile bluesky me.bsky.social");
		profiles.Applied.Should().BeEmpty();
	}

	[Test]
	public async Task The_owner_applies_one_field_and_only_that_field_is_sent()
	{
		var (service, profiles, _, _, _, _) = Create();

		var outcome = await service.ApplyAsync(Network.Bluesky, "me.bsky.social", ProfileField.Bio, "  I make heed and the Nullean .NET tools. heed.build  ", Owner, CancellationToken.None);

		outcome.Should().BeOfType<Outcome<AccountProfile>.Done>().Which.Value.Bio.Should().Be("I make heed and the Nullean .NET tools. heed.build");
		profiles.Applied.Should().ContainSingle().Which.Change.Should().Be(new ProfileChange(Bio: "I make heed and the Nullean .NET tools. heed.build"));
	}

	[Test]
	public async Task What_cannot_be_applied_is_refused_with_why()
	{
		var (service, profiles, _, _, _, _) = Create();

		var agent = await service.ApplyAsync(Network.Bluesky, "me.bsky.social", ProfileField.Bio, "Hi.", Actor.Agent("bot"), CancellationToken.None);
		var linkedin = await service.ApplyAsync(Network.LinkedIn, "Martijn", ProfileField.Bio, "Hi.", Owner, CancellationToken.None);
		var tooLong = await service.ApplyAsync(Network.Bluesky, "me.bsky.social", ProfileField.Bio, new string('a', 257), Owner, CancellationToken.None);
		var notMine = await service.ApplyAsync(Network.Bluesky, "someone.bsky.social", ProfileField.Bio, "Hi.", Owner, CancellationToken.None);
		var picture = await service.ApplyAsync(Network.Bluesky, "me.bsky.social", ProfileField.Avatar, "x", Owner, CancellationToken.None);
		profiles.Refuses = "Bluesky refused (401).";
		var refused = await service.ApplyAsync(Network.Bluesky, "me.bsky.social", ProfileField.DisplayName, "Martijn", Owner, CancellationToken.None);

		agent.Should().BeOfType<Outcome<AccountProfile>.Refused>();
		linkedin.Should().BeOfType<Outcome<AccountProfile>.Refused>().Which.Reasons.Single().Should().Contain("copy it");
		tooLong.Should().BeOfType<Outcome<AccountProfile>.Refused>().Which.Reasons.Single().Should().Contain("256");
		notMine.Should().BeOfType<Outcome<AccountProfile>.Refused>();
		picture.Should().BeOfType<Outcome<AccountProfile>.Refused>();
		refused.Should().BeOfType<Outcome<AccountProfile>.Refused>().Which.Reasons.Single().Should().Be("Bluesky refused (401).");
		profiles.Applied.Should().BeEmpty();
	}

	[Test]
	public async Task Without_claude_there_is_no_suggestion()
	{
		var (service, _, _, _, _, _) = Create(claude: false);

		(await service.SuggestAsync(Network.Bluesky, "me.bsky.social", CancellationToken.None)).Should().BeOfType<Outcome<ProfileSuggestion>.Refused>();
		service.CanSuggest.Should().BeFalse();
	}
}
