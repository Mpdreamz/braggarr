using System.IO.Abstractions.TestingHelpers;

namespace Braggarr.Blog.Tests;

/// <summary>A site on a mock disk with one of everything: a post with an image beside it, a single-file post, a draft, a post dated later, a page with its sections, profiles, projects, static files.</summary>
public static class Sample
{
	public const string Root = "/site";

	public static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

	public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

	public static MockFileSystem Disk(string config = Config)
	{
		var fs = new MockFileSystem();
		fs.AddFile($"{Root}/braggarr.yml", new MockFileData(config));
		fs.AddFile($"{Root}/posts/2026-11-02-curb-1-0/index.md", new MockFileData("""
			---
			title: Curb 1.0 is out
			date: 2026-11-02T09:00:00+01:00
			description: A C# formatter that keeps diffs small.
			tags: [dotnet, release]
			cover: card.png
			cover_alt: A diff with one changed line
			aliases: [/articles/curb-2026-11-02/]
			project: curb
			---

			Curb keeps your diffs small. ![The diff](diff.png)

			## Why

			See [the about page](/about/) and [heed](../heed-review/).
			"""));
		fs.AddFile($"{Root}/posts/2026-11-02-curb-1-0/card.png", new MockFileData(Png));
		fs.AddFile($"{Root}/posts/2026-11-02-curb-1-0/diff.png", new MockFileData(Png));
		fs.AddFile($"{Root}/posts/2026-11-05-heed-review.md", new MockFileData("""
			---
			title: Review a branch without leaving the keyboard
			tags: [dotnet]
			---

			Heed reviews branches.
			"""));
		fs.AddFile($"{Root}/posts/2026-11-06-unfinished/index.md", new MockFileData("""
			---
			title: Unfinished
			date: 2026-11-06T09:00:00+01:00
			draft: true
			---

			Not yet.
			"""));
		fs.AddFile($"{Root}/posts/2026-12-01-scheduled/index.md", new MockFileData("""
			---
			title: Scheduled
			date: 2026-12-01T09:00:00+01:00
			---

			Later.
			"""));
		fs.AddFile($"{Root}/pages/about.md", new MockFileData("""
			---
			title: About
			aliases: [/about-me.html]
			---

			{{ author }}

			Who writes here.

			{{ intro }}

			## What I make

			{{ projects }}

			## Elsewhere

			{{ profiles }}
			"""));
		fs.AddFile($"{Root}/_braggarr/intro.md", new MockFileData("""
			Sam builds small tools for .NET. Everything written here is in [the archive](/archive/).
			"""));
		fs.AddFile($"{Root}/_braggarr/projects.yml", new MockFileData("""
			groups:
			  - title: Developer tools
			    description: Small tools that stay out of the way.
			    projects:
			      - name: curb
			        repo: nullean/curb
			        tldr: A C# formatter that keeps diffs small.
			        icon: media/curb.png
			        image: https://curb.example.com/card.png
			        language: C#
			        stars: 1234
			        release: { tag: v1.0.0, date: "2026-11-02T09:00:00+01:00", url: https://github.com/nullean/curb/releases/tag/v1.0.0 }
			        links:
			          - { title: Docs, url: https://curb.example.com/docs/ }
			      - name: heed
			        repo: heed-build/heed
			        url: https://heed.example.com/
			  - title: Libraries
			    projects:
			      - name: Nullean.Entities
			        repo: nullean/entities
			"""));
		fs.AddFile($"{Root}/_braggarr/profiles.yml", new MockFileData("""
			profiles:
			  - network: bluesky
			    name: "@example.com"
			    url: https://bsky.app/profile/example.com
			"""));
		fs.AddFile($"{Root}/_braggarr/media/curb.png", new MockFileData(Png));
		fs.AddFile($"{Root}/static/images/me.jpg", new MockFileData(Png));
		fs.AddFile($"{Root}/static/custom.css", new MockFileData("body { color: red; }"));
		fs.AddFile($"{Root}/head.html", new MockFileData("<meta name=\"fediverse:creator\" content=\"@me@example.social\">"));
		fs.AddFile($"{Root}/.git/config", new MockFileData("[core]"));
		fs.AddFile($"{Root}/node_modules/x/index.md", new MockFileData("not a post"));
		return fs;
	}

	public const string Config = """
		title: Example blog
		description: Notes on building things.
		url: https://www.example.com/
		author:
		  name: Sam
		  avatar: images/me.jpg
		  bio: Writes code.
		permalink: /articles/{slug}/
		nav:
		  - title: About
		    page: about
		accent: "#d9480f"
		comments:
		  giscus: { repo: owner/site, repo_id: R_1, category: Comments, category_id: DIC_1 }
		posts_per_page: 1
		""";

	public static async Task<(Site Site, BuiltSite Built, IReadOnlyList<Diagnostic> Read)> BuildAsync(MockFileSystem? fs = null, BuildOptions? options = null)
	{
		fs ??= Disk();
		var read = await SiteReader.ReadAsync(fs, Root);
		var built = await SiteBuilder.RenderAsync(fs, Root, read.Site!, options ?? new BuildOptions(Now));
		return (read.Site!, built, read.Diagnostics);
	}

	public static string Text(this BuiltSite built, string path) => System.Text.Encoding.UTF8.GetString(built.Files[path]);
}
