using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;
using CsCheck;
using Nullean.Entities.CsCheck;
using Nullean.Entities.CsCheck.FileSystem;

namespace Braggarr.Blog.Tests;

/// <summary>What a build reads and writes on disk, attacked with generated file system worlds.</summary>
public class FileRulesTests
{
	private static SiteFolder ValidSource()
	{
		var fs = new MockFileSystem();
		fs.AddFile("/site/braggarr.yml", new MockFileData("title: s"));
		fs.AddFile("/site/posts/hello/index.md", new MockFileData("---\ntitle: Hello\n---\n"));
		return new SiteFolder(fs.DirectoryInfo.New("/site"), [fs.FileInfo.New("/site/braggarr.yml"), fs.FileInfo.New("/site/posts/hello/index.md")]);
	}

	private static SiteOutput ValidOutput()
	{
		var fs = new MockFileSystem();
		fs.AddDirectory("/out");
		return new SiteOutput(fs.DirectoryInfo.New("/out"), [fs.FileInfo.New("/out/index.html"), fs.FileInfo.New("/out/posts/hello/index.html")]);
	}

	/// <summary>One more file in the value's own world: the counter-examples below change one thing each.</summary>
	private static IFileInfo Add(IDirectoryInfo root, string relative, Action<MockFileSystem, string> make)
	{
		var fs = (MockFileSystem)root.FileSystem;
		var path = fs.Path.GetFullPath(fs.Path.Combine(root.FullName, relative));
		// The base is one world, shared by axes that run at the same time: add each file once.
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

	public static IEnumerable<Func<AxisCase>> SourceAxes() =>
		AutoTest.For(SiteFolderRules.Instance, Gen.Const(ValidSource()))
			.Options(o => o.UseFileSystem())
			.CounterExample(SiteFolderRules.StayInTheSite, f => f with { Files = [.. f.Files, Add(f.Root, "../elsewhere/braggarr.yml", AFile)] })
			.CounterExample(SiteFolderRules.NoLinks, f => f with { Files = [.. f.Files, Add(f.Root, "posts/linked.md", (fs, path) => fs.File.CreateSymbolicLink(path, f.Files[0].FullName))] })
			.CounterExample(SiteFolderRules.NoHiddenFiles, f => f with { Files = [.. f.Files, Add(f.Root, "posts/.secret/index.md", AFile)] })
			.AsDataSource();

	[Test]
	[MethodDataSource(nameof(SourceAxes))]
	public Task Every_source_axis_holds(AxisCase axis) => axis.RunAsync();

	public static IEnumerable<Func<AxisCase>> OutputAxes() =>
		AutoTest.For(SiteOutputRules.Instance, Gen.Const(ValidOutput()))
			.Options(o => o.UseFileSystem())
			.CounterExample(SiteOutputRules.StayInTheOutput, o => o with { Files = [.. o.Files, o.Root.FileSystem.FileInfo.New("/elsewhere/index.html")] })
			.CounterExample(SiteOutputRules.NoHiddenFiles, o => o with { Files = [.. o.Files, o.Root.FileSystem.FileInfo.New("/out/.well-known/x")] })
			.CounterExample(SiteOutputRules.NoTwoFilesDifferOnlyInCase, o => o with { Files = [.. o.Files, o.Root.FileSystem.FileInfo.New("/out/Index.html")] })
			.AsDataSource();

	[Test]
	[MethodDataSource(nameof(OutputAxes))]
	public Task Every_output_axis_holds(AxisCase axis) => axis.RunAsync();

	[Test]
	public async Task A_site_with_a_linked_file_is_not_read_at_all()
	{
		var fs = new MockFileSystem();
		fs.AddFile("/site/braggarr.yml", new MockFileData("title: s\nurl: https://example.com/\nauthor: { name: me }"));
		fs.AddFile("/secret.md", new MockFileData("---\ntitle: Secret\ndate: 2026-11-02\n---\nSecret."));
		fs.AddDirectory("/site/posts");
		fs.File.CreateSymbolicLink("/site/posts/linked.md", "/secret.md");

		var read = await SiteReader.ReadAsync(fs, "/site");

		read.Site.Should().BeNull();
		read.Diagnostics.Should().ContainSingle(d => d.Level == DiagnosticLevel.Error);
	}

	[Test]
	public async Task A_build_writes_nothing_outside_its_output()
	{
		var fs = new MockFileSystem();
		var built = new BuiltSite(new Dictionary<string, byte[]> { ["../escape.html"] = [1] }, []);

		var problems = await SiteBuilder.WriteAsync(built, fs, "/out");

		problems.Should().NotBeEmpty();
		fs.FileExists("/escape.html").Should().BeFalse();
	}
}
