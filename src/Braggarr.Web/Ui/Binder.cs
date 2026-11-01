using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace Braggarr.Web.Ui;

/// <summary>
/// A request's values, read as Razor Pages' model binding read them, which the pages were written against:
/// <list type="bullet">
/// <item>A value is looked for in the form first, then the route, then the query string. The form's is read in the current
/// culture, the others in the invariant one.</item>
/// <item>The first value counts. An empty or blank value is null; a value that does not convert is the type's default.</item>
/// <item>A number may be written in hex (<c>0x1F</c>, <c>#1F</c>), as the type converter allowed; an enum by name in any case, or
/// by a number it defines.</item>
/// <item>A list takes every value of its name that converts; a dictionary takes <c>name[key]</c>, and is null when a key does
/// not convert. With no value at all, both are empty, not null.</item>
/// </list>
/// </summary>
public sealed class Binder(HttpContext http, IFormCollection? form)
{
	private bool Find(string name, out StringValues values, out CultureInfo culture)
	{
		if (form is not null && form.TryGetValue(name, out values))
		{
			culture = CultureInfo.CurrentCulture;
			return true;
		}
		culture = CultureInfo.InvariantCulture;
		if (http.Request.RouteValues.TryGetValue(name, out var route) && route is not null)
		{
			values = Convert.ToString(route, CultureInfo.InvariantCulture);
			return true;
		}
		return http.Request.Query.TryGetValue(name, out values);
	}

	/// <summary>Whether the request carries <paramref name="name"/> at all: a bound property keeps its value when it does not.</summary>
	public bool Has(string name) => Find(name, out _, out _);

	/// <summary>The value as posted, unconverted: what a form shows again in a field it was bound to.</summary>
	public string? Raw(string name) => Find(name, out var values, out _) ? values.ToString() : null;

	public string? Text(string name) => Find(name, out var values, out _) ? Blank(values) : null;

	public bool Bool(string name) => Find(name, out var values, out _) && Blank(values) is { } value && bool.TryParse(value.Trim(), out var b) && b;

	public int Number(string name) => Find(name, out var values, out var culture) && Blank(values) is { } value && Parse(value, culture) is { } n ? n : 0;

	public int? OptionalNumber(string name) => Find(name, out var values, out var culture) && Blank(values) is { } value ? Parse(value, culture) : null;

	public DateTime DateTime(string name) =>
		Find(name, out var values, out var culture) && Blank(values) is { } value && global::System.DateTime.TryParse(value, culture, DateTimeStyles.None, out var at) ? at : default;

	public T Enum<T>(string name) where T : struct, Enum => Find(name, out var values, out _) && Blank(values) is { } value ? Defined<T>(value) ?? default : default;

	public List<T> EnumList<T>(string name) where T : struct, Enum =>
		Find(name, out var values, out _) ? [.. values.Select(v => Blank(v) is { } value ? Defined<T>(value) : null).OfType<T>()] : [];

	/// <summary>Every value of <paramref name="name"/>; a blank one is null, as it was.</summary>
	public List<string> StringList(string name) => Find(name, out var values, out _) ? [.. values.Select(v => Blank(v)!)] : [];

	/// <summary>The values posted as <c>name[key]</c>, by key; null when a key names no <typeparamref name="TKey"/>.</summary>
	public Dictionary<TKey, string>? Dictionary<TKey>(string name) where TKey : struct, Enum
	{
		var found = new Dictionary<TKey, string>();
		var prefix = name + "[";
		foreach (var (key, values) in form ?? (IEnumerable<KeyValuePair<string, StringValues>>)http.Request.Query)
		{
			if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key.EndsWith(']'))
			{
				if (!System.Enum.TryParse<TKey>(key[prefix.Length..^1], ignoreCase: true, out var k))
				{
					return null;
				}
				found[k] = Blank(values)!;
			}
		}
		return found;
	}

	/// <summary>The file posted as <paramref name="name"/>; an empty file input, which a browser sends with no file name, is none.</summary>
	public IFormFile? File(string name) =>
		form?.Files.FirstOrDefault(f => !(f.Length == 0 && string.IsNullOrEmpty(f.FileName)) && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

	private static string? Blank(StringValues values) => values.Count > 0 && !string.IsNullOrWhiteSpace(values[0]) ? values[0] : null;

	private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

	private static int? Parse(string value, CultureInfo culture)
	{
		var text = value.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.StartsWith('#'))
		{
			return int.TryParse(text[(text[0] == '#' ? 1 : 2)..], NumberStyles.HexNumber, culture, out var hex) ? hex : null;
		}
		return int.TryParse(text, NumberStyles.Integer, culture, out var n) ? n : null;
	}

	private static T? Defined<T>(string value) where T : struct, Enum =>
		System.Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && System.Enum.IsDefined(parsed) ? parsed : null;
}
