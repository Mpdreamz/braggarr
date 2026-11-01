namespace Braggarr.Web.Ui;

/// <summary>What a page tells the layout: its title, and whether it fills the toolbar (its slice's <c>Toolbar</c> section).</summary>
public sealed record LayoutModel(string? Title, bool Toolbar = false);
