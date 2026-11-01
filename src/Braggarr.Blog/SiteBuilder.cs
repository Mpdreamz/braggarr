using System.Globalization;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Xml;
using Braggarr.Blog.Theme;
using Nullean.Entities;
using Nullean.Entities.FileSystem;
using RazorSlices;

namespace Braggarr.Blog;

/// <summary>How to build: at what moment (posts dated later wait), and whether drafts and later posts are shown, as a preview does.</summary>
public sealed record BuildOptions(DateTimeOffset Now)
{
	public bool Drafts { get; init; }

	/// <summary>The site's address for this build, instead of the one in <c>braggarr.yml</c>: a preview, or a deploy elsewhere.</summary>
	public Uri? Url { get; init; }
}

/// <summary>A built site: every file, by its path in the site (<c>articles/curb/index.html</c>), and what the build found.</summary>
public sealed record BuiltSite(IReadOnlyDictionary<string, byte[]> Files, IReadOnlyList<Diagnostic> Diagnostics)
{
	/// <summary>The file a request for <paramref name="path"/> gets: the file itself, or a folder's <c>index.html</c>.</summary>
	public byte[]? Find(string path)
	{
		var relative = Uri.UnescapeDataString(path).TrimStart('/');
		return Files.TryGetValue(relative, out var file) ? file
			: Files.TryGetValue(relative.Length == 0 || relative.EndsWith('/') ? relative + "index.html" : relative + "/index.html", out var index) ? index
			: null;
	}
}

/// <summary>What a build writes: below its output folder, nothing hidden, and no two files a case-folding disk would confuse.</summary>
public sealed record SiteOutput(IDirectoryInfo Root, IReadOnlyList<IFileInfo> Files);

[Constraints<SiteOutput>]
public static partial class SiteOutputRules
{
	public static readonly Constraint<SiteOutput> StayInTheOutput = Files<SiteOutput>.StayInside(o => o.Root, o => o.Files);

	public static readonly Constraint<SiteOutput> NoHiddenFiles = Files<SiteOutput>.NoHiddenSegments(o => o.Root, o => o.Files);

	public static readonly Constraint<SiteOutput> NoTwoFilesDifferOnlyInCase = Files<SiteOutput>.UniqueIgnoringCase(o => o.Files);
}

/// <summary>Builds a site: renders its pages through the theme, writes the feed, the sitemap and the redirects, copies its files.</summary>
public static class SiteBuilder
{
	private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

	public static async Task<BuiltSite> RenderAsync(IFileSystem source, string root, Site site, BuildOptions options, CancellationToken ct = default)
	{
		var config = options.Url is { } url ? site.Config with { Url = url } : site.Config;
		site = site with { Config = config };
		var plan = site.Plan(options.Now, options.Drafts);
		var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
		var diagnostics = new List<Diagnostic>();
		var rendered = new List<(string Path, string Source, Rendered Body)>();

		var css = Theme("style.css");
		var stylesheet = $"/_theme/style.{Hash(css)}.css";
		files[stylesheet.TrimStart('/')] = css;

		LayoutModel Layout(string title, string path) => new(plan, title, path) { Stylesheet = stylesheet, Description = config.Description };

		// The index, a page at a time.
		for (var i = 0; i < plan.Index.Count; i++)
		{
			var path = SitePlan.IndexPath(i + 1);
			var layout = Layout(i == 0 ? config.Title : $"Page {i + 1}", path);
			await Html(files, path, IndexView.Create(new IndexModel(layout, plan.Index[i], i + 1, plan.Index.Count)), ct);
		}

		// The posts, each with its files beside it.
		for (var i = 0; i < plan.Published.Count; i++)
		{
			var post = plan.Published[i];
			var path = post.Permalink(config);
			var body = MarkdownRenderer.Render(post.Markdown);
			var description = post.Description ?? MarkdownRenderer.Excerpt(post.Markdown);
			var model = new PostModel(Layout(post.Title, path), post, body, i > 0 ? plan.Published[i - 1] : null, i + 1 < plan.Published.Count ? plan.Published[i + 1] : null);
			var image = model.CoverHref is { } cover ? Full(config, cover) : config.Author.Avatar is { } avatar ? Full(config, model.Layout.FileHref(avatar)) : null;
			model = model with
			{
				Layout = model.Layout with
				{
					Description = description,
					Kind = "article",
					Image = image,
					ImageAlt = model.CoverHref is not null ? post.CoverAlt : null,
					Canonical = post.Canonical,
					JsonLd = JsonLd.For(post, config, description, image)
				}
			};
			await Html(files, path, PostView.Create(model), ct);
			await CopyAsync(source, root, post.Folder, post.Assets, path, files, ct);
			rendered.Add((path, post.Source, body));
		}

		string? script = null;
		foreach (var page in site.Pages)
		{
			var body = MarkdownRenderer.Render(page.Markdown, sections: true);
			diagnostics.AddRange(Sections(page, body, site));
			var intro = body.Sections.Contains("intro") && site.Intro is { } text ? MarkdownRenderer.Render(text) : null;
			if (body.Sections.Contains("projects") && site.Groups.Count > 0 && script is null)
			{
				var js = Theme("projects.js");
				script = $"/_theme/projects.{Hash(js)}.js";
				files[script.TrimStart('/')] = js;
			}
			var layout = Layout(page.Title, page.Path) with { Description = page.Description ?? MarkdownRenderer.Excerpt(page.Markdown) };
			var model = new PageModel(layout, page, body) { Intro = intro, Script = body.Sections.Contains("projects") ? script : null };
			await Html(files, page.Path, PageView.Create(model), ct);
			await CopyAsync(source, root, page.Folder, page.Assets, page.Path, files, ct);
			rendered.Add((page.Path, page.Source, body));
			if (intro is not null)
			{
				rendered.Add((page.Path, SiteReader.IntroFile, intro));
			}
		}

		foreach (var tag in plan.Tags)
		{
			await Html(files, tag.Path, TagView.Create(new TagModel(Layout($"#{tag.Tag}", tag.Path), tag)), ct);
		}
		await Html(files, "/archive/", ArchiveView.Create(new ArchiveModel(Layout("Archive", "/archive/"))), ct);
		files["404.html"] = await RenderAsync(NotFoundView.Create(Layout("Not found", "/404.html")), ct);

		foreach (var redirect in site.Redirects())
		{
			var to = config.Absolute(redirect.To.TrimStart('/'));
			files[redirect.From.EndsWith('/') ? redirect.From.TrimStart('/') + "index.html" : redirect.From.TrimStart('/')] =
				await RenderAsync(RedirectView.Create(new RedirectModel(to, config.Title)), ct);
		}

		foreach (var file in site.Files)
		{
			files[file] = await source.File.ReadAllBytesAsync(source.Path.Combine(root, "static", file), ct);
		}

		files[config.Feed] = Feed(plan, config);
		files["sitemap.xml"] = Sitemap(plan, config);
		files["robots.txt"] = Utf8.GetBytes($"User-agent: *\nAllow: /\nSitemap: {config.Absolute("sitemap.xml")}\n");

		diagnostics.AddRange(Check(rendered, files, config));
		return new BuiltSite(files, diagnostics);
	}

	/// <summary>
	/// Writes a built site to <paramref name="output"/>, after its paths pass <see cref="SiteOutputRules"/>, and removes what an
	/// earlier build left there that this one did not write.
	/// </summary>
	public static async Task<IReadOnlyList<Diagnostic>> WriteAsync(BuiltSite built, IFileSystem fs, string output, CancellationToken ct = default)
	{
		var root = fs.DirectoryInfo.New(fs.Path.GetFullPath(output));
		var targets = built.Files.Keys.Select(k => fs.FileInfo.New(fs.Path.Combine(root.FullName, k))).ToList();
		var refused = SiteOutputRules.All.Validate(new SiteOutput(root, targets));
		if (refused.Count > 0)
		{
			return [.. refused.Select(v => Diagnostic.Error(v.File ?? output, v.Message))];
		}

		root.Create();
		var wanted = targets.Select(t => t.FullName).ToHashSet(StringComparer.Ordinal);
		foreach (var stale in root.EnumerateFiles("*", SearchOption.AllDirectories).Where(f => !wanted.Contains(f.FullName)))
		{
			stale.Delete();
		}
		foreach (var (relative, bytes) in built.Files)
		{
			var target = fs.Path.Combine(root.FullName, relative);
			fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(target)!);
			await fs.File.WriteAllBytesAsync(target, bytes, ct);
		}
		return [];
	}

	/// <summary>Text as it is, any script included: only markup is escaped, so a title in Japanese stays readable in the source.</summary>
	private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

	private static async Task Html(SortedDictionary<string, byte[]> files, string path, RazorSlice slice, CancellationToken ct) =>
		files[path.TrimStart('/') + "index.html"] = await RenderAsync(slice, ct);

	private static async Task<byte[]> RenderAsync(RazorSlice slice, CancellationToken ct)
	{
		await using var writer = new StringWriter(CultureInfo.InvariantCulture);
		await slice.RenderAsync(writer, Encoder, ct);
		return Utf8.GetBytes(writer.ToString());
	}

	private static async Task CopyAsync(
		IFileSystem source, string root, string folder, ValueList<string> assets, string path, SortedDictionary<string, byte[]> files, CancellationToken ct)
	{
		foreach (var asset in assets)
		{
			files[path.TrimStart('/') + asset] = await source.File.ReadAllBytesAsync(source.Path.Combine(root, folder, asset), ct);
		}
	}

	private static string Full(SiteConfig config, string href) =>
		Uri.TryCreate(href, UriKind.Absolute, out var absolute) && absolute.Scheme is "https" or "http" ? href : new Uri(config.Url, href).ToString();

	/// <summary>
	/// What <c>--strict</c> fails on: a link or image to this site that leads nowhere, and an image without alt text. Links elsewhere
	/// are not followed: a build never goes to the network.
	/// </summary>
	internal static IEnumerable<Diagnostic> Check(IEnumerable<(string Path, string Source, Rendered Body)> rendered, IReadOnlyDictionary<string, byte[]> files, SiteConfig config)
	{
		var built = new BuiltSite(files, []);
		foreach (var (path, source, body) in rendered)
		{
			foreach (var image in body.Images.Where(i => string.IsNullOrWhiteSpace(i.Alt)))
			{
				yield return Diagnostic.Warning(source, $"The image {image.Url} has no alt text: write ![what it shows]({image.Url}).");
			}
			foreach (var target in body.Links.Concat(body.Images.Select(i => i.Url)))
			{
				if (Local(path, target, config) is { } local && built.Find(local) is null)
				{
					yield return Diagnostic.Warning(source, $"{target} leads nowhere on this site.");
				}
			}
		}
	}

	/// <summary>A link's path on this site, resolved from the page at <paramref name="from"/>; null when it goes elsewhere or only to an anchor.</summary>
	internal static string? Local(string from, string target, SiteConfig config)
	{
		if (target.Length == 0 || target[0] == '#' || target.StartsWith("//", StringComparison.Ordinal))
		{
			return null;
		}
		var site = new Uri(config.Url, ".");
		// Before trying it as an absolute address: on Unix, "/about/" parses as file:///about/.
		if (target[0] == '/')
		{
			return new Uri(new Uri("https://site.invalid"), target).AbsolutePath;
		}
		if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
		{
			if (absolute.Scheme is not ("http" or "https") || !string.Equals(absolute.Host, site.Host, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			return absolute.AbsolutePath;
		}
		var resolved = new Uri(new Uri(new Uri("https://site.invalid"), from), target);
		return resolved.AbsolutePath;
	}

	/// <summary>The sections a page can show, and the file each shows.</summary>
	public static IReadOnlyDictionary<string, string> SectionFiles { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		["intro"] = SiteReader.IntroFile,
		["projects"] = SiteReader.ProjectsFile,
		["profiles"] = SiteReader.ProfilesFile
	};

	/// <summary>What <c>--strict</c> fails on in a page's sections: one the theme does not have, or one whose file the site lacks.</summary>
	internal static IEnumerable<Diagnostic> Sections(Page page, Rendered body, Site site)
	{
		foreach (var name in body.Sections.Distinct(StringComparer.Ordinal))
		{
			var has = name switch
			{
				"intro" => site.Intro is not null,
				"projects" => site.Groups.Count > 0,
				"profiles" => site.Profiles.Count > 0,
				_ => (bool?)null
			};
			if (has is null)
			{
				yield return Diagnostic.Warning(page.Source, $"{{{{ {name} }}}} is not a section: a page can show {string.Join(", ", SectionFiles.Keys.Select(k => $"{{{{ {k} }}}}"))}.");
			}
			else if (has == false)
			{
				yield return Diagnostic.Warning(page.Source, $"{{{{ {name} }}}} shows {SectionFiles[name]}, which this site does not have: Braggarr writes it.");
			}
		}
	}

	private static byte[] Theme(string name)
	{
		using var stream = typeof(SiteBuilder).Assembly.GetManifestResourceStream($"theme/{name}")
			?? throw new InvalidOperationException($"The theme has no {name}.");
		using var copy = new MemoryStream();
		stream.CopyTo(copy);
		return copy.ToArray();
	}

	private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..10];

	/// <summary>An Atom feed of the newest posts, with their full text: relative links resolve against each post's address.</summary>
	internal static byte[] Feed(SitePlan plan, SiteConfig config)
	{
		const string Atom = "http://www.w3.org/2005/Atom";
		using var stream = new MemoryStream();
		using (var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = Utf8, Indent = true, Async = false }))
		{
			xml.WriteStartDocument();
			xml.WriteStartElement("feed", Atom);
			xml.WriteElementString("title", Atom, config.Title);
			if (config.Description.Length > 0)
			{
				xml.WriteElementString("subtitle", Atom, config.Description);
			}
			Link(config.Absolute(config.Feed), "self");
			Link(config.Url.ToString(), "alternate");
			xml.WriteElementString("id", Atom, config.Url.ToString());
			// The newest post's date, not the clock's: building twice gives the same feed.
			xml.WriteElementString("updated", Atom, Rfc3339(plan.Feed.Count > 0 ? plan.Feed[0].Date : DateTimeOffset.UnixEpoch));
			xml.WriteStartElement("author", Atom);
			xml.WriteElementString("name", Atom, config.Author.Name);
			xml.WriteEndElement();
			foreach (var post in plan.Feed)
			{
				var href = config.Absolute(post.Permalink(config).TrimStart('/'));
				xml.WriteStartElement("entry", Atom);
				xml.WriteAttributeString("xml", "base", null, href);
				xml.WriteElementString("title", Atom, post.Title);
				Link(href, "alternate");
				xml.WriteElementString("id", Atom, href);
				xml.WriteElementString("published", Atom, Rfc3339(post.Date));
				xml.WriteElementString("updated", Atom, Rfc3339(post.Date));
				xml.WriteElementString("summary", Atom, post.Description ?? MarkdownRenderer.Excerpt(post.Markdown));
				xml.WriteStartElement("content", Atom);
				xml.WriteAttributeString("type", "html");
				xml.WriteString(MarkdownRenderer.Render(post.Markdown).Html);
				xml.WriteEndElement();
				foreach (var tag in post.Tags)
				{
					xml.WriteStartElement("category", Atom);
					xml.WriteAttributeString("term", tag);
					xml.WriteEndElement();
				}
				xml.WriteEndElement();
			}
			xml.WriteEndElement();
			xml.WriteEndDocument();

			void Link(string href, string rel)
			{
				xml.WriteStartElement("link", Atom);
				xml.WriteAttributeString("href", href);
				xml.WriteAttributeString("rel", rel);
				xml.WriteEndElement();
			}
		}
		return stream.ToArray();
	}

	internal static byte[] Sitemap(SitePlan plan, SiteConfig config)
	{
		const string Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
		var dated = plan.Published.ToDictionary(p => p.Permalink(config), p => p.Date, StringComparer.Ordinal);
		using var stream = new MemoryStream();
		using (var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = Utf8, Indent = true }))
		{
			xml.WriteStartDocument();
			xml.WriteStartElement("urlset", Ns);
			foreach (var path in plan.Sitemap())
			{
				xml.WriteStartElement("url", Ns);
				xml.WriteElementString("loc", Ns, config.Absolute(path.TrimStart('/')));
				if (dated.TryGetValue(path, out var date))
				{
					xml.WriteElementString("lastmod", Ns, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
				}
				xml.WriteEndElement();
			}
			xml.WriteEndElement();
			xml.WriteEndDocument();
		}
		return stream.ToArray();
	}

	private static string Rfc3339(DateTimeOffset date) => date.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}
