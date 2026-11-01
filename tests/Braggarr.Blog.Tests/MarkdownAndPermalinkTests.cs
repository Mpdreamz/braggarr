using AwesomeAssertions;
using CsCheck;

namespace Braggarr.Blog.Tests;

public class MarkdownAndPermalinkTests
{
	[Test]
	public void Markdown_gets_tables_footnotes_and_headings_readers_can_link_to()
	{
		var rendered = MarkdownRenderer.Render("## Why it matters\n\n| a |\n|---|\n| 1 |\n\nNote.[^1]\n\n[^1]: Here.\n\n![A diff](diff.png) [about](/about/)");

		rendered.Html.Should().Contain("<h2 id=\"why-it-matters\">Why it matters<a class=\"anchor\" href=\"#why-it-matters\" aria-hidden=\"true\">#</a></h2>");
		rendered.Html.Should().Contain("<table>");
		rendered.Html.Should().Contain("class=\"footnote-ref\"");
		rendered.Images.Should().Equal(new ImageRef("diff.png", "A diff"));
		rendered.Links.Should().Contain("/about/");
	}

	[Test]
	public void Reading_time_counts_words_in_paragraphs_and_code_and_is_never_zero()
	{
		MarkdownRenderer.Render("").Minutes.Should().Be(1);
		MarkdownRenderer.Render(string.Join(' ', Enumerable.Repeat("word", 221))).Minutes.Should().Be(2);
		MarkdownRenderer.Render("One two.\n\nThree.").Words.Should().Be(3);
		MarkdownRenderer.Render("```\nvar x = 1;\n```").Words.Should().Be(4);
	}

	[Test]
	public void An_excerpt_is_the_first_paragraph_cut_at_a_word()
	{
		MarkdownRenderer.Excerpt("## Heading\n\nFirst *paragraph* with `code`.\n\nSecond.").Should().Be("First paragraph with code.");
		var cut = MarkdownRenderer.Excerpt(string.Join(' ', Enumerable.Repeat("words", 60)));
		cut.Length.Should().BeLessThanOrEqualTo(BlogPostRules.DescriptionLength);
		cut.Should().EndWith("words…");
	}

	[Test]
	public void A_permalink_fills_its_tokens_and_keeps_a_moved_slug_to_the_character()
	{
		var date = new DateTimeOffset(2013, 3, 7, 22, 0, 0, TimeSpan.FromHours(1));

		new Permalink("/{year}/{month}/{day}/{slug}/").For("curb", date).Should().Be("/2013/03/07/curb/");
		new Permalink("/articles/{slug}/").For("oss-development,-a-continuous-lesson-in-humility-2013-03-07", date)
			.Should().Be("/articles/oss-development,-a-continuous-lesson-in-humility-2013-03-07/");
		new Permalink("/{category}/{slug}/").Named().Should().Equal("category", "slug");
	}

	[Test]
	public void Posts_with_different_slugs_never_share_an_address() =>
		Gen.Select(Gens.Permalinks, Gen.OneOfConst(Gens.Slugs), Gen.OneOfConst(Gens.Slugs), Gen.Int[-400, 400], Gen.Int[-400, 400]).Sample((permalink, a, b, dayA, dayB) =>
		{
			var addressA = permalink.For(a, Gens.Start.AddDays(dayA));
			var addressB = permalink.For(b, Gens.Start.AddDays(dayB));

			if (addressA == addressB)
			{
				a.Should().Be(b, $"{permalink} gave {addressA} to both");
			}
		});

	[Test]
	public void A_generated_slug_is_safe_and_stable() =>
		Gen.String.Sample(title =>
		{
			var slug = Segments.Slug(title);

			slug.Should().MatchRegex("^([a-z0-9]+(-[a-z0-9]+)*)?$");
			Segments.Slug(slug).Should().Be(slug);
			(slug.Length == 0 || Segments.IsSafe(slug)).Should().BeTrue();
		});

	[Test]
	public void Site_paths_refuse_what_would_leave_the_site()
	{
		Segments.IsSitePath("/about/").Should().BeTrue();
		Segments.IsSitePath("/about-me.html").Should().BeTrue();
		Segments.IsSitePath("about/").Should().BeFalse();
		Segments.IsSitePath("/a/../b/").Should().BeFalse();
		Segments.IsSitePath("/.git/config").Should().BeFalse();
		Segments.IsSitePath("/a?b").Should().BeFalse();
		Segments.IsSitePath("/a//b").Should().BeFalse();
		Segments.IsSafe("the-joys-of-.net").Should().BeTrue();
		Segments.IsSafe("..").Should().BeFalse();
		Segments.IsSafe("a/b").Should().BeFalse();
	}

	[Test]
	public void A_preview_shows_raw_html_as_text()
	{
		var html = MarkdownRenderer.Preview("Text <script src=\"https://evil.example/x.js\"></script> <form action=\"/Settings\"><button>Go</button></form>\n\n**bold**");

		html.Should().NotContain("<script").And.NotContain("<form").And.Contain("&lt;script").And.Contain("<strong>bold</strong>");
	}
}
