using System.Globalization;

namespace Braggarr.Blog;

/// <summary>The files <c>braggarr blog init</c> and <c>braggarr blog new</c> write, relative to the site's folder.</summary>
public static class Scaffold
{
	public static IReadOnlyDictionary<string, string> Site(DateTimeOffset now) =>
		new Dictionary<string, string>(StringComparer.Ordinal)
		{
			[SiteReader.ConfigFile] = """
				# The site. Profile links (Bluesky, Mastodon, …) are not set here: Braggarr writes them to _braggarr/profiles.yml.
				title: My blog
				description: What this blog is about, in a sentence.
				url: https://example.com/
				author:
				  name: Your Name
				  # avatar: images/avatar.jpg   # a file in static/, or a full https address
				  # bio: One line about you.
				permalink: /posts/{slug}/
				nav:
				  - title: About
				    page: about
				  - title: Archive
				    href: /archive/
				# accent: "#d9480f"
				# comments:                     # giscus: GitHub Discussions, ids from https://giscus.app
				#   giscus: { repo: owner/name, repo_id: …, category: Comments, category_id: … }

				""",
			["pages/about.md"] = """
				---
				title: About
				---

				Who you are and what you write about.

				""",
			[Post("Hello", now).Path] = Post("Hello", now, draft: false).Text
		};

	/// <summary>A new post called <paramref name="title"/>, dated <paramref name="now"/> in its own offset.</summary>
	public static (string Path, string Text) Post(string title, DateTimeOffset now, bool draft = true)
	{
		var slug = Segments.Slug(title) is { Length: > 0 } s ? s : "post";
		var front = SiteYaml.Write(new NewPost { Title = title, Date = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) })
			// Added by hand: SharpYaml writes a null bool as "draft: null" whatever the ignore condition says.
			+ (draft ? "draft: true\n" : "");
		return ($"posts/{now:yyyy-MM-dd}-{slug}/index.md", SiteYaml.Join(front, "Write here. Files beside this one (images, say) are published next to the post."));
	}
}
