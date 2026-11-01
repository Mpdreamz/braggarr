using System.Globalization;
using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Nullean.Entities;
using Nullean.Entities.FileSystem;

namespace Braggarr.Blog;

/// <summary>The files of a site's folder that a build reads, as the folder lists them.</summary>
public sealed record SiteFolder(IDirectoryInfo Root, IReadOnlyList<IFileInfo> Files);

/// <summary>A build reads its own folder and nothing else: no file through a link, outside the folder, or hidden.</summary>
[Constraints<SiteFolder>]
public static partial class SiteFolderRules
{
	public static readonly Constraint<SiteFolder> StayInTheSite = Files<SiteFolder>.StayInside(f => f.Root, f => f.Files);

	public static readonly Constraint<SiteFolder> NoLinks = Files<SiteFolder>.NotSymlink(f => f.Files, f => f.Root);

	public static readonly Constraint<SiteFolder> NoHiddenFiles = Files<SiteFolder>.NoHiddenSegments(f => f.Root, f => f.Files);
}

/// <summary>A site read from its folder, with what was wrong in it.</summary>
public sealed record ReadSite(Site? Site, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Reads a site's folder:
/// <list type="bullet">
/// <item><c>braggarr.yml</c>, the config;</item>
/// <item><c>posts/&lt;name&gt;/index.md</c> with the files beside it, or <c>posts/&lt;name&gt;.md</c>;</item>
/// <item><c>pages/&lt;name&gt;.md</c> or <c>pages/&lt;name&gt;/index.md</c>;</item>
/// <item><c>_braggarr/profiles.yml</c>, which Braggarr writes;</item>
/// <item><c>static/</c>, copied to the site's root as it is;</item>
/// <item><c>head.html</c> and <c>footer.html</c>, added to every page.</item>
/// </list>
/// A name may start with its date (<c>2026-11-02-curb-1-0</c>): the date is the post's when the front matter has none, and the rest
/// is its slug.
/// </summary>
public static partial class SiteReader
{
	public const string ConfigFile = "braggarr.yml";

	public const string ProfilesFile = "_braggarr/profiles.yml";

	public static async Task<ReadSite> ReadAsync(IFileSystem fs, string root, CancellationToken ct = default)
	{
		var diagnostics = new List<Diagnostic>();
		var rootDir = fs.DirectoryInfo.New(fs.Path.GetFullPath(root));
		if (!rootDir.Exists)
		{
			return new ReadSite(null, [Diagnostic.Error(root, "The site's folder does not exist.")]);
		}

		var files = Listed(rootDir);
		var folder = new SiteFolder(rootDir, files);
		var refused = SiteFolderRules.All.Validate(folder);
		if (refused.Count > 0)
		{
			// A link or an escape: nothing from the folder is trusted until it is gone.
			return new ReadSite(null, [.. refused.Select(v => Diagnostic.Error(v.File ?? root, v.Message))]);
		}
		var byPath = files.ToDictionary(f => Relative(rootDir, f), f => f, StringComparer.Ordinal);

		if (!byPath.TryGetValue(ConfigFile, out var configFile))
		{
			return new ReadSite(null, [Diagnostic.Error(ConfigFile, "There is no braggarr.yml: run `braggarr blog init` to start a site here.")]);
		}
		var config = ReadConfig(await fs.File.ReadAllTextAsync(configFile.FullName, ct), diagnostics);
		if (config is null)
		{
			return new ReadSite(null, diagnostics);
		}
		diagnostics.AddRange(Diagnostic.From(ConfigFile, SiteConfigRules.All.Validate(config)));

		var posts = new List<BlogPost>();
		foreach (var (source, assets) in Documents(byPath.Keys, "posts/"))
		{
			if (await ReadPostAsync(fs, byPath[source].FullName, source, assets, diagnostics, ct) is { } post)
			{
				diagnostics.AddRange(Diagnostic.From(source, BlogPostRules.All.Validate(post)));
				posts.Add(post);
			}
		}

		var pages = new List<Page>();
		foreach (var (source, assets) in Documents(byPath.Keys, "pages/"))
		{
			if (await ReadPageAsync(fs, byPath[source].FullName, source, assets, diagnostics, ct) is { } page)
			{
				pages.Add(page);
			}
		}

		var profiles = byPath.TryGetValue(ProfilesFile, out var profilesFile)
			? ReadProfiles(await fs.File.ReadAllTextAsync(profilesFile.FullName, ct), diagnostics)
			: [];

		var site = new Site(config, [.. posts], [.. pages])
		{
			Profiles = profiles,
			Files = [.. byPath.Keys.Where(k => k.StartsWith("static/", StringComparison.Ordinal)).Select(k => k["static/".Length..]).Order(StringComparer.Ordinal)],
			Head = byPath.TryGetValue("head.html", out var head) ? await fs.File.ReadAllTextAsync(head.FullName, ct) : null,
			Footer = byPath.TryGetValue("footer.html", out var footer) ? await fs.File.ReadAllTextAsync(footer.FullName, ct) : null
		};
		diagnostics.AddRange(Diagnostic.From(ConfigFile, SiteRules.All.Validate(site)));
		return new ReadSite(site, diagnostics);
	}

	/// <summary>
	/// The files a build may read: everything below the root, skipping hidden folders (<c>.git</c>, <c>.github</c>) and the output
	/// most builds write into (<c>.artifacts</c>), and never following a linked folder.
	/// </summary>
	private static List<IFileInfo> Listed(IDirectoryInfo root)
	{
		var files = new List<IFileInfo>();
		var pending = new Stack<IDirectoryInfo>([root]);
		while (pending.Count > 0)
		{
			var dir = pending.Pop();
			foreach (var entry in dir.EnumerateFileSystemInfos())
			{
				if (entry.Name.StartsWith('.'))
				{
					continue;
				}
				if (entry is IDirectoryInfo sub)
				{
					if (sub.LinkTarget is not null)
					{
						// Listed so the rules refuse it, never entered.
						files.Add(root.FileSystem.FileInfo.New(sub.FullName));
						continue;
					}
					if (sub.Name is "node_modules")
					{
						continue;
					}
					pending.Push(sub);
				}
				else if (entry is IFileInfo file)
				{
					files.Add(file);
				}
			}
		}
		return files;
	}

	private static string Relative(IDirectoryInfo root, IFileInfo file) =>
		root.FileSystem.Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');

	/// <summary>The markdown documents below <paramref name="prefix"/>, each with the files beside it when it is a folder's <c>index.md</c>.</summary>
	private static IEnumerable<(string Source, ValueList<string> Assets)> Documents(IEnumerable<string> paths, string prefix)
	{
		var below = paths.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p[prefix.Length..]).ToList();
		foreach (var path in below.Order(StringComparer.Ordinal))
		{
			var parts = path.Split('/');
			if (parts.Length == 1 && path.EndsWith(".md", StringComparison.Ordinal))
			{
				yield return (prefix + path, []);
			}
			else if (parts.Length == 2 && parts[1] == "index.md")
			{
				var folder = parts[0] + "/";
				yield return (prefix + path, [.. below.Where(p => p.StartsWith(folder, StringComparison.Ordinal) && p != path).Select(p => p[folder.Length..])]);
			}
		}
	}

	internal static SiteConfig? ReadConfig(string yaml, List<Diagnostic> diagnostics)
	{
		ConfigDocument document;
		try
		{
			document = SiteYaml.Config(yaml);
		}
		catch (Exception e) when (e is SharpYaml.YamlException or FormatException or InvalidOperationException)
		{
			diagnostics.Add(Diagnostic.Error(ConfigFile, $"braggarr.yml is not valid YAML: {e.Message}"));
			return null;
		}
		if (!Uri.TryCreate(document.Url ?? "", UriKind.Absolute, out var url))
		{
			diagnostics.Add(Diagnostic.Error(ConfigFile, "url is the site's full address, like https://example.com/."));
			return null;
		}
		var pageLinks = (document.Nav ?? []).Select(n => new NavLink(n.Title ?? "", n.Href ?? (n.Page is { } page ? $"/{page}/" : "")));
		return new SiteConfig(document.Title ?? "", document.Description ?? "", url, new Author(document.Author?.Name ?? "")
		{
			Avatar = document.Author?.Avatar,
			Bio = document.Author?.Bio
		})
		{
			Language = document.Language ?? "en",
			Permalink = document.Permalink is { } permalink ? new Permalink(permalink) : Permalink.Default,
			Nav = [.. pageLinks],
			Accent = document.Accent,
			Comments = document.Comments?.Giscus is { } c
				? new Giscus(c.Repo ?? "", c.RepoId ?? "", c.Category ?? "", c.CategoryId ?? "") { Mapping = c.Mapping ?? "pathname" }
				: null,
			Feed = document.Feed ?? "feed.xml",
			PostsPerPage = document.PostsPerPage ?? 10,
			FeedSize = document.FeedSize ?? 20,
			UnknownKeys = [.. SiteYaml.Keys(yaml).Where(k => !SiteConfigRules.Keys.Contains(k))]
		};
	}

	private static async Task<BlogPost?> ReadPostAsync(
		IFileSystem fs, string fullName, string source, ValueList<string> assets, List<Diagnostic> diagnostics, CancellationToken ct)
	{
		if (Parse(await fs.File.ReadAllTextAsync(fullName, ct), source, diagnostics) is not { } parsed)
		{
			return null;
		}
		var (front, body, keys) = parsed;
		var (named, folder) = Named(source);
		var dated = DatePrefix().Match(named);
		var slug = front.Slug ?? (dated.Success ? named[dated.Length..] : named);

		DateTimeOffset date;
		if (front.Date is { } written)
		{
			if (!TryDate(written, out date, out var hasOffset))
			{
				diagnostics.Add(Diagnostic.Error(source, $"date \"{written}\" is not a date: write it as 2026-11-02T09:00:00+01:00."));
				return null;
			}
			if (!hasOffset)
			{
				diagnostics.Add(Diagnostic.Warning(source, $"date \"{written}\" has no offset, so it is read as UTC: write it as {date:yyyy-MM-dd'T'HH:mm:ss}+01:00 (or your own offset)."));
			}
		}
		else if (dated.Success)
		{
			date = DateTimeOffset.ParseExact(dated.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
		}
		else
		{
			diagnostics.Add(Diagnostic.Error(source, "A post needs a date: in its front matter, or at the start of its name (2026-11-02-my-post)."));
			return null;
		}

		return new BlogPost(slug, front.Title ?? "", date, body)
		{
			Folder = folder,
			Source = source,
			Assets = assets,
			Description = front.Description,
			Tags = [.. front.Tags ?? []],
			Draft = front.Draft ?? false,
			Cover = front.Cover,
			CoverAlt = front.CoverAlt,
			Aliases = [.. front.Aliases ?? []],
			Canonical = front.Canonical,
			Project = front.Project,
			Campaign = front.Campaign,
			UnknownKeys = [.. keys.Where(k => !BlogPostRules.Keys.Contains(k))]
		};
	}

	private static async Task<Page?> ReadPageAsync(
		IFileSystem fs, string fullName, string source, ValueList<string> assets, List<Diagnostic> diagnostics, CancellationToken ct)
	{
		if (Parse(await fs.File.ReadAllTextAsync(fullName, ct), source, diagnostics) is not { } parsed)
		{
			return null;
		}
		var (front, body, _) = parsed;
		var (named, folder) = Named(source);
		var page = new Page(front.Slug ?? named, front.Title ?? named, body)
		{
			Folder = folder,
			Source = source,
			Assets = assets,
			Description = front.Description,
			Aliases = [.. front.Aliases ?? []]
		};
		if (!Segments.IsSafe(page.Slug))
		{
			diagnostics.Add(Diagnostic.Error(source, $"\"{page.Slug}\" cannot name a page's folder: no slash, no leading dot."));
			return null;
		}
		if (!page.Aliases.All(Segments.IsSitePath))
		{
			diagnostics.Add(Diagnostic.Error(source, "An alias is a path on this site (/old/path/ or /old.html), with no .., query or fragment."));
		}
		return page;
	}

	private static (FrontMatter Front, string Body, IEnumerable<string> Keys)? Parse(string text, string source, List<Diagnostic> diagnostics)
	{
		try
		{
			var (yaml, body) = SiteYaml.Split(text);
			return (SiteYaml.Front(yaml), body, SiteYaml.Keys(yaml).ToList());
		}
		catch (Exception e) when (e is SharpYaml.YamlException or FormatException or InvalidOperationException)
		{
			diagnostics.Add(Diagnostic.Error(source, $"The front matter is not valid YAML: {e.Message}"));
			return null;
		}
	}

	/// <summary>A document's name (its folder's for an <c>index.md</c>, else its file's) and the folder its files sit in.</summary>
	private static (string Name, string Folder) Named(string source)
	{
		var parts = source.Split('/');
		return parts[^1] == "index.md"
			? (parts[^2], string.Join('/', parts[..^1]))
			: (parts[^1][..^".md".Length], string.Join('/', parts[..^1]));
	}

	private static ValueList<Profile> ReadProfiles(string yaml, List<Diagnostic> diagnostics)
	{
		try
		{
			return [.. (SiteYaml.Profiles(yaml).Profiles ?? []).Select(p => new Profile(p.Network ?? "", p.Url ?? "") { Name = p.Name })];
		}
		catch (Exception e) when (e is SharpYaml.YamlException or FormatException or InvalidOperationException)
		{
			diagnostics.Add(Diagnostic.Error(ProfilesFile, $"{ProfilesFile} is not valid YAML: {e.Message}"));
			return [];
		}
	}

	/// <summary>A date as front matter writes it: with an offset, or without one (read as UTC), with or without a time.</summary>
	internal static bool TryDate(string written, out DateTimeOffset date, out bool hasOffset)
	{
		var text = written.Trim();
		hasOffset = OffsetSuffix().IsMatch(text);
		return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date);
	}

	[GeneratedRegex(@"^(?<date>\d{4}-\d{2}-\d{2})-")]
	private static partial Regex DatePrefix();

	[GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})$")]
	private static partial Regex OffsetSuffix();
}
