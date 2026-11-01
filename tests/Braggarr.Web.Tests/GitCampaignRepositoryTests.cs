using AwesomeAssertions;
using Braggarr.Adapters.Git;
using Braggarr.Adapters.Posts;
using Braggarr.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Braggarr.Web.Tests;

/// <summary>Against a real git: a bare repo on disk as "GitHub", the owner's own clone, and Braggarr's clone.</summary>
public sealed class GitCampaignRepositoryTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("braggarr-git-").FullName;

	private string Remote => Path.Combine(_root, "remote.git");

	private string Owner => Path.Combine(_root, "owner");

	public GitCampaignRepositoryTests()
	{
		GitFixture.Git(_root, "init", "--quiet", "--bare", "--initial-branch=main", Remote);
		GitFixture.Git(_root, "clone", "--quiet", Remote, Owner);
		var folder = Directory.CreateDirectory(Path.Combine(Owner, "campaigns", "curb-1-0")).FullName;
		foreach (var (name, content) in PostsFormat.WriteCampaign(Campaign()))
		{
			File.WriteAllText(Path.Combine(folder, name), content);
		}
		GitFixture.Commit(Owner, "Approve curb-1-0");
	}

	private static Campaign Campaign() =>
		new(
			"curb-1-0", "Curb 1.0", "curb", CampaignStatus.Approved, [Network.Bluesky, Network.Mastodon], [], "Brief.", "owner:mpdreamz",
			[new Post("01-launch", new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)), [new Variant(Network.Bluesky, "Out."), new Variant(Network.Mastodon, "Out.")])]
		);

	private GitCampaignRepository Braggarr(string name = "braggarr") =>
		new(
			new GitFixture.FixedOptions(new PostsRepositoryOptions { Repository = "me/posts", Directory = Path.Combine(_root, name), CloneUrl = Remote }),
			new GitFixture.NoToken(),
			// The real disk, as in production: scoped to where the clones live, git's own folder allowed.
			global::Braggarr.Adapters.ConfigFileSystem.Create(_root),
			NullLogger<GitCampaignRepository>.Instance
		);

	[Test]
	public async Task Reads_approved_campaigns_from_main()
	{
		using var repository = Braggarr();

		var state = await repository.PullAsync(CancellationToken.None);

		state.Campaigns.Should().ContainSingle().Which.Should().Be(Campaign());
		state.Deliveries.Should().BeEmpty();
	}

	[Test]
	public async Task A_recorded_delivery_survives_a_restart_and_reaches_github()
	{
		var key = new DeliveryKey("curb-1-0", "01-launch", Network.Bluesky);
		var sending = Delivery.New(key).Begin(new DateTimeOffset(2026, 11, 2, 9, 0, 1, TimeSpan.Zero));
		using (var before = Braggarr())
		{
			_ = await before.PullAsync(CancellationToken.None);
			await before.RecordAsync(sending, CancellationToken.None);
		}

		// A new process, same clone: the record was committed, nothing was pushed yet.
		using var after = Braggarr();
		(await after.PullAsync(CancellationToken.None)).Deliveries.Should().Equal(sending);

		await after.PushAsync(CancellationToken.None);
		GitFixture.Git(Owner, "pull", "--quiet");
		File.ReadAllText(Path.Combine(Owner, "campaigns", "curb-1-0", "deliveries.yml")).Should().Contain("state: sending");
	}

	[Test]
	public async Task The_owners_edits_and_braggarrs_records_never_conflict()
	{
		using var repository = Braggarr();
		_ = await repository.PullAsync(CancellationToken.None);
		await repository.RecordAsync(Delivery.New(new DeliveryKey("curb-1-0", "01-launch", Network.Bluesky)).Begin(DateTimeOffset.UnixEpoch), CancellationToken.None);

		// Meanwhile the owner rewrites the Mastodon text and pushes first.
		var post = Path.Combine(Owner, "campaigns", "curb-1-0", "01-launch.md");
		File.WriteAllText(post, File.ReadAllText(post).Replace("## mastodon\n\nOut.", "## mastodon\n\nOut now, with a longer note.", StringComparison.Ordinal));
		GitFixture.Commit(Owner, "Reword the Mastodon post");

		await repository.PushAsync(CancellationToken.None);

		var state = await repository.PullAsync(CancellationToken.None);
		state.Campaigns.Single().Posts.Single().For(Network.Mastodon)!.Text.Should().Be("Out now, with a longer note.");
		state.Deliveries.Should().ContainSingle().Which.State.Should().Be(DeliveryState.Sending);
		GitFixture.Git(Owner, "pull", "--quiet");
		File.Exists(Path.Combine(Owner, "campaigns", "curb-1-0", "deliveries.yml")).Should().BeTrue();
	}

	[Test]
	public async Task Holding_is_a_commit_on_main()
	{
		using var repository = Braggarr();
		_ = await repository.PullAsync(CancellationToken.None);

		await repository.SetHeldAsync("curb-1-0", held: true, Actor.Owner("mpdreamz"), CancellationToken.None);

		GitFixture.Git(Owner, "pull", "--quiet");
		File.ReadAllText(Path.Combine(Owner, "campaigns", "curb-1-0", "campaign.md")).Should().Contain("held: true");
		(await repository.PullAsync(CancellationToken.None)).Campaigns.Single().Status.Should().Be(CampaignStatus.Held);
	}

	[Test]
	public async Task Images_are_read_from_the_campaigns_own_folder_and_nowhere_else()
	{
		var media = Directory.CreateDirectory(Path.Combine(Owner, "campaigns", "curb-1-0", "media")).FullName;
		File.WriteAllBytes(Path.Combine(media, "card.png"), [1, 2, 3]);
		File.WriteAllText(Path.Combine(Owner, "secret.txt"), "not for posting");
		GitFixture.Commit(Owner, "Add an image");
		using var repository = Braggarr();
		_ = await repository.PullAsync(CancellationToken.None);

		(await repository.ReadMediaAsync("curb-1-0", "media/card.png", CancellationToken.None)).Should().Equal(1, 2, 3);
		(await repository.ReadMediaAsync("curb-1-0", "../../secret.txt", CancellationToken.None)).Should().BeNull();
		(await repository.ReadMediaAsync("curb-1-0", "media/missing.png", CancellationToken.None)).Should().BeNull();
	}

	/// <summary>
	/// Git keeps symbolic links. One committed as an image, pointing at the secrets next to the clone, would be uploaded to a
	/// network as a post's picture. The campaign folder's rules and the scoped file system both refuse to follow it.
	/// </summary>
	[Test]
	public async Task An_image_committed_as_a_link_is_never_read()
	{
		File.WriteAllText(Path.Combine(_root, "secrets.json"), "the owner's credentials");
		var media = Directory.CreateDirectory(Path.Combine(Owner, "campaigns", "curb-1-0", "media")).FullName;
		File.CreateSymbolicLink(Path.Combine(media, "logo.png"), "../../../../secrets.json");
		Directory.CreateDirectory(Path.Combine(Owner, "campaigns", "curb-1-0", ".drafts"));
		File.WriteAllBytes(Path.Combine(Owner, "campaigns", "curb-1-0", ".drafts", "card.png"), [4, 5, 6]);
		GitFixture.Commit(Owner, "Add a linked logo and a hidden draft");
		using var repository = Braggarr();
		_ = await repository.PullAsync(CancellationToken.None);
		File.Exists(Path.Combine(_root, "braggarr", "campaigns", "curb-1-0", "media", "logo.png")).Should().BeTrue("git checked the link out");

		(await repository.ReadMediaAsync("curb-1-0", "media/logo.png", CancellationToken.None)).Should().BeNull();
		(await repository.ReadMediaAsync("curb-1-0", ".drafts/card.png", CancellationToken.None)).Should().BeNull();
	}

	[Test]
	public async Task A_post_is_never_written_outside_its_campaigns_folder()
	{
		using var repository = Braggarr();
		_ = await repository.PullAsync(CancellationToken.None);
		var post = new Post("01-launch", DateTimeOffset.UnixEpoch, [new Variant(Network.Bluesky, "Out.")]);

		var writing = () => repository.UpdatePostAsync("curb-1-0", post with { Id = "../heed-0-20/01-launch" }, Actor.Owner("mpdreamz"), CancellationToken.None);

		_ = await writing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is outside*");
		Directory.Exists(Path.Combine(_root, "braggarr", "campaigns", "heed-0-20")).Should().BeFalse();
	}

	public void Dispose() => Directory.Delete(_root, recursive: true);
}
