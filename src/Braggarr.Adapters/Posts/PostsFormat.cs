using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Braggarr.Domain;
using SharpYaml;
using SharpYaml.Serialization;

namespace Braggarr.Adapters.Posts;

/// <summary>The files of one campaign folder, by path relative to the folder: <c>campaign.md</c>, one <c>.md</c> per post, <c>deliveries.yml</c>.</summary>
public sealed record CampaignFiles(string Slug, IReadOnlyDictionary<string, string> Files);

/// <summary>
/// Reads and writes the posts repo's files. A campaign folder holds <c>campaign.md</c> (front matter and the brief), one markdown
/// file per post (front matter with <c>at</c>, then a <c>## network</c> section per variant), and <c>deliveries.yml</c>, which only
/// Braggarr writes. What this writes it reads back unchanged, byte for byte.
/// </summary>
public static partial class PostsFormat
{
	public const string CampaignFile = "campaign.md";
	public const string DeliveriesFile = "deliveries.yml";

	/// <summary>The judge's verdict, next to the campaign; written by Braggarr only.</summary>
	public const string JudgementFile = "judgement.yml";

	/// <summary>The folder of the owner's judge criteria, one markdown file each, read from main only.</summary>
	public const string JudgesFolder = "judges";

	/// <summary>
	/// A post's blog article, beside its file: <c>01-launch.blog.md</c>. Its own file, because an article's markdown has headings
	/// of its own, which would read as a post's <c>## network</c> sections.
	/// </summary>
	public const string BlogSuffix = ".blog.md";

	public static Campaign ReadCampaign(CampaignFiles folder, CampaignStatus mainStatus)
	{
		var (front, brief) = SplitFrontMatter(folder.Files[CampaignFile]);
		var meta = YamlSerializer.Deserialize(front, PostsYaml.Default.CampaignMeta) ?? new CampaignMeta();
		var networks = (meta.Networks ?? []).Select(n => Networks.TryParse(n, out var network) ? network : throw Problem(folder, $"\"{n}\" is not a network."));

		var posts = folder.Files
			.Where(f => f.Key.EndsWith(".md", StringComparison.Ordinal) && !f.Key.EndsWith(BlogSuffix, StringComparison.Ordinal) && f.Key != CampaignFile && !f.Key.Contains('/'))
			.OrderBy(f => f.Key, StringComparer.Ordinal)
			.Select(f => (Id: Path.GetFileNameWithoutExtension(f.Key), Text: f.Value))
			.Select(f => ReadPost(f.Id, f.Text, folder.Files.TryGetValue(f.Id + BlogSuffix, out var article) ? article : null));

		var status = mainStatus == CampaignStatus.Approved && meta.Held == true ? CampaignStatus.Held : mainStatus;
		return new Campaign(
			folder.Slug, meta.Title ?? folder.Slug, meta.Project ?? "", status, [.. networks.Distinct().Order()], [.. meta.Links ?? []], brief.Trim(),
			meta.CreatedBy ?? "", [.. posts]
		)
		{
			RepeatMonths = meta.RepeatMonths ?? 0,
			Accounts =
			[
				.. (meta.Accounts ?? []).Select(a => new CampaignAccount(
					Networks.TryParse(a.Key, out var network) ? network : throw Problem(folder, $"accounts: \"{a.Key}\" is not a network."), a.Value.Trim()))
			],
			LastRepeat = meta.LastRepeat is { } last ? DateTimeOffset.Parse(last, CultureInfo.InvariantCulture) : null,
			Points = [.. (meta.Points ?? []).Select(p => ReadPoint(folder, p))],
			Judgement = folder.Files.TryGetValue(JudgementFile, out var judgement) ? ReadJudgement(judgement) : null,
			Approval = meta.ApprovedBy is { } by && meta.ApprovedDigest is { } digest && meta.ApprovedAt is { } at
				? new Approval(by, digest, DateTimeOffset.Parse(at, CultureInfo.InvariantCulture))
				{
					NotBefore = meta.ApprovedNotBefore is { } notBefore ? DateTimeOffset.Parse(notBefore, CultureInfo.InvariantCulture) : null
				}
				: null
		};
	}

	private static TalkingPoint ReadPoint(CampaignFiles folder, PointMeta point) =>
		new(
			point.Text?.Trim() ?? throw Problem(folder, "a point has no text."),
			[
				.. (point.Wanted ?? []).Select(w => new WantedMedia(
					Enum.TryParse<MediaKind>(w.Kind, ignoreCase: true, out var kind) ? kind : throw Problem(folder, $"\"{w.Kind}\" is not image or video."),
					w.Description?.Trim() ?? ""
				))
			]
		);

	/// <summary>A post's file, and its blog article's when it has one.</summary>
	public static Post ReadPost(string id, string text, string? article = null)
	{
		var (front, body) = SplitFrontMatter(text);
		var meta = YamlSerializer.Deserialize(front, PostsYaml.Default.PostMeta) ?? new PostMeta();
		var at = DateTimeOffset.Parse(meta.At ?? throw new FormatException($"Post \"{id}\" has no \"at\"."), CultureInfo.InvariantCulture);

		var variants = new List<Variant>();
		Network? current = null;
		var section = new StringBuilder();
		foreach (var line in body.Split('\n'))
		{
			// The blog's text is its own file: a "## blog" here is a heading like any other.
			if (line.StartsWith("## ", StringComparison.Ordinal) && Networks.TryParse(line[3..], out var network) && network != Network.Blog)
			{
				Flush();
				current = network;
				continue;
			}
			_ = section.Append(line).Append('\n');
		}
		Flush();
		if (article is not null)
		{
			variants.Add(ReadArticle(id, article));
		}
		return new Post(id, at, [.. variants])
		{
			Angle = string.IsNullOrWhiteSpace(meta.Angle) ? null : meta.Angle,
			Networks = meta.Networks is { } named
				? [.. named.Select(n => Networks.TryParse(n, out var network) ? network : throw new FormatException($"Post \"{id}\": \"{n}\" is not a network."))]
				: null,
			// Counted from 1 in the file, as the owner reads them; from 0 in the model.
			Points = [.. (meta.Points ?? []).Select(p => p - 1)]
		};

		void Flush()
		{
			if (current is { } n)
			{
				variants.Add(ReadVariant(n, section.ToString()));
			}
			_ = section.Clear();
		}
	}

	private static Variant ReadVariant(Network network, string section)
	{
		var media = Image().Matches(section).Select(m => new MediaRef(m.Groups["path"].Value, m.Groups["alt"].Value));
		var text = Image().Replace(section, "").Trim().Replace("\r\n", "\n", StringComparison.Ordinal);
		return new Variant(network, text, [.. media]);
	}

	/// <summary>
	/// A blog article: its title, description, tags, cover and slug in front matter, its markdown below, verbatim. The images in
	/// it stay where they are, in the text, and are its media too, so they are checked and copied beside it.
	/// </summary>
	public static Variant ReadArticle(string id, string text)
	{
		var (front, body) = SplitFrontMatter(text);
		var meta = YamlSerializer.Deserialize(front, PostsYaml.Default.ArticleMeta) ?? new ArticleMeta();
		var markdown = body.Trim('\n');
		return new Variant(Network.Blog, markdown, Blogging.ImagesIn(markdown))
		{
			Article = new Article(
				meta.Title?.Trim() ?? throw new FormatException($"The blog post of \"{id}\" has no title."),
				meta.Description?.Trim() ?? "",
				[.. meta.Tags ?? []]
			)
			{
				Cover = meta.Cover is { } cover ? new MediaRef(cover, meta.CoverAlt?.Trim() ?? "") : null,
				Slug = string.IsNullOrWhiteSpace(meta.Slug) ? null : meta.Slug.Trim()
			}
		};
	}

	public static string WriteArticle(Variant variant)
	{
		var article = variant.Article ?? throw new ArgumentException("Only a blog variant has an article.", nameof(variant));
		var meta = new ArticleMeta
		{
			Title = article.Title,
			Description = article.Description,
			Tags = article.Tags.Count == 0 ? null : [.. article.Tags],
			Cover = article.Cover?.Path,
			CoverAlt = article.Cover?.Alt,
			Slug = article.Slug
		};
		return WithFrontMatter(YamlSerializer.Serialize(meta, PostsYaml.Default.ArticleMeta), variant.Text.Trim('\n'));
	}

	/// <summary>The folder's files for a new campaign: <c>campaign.md</c> and one file per post. No deliveries yet.</summary>
	public static IReadOnlyDictionary<string, string> WriteCampaign(Campaign campaign)
	{
		var meta = new CampaignMeta
		{
			Title = campaign.Title,
			Project = campaign.Project,
			Networks = [.. campaign.Networks.Select(n => n.Slug())],
			Accounts = campaign.Accounts.Count == 0 ? null : campaign.Accounts.OrderBy(a => a.Network).ToDictionary(a => a.Network.Slug(), a => a.Account),
			Links = campaign.Links.Count == 0 ? null : [.. campaign.Links],
			CreatedBy = campaign.CreatedBy,
			Held = campaign.Status == CampaignStatus.Held ? true : null,
			RepeatMonths = campaign.RepeatMonths > 0 ? campaign.RepeatMonths : null,
			LastRepeat = campaign.LastRepeat?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
			ApprovedBy = campaign.Approval?.By,
			ApprovedDigest = campaign.Approval?.Digest,
			ApprovedAt = campaign.Approval?.At.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
			ApprovedNotBefore = campaign.Approval?.NotBefore?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
			Points = campaign.Points.Count == 0
				? null
				:
				[
					.. campaign.Points.Select(p => new PointMeta
					{
						Text = p.Text,
						Wanted = p.Wanted.Count == 0
							? null
							: [.. p.Wanted.Select(w => new WantedMeta { Kind = w.Kind.ToString().ToLowerInvariant(), Description = w.Description })]
					})
				]
		};
		var files = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			[CampaignFile] = WithFrontMatter(YamlSerializer.Serialize(meta, PostsYaml.Default.CampaignMeta), campaign.Brief)
		};
		foreach (var post in campaign.Posts)
		{
			files[$"{post.Id}.md"] = WritePost(post);
			if (post.For(Network.Blog) is { Article: not null } blog)
			{
				files[post.Id + BlogSuffix] = WriteArticle(blog);
			}
		}
		if (campaign.Judgement is { } judgement)
		{
			files[JudgementFile] = WriteJudgement(judgement);
		}
		return files;
	}

	public static string WritePost(Post post)
	{
		var body = new StringBuilder();
		// The blog's article is a file of its own (WriteArticle).
		foreach (var variant in post.Variants.Where(v => v.Network != Network.Blog))
		{
			_ = body.Append("## ").Append(variant.Network.Slug()).Append("\n\n").Append(variant.Text.Trim()).Append('\n');
			foreach (var image in variant.Media)
			{
				_ = body.Append('\n').Append("![").Append(image.Alt).Append("](").Append(image.Path).Append(")\n");
			}
			_ = body.Append('\n');
		}
		var meta = new PostMeta
		{
			At = post.At.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
			Angle = post.Angle,
			Networks = post.Networks is { } named ? [.. named.Select(n => n.Slug())] : null,
			Points = post.Points.Count == 0 ? null : [.. post.Points.Select(p => p + 1)]
		};
		return WithFrontMatter(YamlSerializer.Serialize(meta, PostsYaml.Default.PostMeta), body.ToString().TrimEnd('\n'));
	}

	/// <summary>Sets or clears <c>held: true</c> in <c>campaign.md</c>, touching only that line so the owner's formatting stays.</summary>
	public static string SetHeld(string campaignMd, bool held) => SetField(campaignMd, "held", held ? "true" : null);

	/// <summary>Sets the approval's four fields, touching only their lines.</summary>
	public static string SetApproval(string campaignMd, Approval approval)
	{
		var md = SetField(campaignMd, "approved_by", approval.By);
		md = SetField(md, "approved_digest", approval.Digest);
		md = SetField(md, "approved_at", approval.At.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
		return SetField(md, "approved_not_before", approval.NotBefore?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
	}

	/// <summary>Sets one front matter field (or removes it, for null), touching only its line.</summary>
	public static string SetField(string campaignMd, string field, string? value)
	{
		var (front, body) = SplitFrontMatter(campaignMd);
		var lines = front.Split('\n').Where(l => !l.StartsWith($"{field}:", StringComparison.Ordinal)).ToList();
		if (value is not null)
		{
			lines.Insert(lines.FindLastIndex(l => l.Length > 0) + 1, $"{field}: {value}");
		}
		return WithFrontMatter(string.Join('\n', lines).TrimEnd('\n') + "\n", body.Trim());
	}

	public static Judgement? ReadJudgement(string yaml)
	{
		var document = YamlSerializer.Deserialize(yaml, PostsYaml.Default.JudgementDocument);
		if (document?.Digest is not { } digest || document.Criteria is not { } criteria || document.At is not { } at)
		{
			return null;
		}
		return new Judgement(
			digest, criteria, DateTimeOffset.Parse(at, CultureInfo.InvariantCulture), document.Model ?? "",
			[
				.. from f in document.Findings ?? []
				where Networks.TryParse(f.Network ?? "", out _)
				select new Finding(f.Criterion ?? "", f.Post ?? "", Networks.TryParse(f.Network!, out var n) ? n : default, f.Passed, f.Blocks, f.Reason ?? "")
			]
		);
	}

	public static string WriteJudgement(Judgement judgement) =>
		"# Written by Braggarr: the judge's verdict on this version of the campaign.\n"
		+ YamlSerializer.Serialize(new JudgementDocument
		{
			Digest = judgement.Digest,
			Criteria = judgement.Criteria,
			At = judgement.At.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
			Model = judgement.Model,
			Findings =
			[
				.. judgement.Findings.Select(f => new FindingEntry
				{
					Criterion = f.Criterion, Post = f.Post, Network = f.Network.Slug(), Passed = f.Passed, Blocks = f.Blocks, Reason = f.Reason
				})
			]
		}, PostsYaml.Default.JudgementDocument);

	/// <summary>
	/// A criterion from <c>judges/&lt;name&gt;.md</c>: front matter with an optional <c>name</c>, <c>on_failure: block</c> (the
	/// default) or <c>warn</c>, and optional <c>networks</c>; the body is what the judge is asked to check.
	/// </summary>
	public static Criterion ReadCriterion(string fileName, string text)
	{
		var (front, body) = SplitFrontMatter(text);
		var meta = YamlSerializer.Deserialize(front, PostsYaml.Default.CriterionMeta) ?? new CriterionMeta();
		var name = string.IsNullOrWhiteSpace(meta.Name) ? Path.GetFileNameWithoutExtension(fileName) : meta.Name.Trim();
		var blocks = (meta.OnFailure ?? "block").Trim().ToLowerInvariant() switch
		{
			"block" => true,
			"warn" => false,
			var other => throw new FormatException($"judges/{fileName}: on_failure is block or warn, not \"{other}\".")
		};
		if (string.IsNullOrWhiteSpace(body))
		{
			throw new FormatException($"judges/{fileName} says nothing to judge by: write the criterion below the front matter.");
		}
		return new Criterion(name, body.Trim(), blocks)
		{
			Networks = meta.Networks is { } named
				? [.. named.Select(n => Networks.TryParse(n, out var network) ? network : throw new FormatException($"judges/{fileName}: \"{n}\" is not a network."))]
				: null
		};
	}

	public const string ProjectsFolder = "projects";

	/// <summary>A project's brief: all of it in the front matter, so the owner edits it as plain YAML.</summary>
	public static ProjectBrief ReadBrief(string fileName, string text)
	{
		var (front, _) = SplitFrontMatter(text);
		var meta = YamlSerializer.Deserialize(front, PostsYaml.Default.BriefMeta) ?? new BriefMeta();
		return new ProjectBrief(
			meta.Project?.Trim() ?? Path.GetFileNameWithoutExtension(fileName), meta.Tldr?.Trim() ?? "", meta.Apart?.Trim() ?? "", meta.Audience?.Trim() ?? "")
		{
			Avoid = [.. (meta.Avoid ?? []).Select(a => a.Trim()).Where(a => a.Length > 0)],
			Links = [.. (meta.Links ?? []).Select(l => new BriefLink(l.Label?.Trim() ?? "", l.Url?.Trim() ?? ""))]
		};
	}

	public static string WriteBrief(ProjectBrief brief) =>
		WithFrontMatter(
			YamlSerializer.Serialize(new BriefMeta
			{
				Project = brief.Project,
				Tldr = brief.Tldr,
				Apart = brief.Apart,
				Audience = brief.Audience.Length == 0 ? null : brief.Audience,
				Avoid = brief.Avoid.Count == 0 ? null : [.. brief.Avoid],
				Links = brief.Links.Count == 0 ? null : [.. brief.Links.Select(l => new BriefLinkEntry { Label = l.Label, Url = l.Url })]
			}, PostsYaml.Default.BriefMeta),
			""
		);

	public static IReadOnlyList<Delivery> ReadDeliveries(string slug, string? yaml)
	{
		if (string.IsNullOrWhiteSpace(yaml))
		{
			return [];
		}
		var file = YamlSerializer.Deserialize(yaml, PostsYaml.Default.DeliveriesDocument) ?? new DeliveriesDocument();
		return
		[
			.. from e in file.Deliveries ?? []
			where Networks.TryParse(e.Network ?? "", out _)
			let network = Networks.TryParse(e.Network!, out var n) ? n : default
			select new Delivery(
				new DeliveryKey(slug, e.Post ?? "", network),
				Enum.Parse<DeliveryState>((e.State ?? nameof(DeliveryState.Pending)).Replace("_", "", StringComparison.Ordinal), ignoreCase: true),
				e.Attempts,
				e.LastAttempt is { } last ? DateTimeOffset.Parse(last, CultureInfo.InvariantCulture) : null,
				e.Url is { } url ? new Receipt(e.RemoteId ?? url, url) : null,
				e.Error,
				e.Issue
			)
		];
	}

	public static string WriteDeliveries(IEnumerable<Delivery> deliveries)
	{
		var document = new DeliveriesDocument
		{
			Deliveries =
			[
				.. from d in deliveries.OrderBy(d => d.Key)
				select new DeliveryEntry
				{
					Post = d.Key.Post,
					Network = d.Key.Network.Slug(),
					State = Snake(d.State.ToString()),
					Attempts = d.Attempts,
					LastAttempt = d.LastAttempt?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
					RemoteId = d.Receipt?.RemoteId,
					Url = d.Receipt?.Url,
					Error = d.Error,
					Issue = d.Issue
				}
			]
		};
		return "# Written by Braggarr: one entry per post and network. Edit campaigns, not this file.\n"
			+ YamlSerializer.Serialize(document, PostsYaml.Default.DeliveriesDocument);
	}

	internal static (string Front, string Body) SplitFrontMatter(string text)
	{
		var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal);
		if (!normalised.StartsWith("---\n", StringComparison.Ordinal))
		{
			return ("", normalised);
		}
		var end = normalised.IndexOf("\n---\n", 3, StringComparison.Ordinal);
		if (end < 0)
		{
			end = normalised.EndsWith("\n---", StringComparison.Ordinal) ? normalised.Length - 4 : throw new FormatException("The front matter has no closing ---.");
		}
		var front = normalised[4..(end + 1)];
		var body = end + 5 <= normalised.Length ? normalised[(end + 5)..] : "";
		return (front, body);
	}

	private static string WithFrontMatter(string yaml, string body) =>
		$"---\n{yaml.TrimEnd('\n')}\n---\n" + (body.Length == 0 ? "" : $"\n{body}\n");

	private static string Snake(string pascal) => string.Concat(pascal.Select((c, i) => char.IsUpper(c) && i > 0 ? $"_{char.ToLowerInvariant(c)}" : $"{char.ToLowerInvariant(c)}"));

	private static FormatException Problem(CampaignFiles folder, string message) => new($"campaigns/{folder.Slug}: {message}");

	[GeneratedRegex(@"^!\[(?<alt>[^\]]*)\]\((?<path>[^)\s]+)\)\s*$", RegexOptions.Multiline)]
	private static partial Regex Image();
}

internal sealed class CampaignMeta
{
	public string? Title { get; set; }
	public string? Project { get; set; }
	public List<string>? Networks { get; set; }
	public Dictionary<string, string>? Accounts { get; set; }
	public List<string>? Links { get; set; }
	public string? CreatedBy { get; set; }
	public bool? Held { get; set; }
	public int? RepeatMonths { get; set; }
	public string? LastRepeat { get; set; }
	public List<PointMeta>? Points { get; set; }
	public string? ApprovedBy { get; set; }
	public string? ApprovedDigest { get; set; }
	public string? ApprovedAt { get; set; }
	public string? ApprovedNotBefore { get; set; }
}

internal sealed class PointMeta
{
	public string? Text { get; set; }
	public List<WantedMeta>? Wanted { get; set; }
}

internal sealed class WantedMeta
{
	public string? Kind { get; set; }
	public string? Description { get; set; }
}

internal sealed class ArticleMeta
{
	public string? Title { get; set; }
	public string? Description { get; set; }
	public List<string>? Tags { get; set; }
	public string? Cover { get; set; }
	public string? CoverAlt { get; set; }
	public string? Slug { get; set; }
}

internal sealed class PostMeta
{
	public string? At { get; set; }
	public string? Angle { get; set; }
	public List<string>? Networks { get; set; }
	public List<int>? Points { get; set; }
}

internal sealed class JudgementDocument
{
	public string? Digest { get; set; }
	public string? Criteria { get; set; }
	public string? At { get; set; }
	public string? Model { get; set; }
	public List<FindingEntry>? Findings { get; set; }
}

internal sealed class FindingEntry
{
	public string? Criterion { get; set; }
	public string? Post { get; set; }
	public string? Network { get; set; }
	public bool Passed { get; set; }
	public bool Blocks { get; set; }
	public string? Reason { get; set; }
}

internal sealed class BriefMeta
{
	public string? Project { get; set; }
	public string? Tldr { get; set; }
	public string? Apart { get; set; }
	public string? Audience { get; set; }
	public List<string>? Avoid { get; set; }
	public List<BriefLinkEntry>? Links { get; set; }
}

internal sealed class BriefLinkEntry
{
	public string? Label { get; set; }
	public string? Url { get; set; }
}

internal sealed class CriterionMeta
{
	public string? Name { get; set; }
	public string? OnFailure { get; set; }
	public List<string>? Networks { get; set; }
}

internal sealed class DeliveriesDocument
{
	public List<DeliveryEntry>? Deliveries { get; set; }
}

internal sealed class DeliveryEntry
{
	public string? Post { get; set; }
	public string? Network { get; set; }
	public string? State { get; set; }
	public int Attempts { get; set; }
	public string? LastAttempt { get; set; }
	public string? RemoteId { get; set; }
	public string? Url { get; set; }
	public string? Error { get; set; }
	public int? Issue { get; set; }
}

[YamlSerializable(typeof(CampaignMeta))]
[YamlSerializable(typeof(PostMeta))]
[YamlSerializable(typeof(ArticleMeta))]
[YamlSerializable(typeof(DeliveriesDocument))]
[YamlSerializable(typeof(JudgementDocument))]
[YamlSerializable(typeof(CriterionMeta))]
[YamlSerializable(typeof(BriefMeta))]
[YamlSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = YamlIgnoreCondition.WhenWritingNull)]
internal sealed partial class PostsYaml : YamlSerializerContext;
