using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;
using Braggarr.Adapters;
using CsCheck;
using Nullean.Entities.CsCheck;
using Nullean.Entities.CsCheck.FileSystem;
using Nullean.ScopedFileSystem;

namespace Braggarr.Web.Tests;

/// <summary>
/// The rules on what Braggarr reads and writes on disk, attacked with generated file system worlds: a missing file, a link, a
/// file below a linked or hidden folder, one outside the root, one reached through <c>..</c>, the wrong extension.
/// </summary>
public class FileRulesTests
{
	private static CampaignFolder ValidFolder()
	{
		var fs = new MockFileSystem();
		fs.AddFile("/config/posts/campaigns/curb-1-0/01-launch.md", new MockFileData("---\nat: 2026-11-02T10:00:00+01:00\n---\n"));
		fs.AddFile("/config/posts/campaigns/curb-1-0/media/card.png", new MockFileData([0x89, 0x50, 0x4E, 0x47]));
		return CampaignFolder.Of(fs, "/config/posts/campaigns/curb-1-0", "01-launch.md", "media/card.png");
	}

	private static VideoLibrary ValidLibrary()
	{
		var fs = new MockFileSystem();
		var name = $"/config/media/sha256-{new string('a', 64)}.mp4";
		fs.AddFile(name, new MockFileData(new byte[1024]));
		return new VideoLibrary(fs.DirectoryInfo.New("/config/media"), [fs.FileInfo.New(name)]);
	}

	/// <summary>One more file in the value's own world, made by <paramref name="make"/>: the counter-examples below change one thing each.</summary>
	private static IFileInfo Add(IDirectoryInfo root, string relative, Action<MockFileSystem, string> make)
	{
		var fs = (MockFileSystem)root.FileSystem;
		var path = fs.Path.GetFullPath(fs.Path.Combine(root.FullName, relative));
		// The base is one constant world, shared by axes that run at the same time, and a counter-example may be applied to it
		// more than once: add each file once.
		lock (fs)
		{
			if (!fs.FileExists(path) && !fs.Directory.Exists(path))
			{
				make(fs, path);
			}
		}
		return fs.FileInfo.New(path);
	}

	private static readonly Action<MockFileSystem, string> AFile = (fs, path) => fs.AddFile(path, new MockFileData("x"));

	public static IEnumerable<Func<AxisCase>> FolderAxes() =>
		AutoTest.For(CampaignFolderRules.Instance, Gen.Const(ValidFolder()))
			.Options(o => o.UseFileSystem())
			// Each breaks one rule and no other, in the campaign's own file system.
			.CounterExample(CampaignFolderRules.StayInTheCampaign, f => f with { Files = [.. f.Files, Add(f.Root, "../heed-0-20/01-launch.md", AFile)] })
			.CounterExample(
				CampaignFolderRules.NoLinks,
				f => f with { Files = [.. f.Files, Add(f.Root, "02-linked.md", (fs, path) => fs.File.CreateSymbolicLink(path, f.Files[0].FullName))] }
			)
			.CounterExample(CampaignFolderRules.NoHiddenFolders, f => f with { Files = [.. f.Files, Add(f.Root, ".drafts/03-hidden.md", AFile)] })
			.CounterExample(CampaignFolderRules.KnownKinds, f => f with { Files = [.. f.Files, Add(f.Root, "notes.txt", AFile)] })
			.AsDataSource();

	public static IEnumerable<Func<AxisCase>> LibraryAxes() =>
		AutoTest.For(VideoLibraryRules.Instance, Gen.Const(ValidLibrary()))
			.Options(o => o.UseFileSystem())
			.CounterExample(VideoLibraryRules.StayInTheLibrary, l => l with { Videos = [.. l.Videos, Add(l.Root, "../elsewhere/run.mp4", AFile)] })
			.CounterExample(
				VideoLibraryRules.NoLinks,
				l => l with { Videos = [.. l.Videos, Add(l.Root, "linked.mp4", (fs, path) => fs.File.CreateSymbolicLink(path, l.Videos[0].FullName))] }
			)
			.CounterExample(VideoLibraryRules.NoHiddenFolders, l => l with { Videos = [.. l.Videos, Add(l.Root, ".cache/run.mp4", AFile)] })
			.CounterExample(VideoLibraryRules.OnlyMp4, l => l with { Videos = [.. l.Videos, Add(l.Root, "run.mov", AFile)] })
			// Too large: one more video in the library, a byte past what an upload may weigh. The base world is left as it is.
			.CounterExample(
				VideoLibraryRules.NoLargerThanAnUpload,
				l => l with
				{
					Videos = [.. l.Videos, Add(l.Root, "large.mp4", (fs, path) => fs.AddFile(path, new MockFileData(new byte[Braggarr.Services.CampaignService.MaxVideoBytes + 1])))]
				}
			)
			.AsDataSource();

	[Test]
	[MethodDataSource(nameof(FolderAxes))]
	public Task The_campaign_folder_rules_hold_and_each_can_fire(AxisCase axis) => axis.RunAsync();

	[Test]
	[MethodDataSource(nameof(LibraryAxes))]
	public Task The_video_library_rules_hold_and_each_can_fire(AxisCase axis) => axis.RunAsync();

	[Test]
	public void Each_campaign_folder_rule_is_broken_by_some_generated_world()
	{
		var report = CampaignFolderRules.All.Probe(GenAuto.Of<CampaignFolder>(AutoMode.Mixed, o => o.UseFileSystem()), samples: 1500, throwOnContradiction: false);

		report.NeverViolated.Should().BeEmpty(report.ToString());
	}

	[Test]
	public void The_config_file_system_refuses_what_is_outside_config_hidden_or_linked()
	{
		var disk = new MockFileSystem();
		disk.AddFile("/config/posts/campaigns/curb-1-0/01-launch.md", new MockFileData("post"));
		disk.AddFile("/config/posts/.git/HEAD", new MockFileData("ref: refs/heads/main"));
		disk.AddFile("/etc/passwd", new MockFileData("root"));
		disk.AddFile("/config/.env", new MockFileData("SECRET=1"));
		_ = disk.Directory.CreateSymbolicLink("/config/posts/campaigns/escape", "/etc");
		var fs = ConfigFileSystem.Create("/config", disk);

		fs.File.ReadAllText("/config/posts/campaigns/curb-1-0/01-launch.md").Should().Be("post");
		fs.File.ReadAllText("/config/posts/.git/HEAD").Should().StartWith("ref:", "the posts clone's .git is the one hidden folder allowed");
		Refused(() => fs.File.ReadAllText("/config/posts/campaigns/curb-1-0/../../../../etc/passwd"));
		Refused(() => fs.File.WriteAllText("/etc/passwd", "owned"));
		Refused(() => fs.File.ReadAllText("/config/.env"));
		Refused(() => fs.File.ReadAllText("/config/posts/campaigns/escape/passwd"));
		fs.File.Exists("/etc/passwd").Should().BeFalse("outside the scope, a file does not exist rather than throw");

		static void Refused(Action act) => act.Should().Throw<ScopedFileSystemException>();
	}

	[Test]
	public void A_post_path_that_leaves_its_campaign_is_named_by_the_rule_that_refuses_it()
	{
		var fs = new MockFileSystem();
		fs.AddFile("/config/posts/campaigns/heed-0-20/01-launch.md", new MockFileData("approved"));

		var violations = CampaignFolderRules.All.Validate(CampaignFolder.Of(fs, "/config/posts/campaigns/curb-1-0", "../heed-0-20/01-launch.md"));

		violations.Select(v => v.ConstraintId).Should().Equal("stay-in-the-campaign");
		violations[0].Message.Should().Contain("/config/posts/campaigns/heed-0-20/01-launch.md");
	}
}
