using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Web.Ui;

/// <summary>A post's talking points and angle, under its heading.</summary>
public sealed record PostAboutModel(Campaign Campaign, Post Post);

/// <summary>The accounts a campaign posts as: a form to change them on a proposal, else a line.</summary>
public sealed record PostingAsModel(Campaign Campaign, Func<Network, IReadOnlyList<string>>? Choices, int? Proposal);

/// <summary>One image or video of a variant, or the slot a talking point wants filled.</summary>
public sealed record MediaModel(string Slug, MediaRef Media);

/// <summary>
/// A blog post as it will read: its title, description, tags and cover, and its markdown as HTML, raw HTML escaped, its images
/// served from the campaign.
/// </summary>
public sealed record ArticleModel(string Slug, Variant Variant)
{
	public Microsoft.AspNetCore.Html.HtmlString Body =>
		new(Braggarr.Blog.MarkdownRenderer.Preview(Variant.Text).Replace("src=\"media/", $"src=\"/campaigns/{Slug}/media/", StringComparison.Ordinal));
}

/// <summary>What the judge found, and whether it still holds.</summary>
public sealed record JudgementModel(Campaign Campaign, JudgeState Judge, Clock Clock);
