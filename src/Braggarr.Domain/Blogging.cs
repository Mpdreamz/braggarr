namespace Braggarr.Domain;

/// <summary>Whether a project's repository can be seen by anyone, as GitHub says.</summary>
public enum ProjectVisibility
{
	Public,
	Private,

	/// <summary>GitHub could not say: not found, or not readable with Braggarr's access.</summary>
	Unknown
}

/// <summary>
/// What is particular to the blog: its article's limits, the placeholder other posts link it with, and the one rule no campaign
/// can break by itself, that only public projects get a blog post.
/// </summary>
public static partial class Blogging
{
	/// <summary>Where another post links the campaign's blog post; it becomes the post's address once the post is live.</summary>
	public const string Placeholder = "{blog}";

	/// <summary>
	/// What the placeholder counts as on a network until the address is known: a 60-character link. X and Mastodon count any
	/// link as 23.
	/// </summary>
	public static readonly string MeasuredAs = "https://" + new string('x', 52);

	/// <summary>How long after the blog post a post that links it is planned at the earliest: the site's build, with room to spare.</summary>
	public static readonly TimeSpan DeployMargin = TimeSpan.FromMinutes(30);

	/// <summary>How long a committed post may take to answer at its address before the owner is asked to look.</summary>
	public static readonly TimeSpan LiveWithin = TimeSpan.FromHours(1);

	public const int TitleLimit = 100;

	public const int DescriptionLimit = 160;

	/// <summary>The cover and the images in the article.</summary>
	public const int ImageLimit = 20;

	/// <summary>
	/// The images an article's markdown shows, in the campaign's media folder, each once: a blog variant's media, so they are
	/// checked and copied beside it. Always read from the text, so an edit to the text is an edit to them.
	/// </summary>
	public static ValueList<MediaRef> ImagesIn(string markdown) =>
		[.. InlineImage().Matches(markdown).Select(m => new MediaRef(m.Groups["path"].Value, m.Groups["alt"].Value)).Distinct()];

	[System.Text.RegularExpressions.GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\((?<path>media/[^)\s]+)\)")]
	private static partial System.Text.RegularExpressions.Regex InlineImage();

	/// <summary>A variant on another network that links the blog post.</summary>
	public static bool LinksToBlog(this Variant variant) =>
		variant.Network != Network.Blog && variant.Text.Contains(Placeholder, StringComparison.Ordinal);

	/// <summary>
	/// A campaign may go to the blog only for a project anyone can see: a blog post is public and stays up, and Braggarr never
	/// mentions a private project. A campaign that does not go to the blog may be about anything the owner chooses.
	/// </summary>
	public static bool MayBlog(this Campaign campaign, ProjectVisibility visibility) =>
		!campaign.Networks.Contains(Network.Blog) || visibility == ProjectVisibility.Public;
}
