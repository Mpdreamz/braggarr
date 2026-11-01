using Braggarr.Domain;
using Microsoft.AspNetCore.Html;

namespace Braggarr.Web;

/// <summary>
/// Each network's own mark, from the sprite in the layout (<c>#n-&lt;slug&gt;</c>), drawn in the text's colour. The marks are
/// Simple Icons' (CC0); LinkedIn's is drawn here, since Simple Icons does not carry it.
/// </summary>
public static class NetworkIcons
{
	public static string Name(Network network) => network == Network.HackerNews ? "Hacker News" : network.ToString();

	// Slugs and names are fixed strings of ours, never user input, so they need no encoding.
	public static IHtmlContent Icon(Network network) =>
		new HtmlString($"<svg class=\"net\" aria-hidden=\"true\"><use href=\"#n-{network.Slug()}\" /></svg>");

	public static IHtmlContent Named(Network network) =>
		new HtmlString($"<span class=\"net-name\"><svg class=\"net\" aria-hidden=\"true\"><use href=\"#n-{network.Slug()}\" /></svg>{Name(network)}</span>");
}
