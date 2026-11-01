using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Helpers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Braggarr.Blog;

/// <summary>An image in a post, as written: where it points and what it says to someone who cannot see it.</summary>
public sealed record ImageRef(string Url, string Alt);

/// <summary>A post's markdown as HTML, with what the strict build checks: its links and images, and how long it is.</summary>
public sealed record Rendered(string Html, ValueList<string> Links, ValueList<ImageRef> Images, int Words)
{
	/// <summary>
	/// The sections a page asked for (<c>{{ projects }}</c> alone in a paragraph), in order. Each stands in <see cref="Html"/> as its
	/// name between two NUL characters, which markdown cannot write: CommonMark reads a NUL as U+FFFD.
	/// </summary>
	public ValueList<string> Sections { get; init; } = [];

	/// <summary>The HTML cut at its sections: markup, a section's name, markup, …; always an odd count, markup first and last.</summary>
	public IReadOnlyList<string> Parts() => Html.Split('\0');

	/// <summary>Minutes to read, at 220 words a minute, never less than one.</summary>
	public int Minutes => Math.Max(1, (int)Math.Ceiling(Words / 220.0));
}

/// <summary>Markdown to HTML: tables, footnotes, task lists, heading anchors and the rest of Markdig's advanced extensions.</summary>
public static partial class MarkdownRenderer
{
	private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
		.UseAdvancedExtensions()
		.Build();

	/// <summary>The same markdown with any raw HTML in it shown as text: for showing someone else's article inside a page.</summary>
	private static readonly MarkdownPipeline Safe = new MarkdownPipelineBuilder()
		.UseAdvancedExtensions()
		.DisableHtml()
		.Build();

	/// <summary>
	/// An article as HTML for a preview, with raw HTML in the markdown escaped rather than kept: an agent's article is shown
	/// in the owner's page before anyone has approved it.
	/// </summary>
	public static string Preview(string markdown) => Markdown.ToHtml(markdown, Safe);

	/// <summary>Markdown as HTML. With <paramref name="sections"/>, as a page renders it: a paragraph that is only <c>{{ name }}</c> marks a section.</summary>
	public static Rendered Render(string markdown, bool sections = false)
	{
		var document = Markdown.Parse(markdown, Pipeline);
		var named = new List<string>();
		if (sections)
		{
			foreach (var paragraph in document.Descendants<ParagraphBlock>().ToList())
			{
				if (paragraph.Inline is { } inline && inline.All(i => i is LiteralInline) && Section().Match(PlainText(paragraph)) is { Success: true } match
					&& paragraph.Parent is { } parent)
				{
					var name = match.Groups["name"].Value;
					var lines = new StringLineGroup(1) { new StringSlice($"\0{name}\0") };
					var block = new HtmlBlock(null) { Type = HtmlBlockType.NonInterruptingBlock, Lines = lines };
					parent[parent.IndexOf(paragraph)] = block;
					named.Add(name);
				}
			}
		}
		var links = new List<string>();
		var images = new List<ImageRef>();
		foreach (var link in document.Descendants<LinkInline>())
		{
			if (link.Url is not { } url)
			{
				continue;
			}
			if (link.IsImage)
			{
				images.Add(new ImageRef(url, PlainText(link)));
			}
			else
			{
				links.Add(url);
			}
		}
		foreach (var heading in document.Descendants<HeadingBlock>())
		{
			// An anchor readers can link to and see: the heading's own id, which the auto-identifier extension set.
			if (heading.GetAttributes().Id is { } id && heading.Inline is { } inline)
			{
				inline.AppendChild(new HtmlInline($"<a class=\"anchor\" href=\"#{id}\" aria-hidden=\"true\">#</a>"));
			}
		}
		var html = document.ToHtml(Pipeline);
		var words = PlainText(document).Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
		return new Rendered(html, [.. links], [.. images], words) { Sections = [.. named] };
	}

	[GeneratedRegex(@"^\{\{\s*(?<name>[a-z]+)\s*\}\}$")]
	private static partial Regex Section();

	/// <summary>The first paragraph as plain text, cut at a word to <paramref name="length"/> characters: a post's description when it has none.</summary>
	public static string Excerpt(string markdown, int length = BlogPostRules.DescriptionLength)
	{
		var document = Markdown.Parse(markdown, Pipeline);
		var first = document.Descendants<ParagraphBlock>().Select(PlainText).FirstOrDefault(t => t.Length > 0 && !Section().IsMatch(t)) ?? "";
		if (first.Length <= length)
		{
			return first;
		}
		var cut = first[..(length - 1)];
		var space = cut.LastIndexOf(' ');
		return (space > length / 2 ? cut[..space] : cut).TrimEnd(',', '.', ';', ':', ' ') + "…";
	}

	/// <summary>The words of a node: its literals and inline code, block by block, and the lines of its code blocks.</summary>
	private static string PlainText(MarkdownObject node)
	{
		var text = new StringBuilder();
		var blocks = node switch
		{
			LeafBlock leaf => [leaf],
			ContainerBlock container => container.Descendants<LeafBlock>(),
			_ => []
		};
		if (node is Inline inline)
		{
			Words(inline);
		}
		foreach (var block in blocks)
		{
			if (block is CodeBlock)
			{
				text.Append(' ').Append(block.Lines.ToString());
			}
			else if (block.Inline is { } inlines)
			{
				Words(inlines);
			}
			text.Append(' ');
		}
		return string.Join(' ', text.ToString().Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

		void Words(Inline from)
		{
			foreach (var part in from is ContainerInline container ? container.Descendants<Inline>() : [from])
			{
				switch (part)
				{
					case LiteralInline literal:
						text.Append(literal.Content.ToString());
						break;
					case CodeInline code:
						text.Append(code.Content);
						break;
					case LineBreakInline:
						text.Append(' ');
						break;
				}
			}
		}
	}
}
