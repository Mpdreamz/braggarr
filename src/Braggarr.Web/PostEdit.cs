using Braggarr.Domain;

namespace Braggarr.Web;

/// <summary>What the post editor needs: the campaign and post, the page and route back, and which networks already went out.</summary>
public sealed record PostEdit(Campaign Campaign, Post Post, Clock Clock, string Page, IDictionary<string, string> Route, IReadOnlySet<Network> Locked);
