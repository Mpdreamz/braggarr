using System.IO.Abstractions;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Braggarr.Adapters.Config;

/// <summary>
/// The Data Protection keys in <c>/config/keys</c>, read and written through the scoped file system like everything else. The
/// files are what ASP.NET's own file repository writes (<c>key-&lt;id&gt;.xml</c>), so keys written before stay readable.
/// </summary>
public sealed class KeyRepository(IFileSystem fs, string directory) : IXmlRepository
{
	public IReadOnlyCollection<XElement> GetAllElements() =>
		fs.Directory.Exists(directory)
			? [.. fs.Directory.GetFiles(directory, "*.xml").Order(StringComparer.Ordinal).Select(f => XDocument.Parse(fs.File.ReadAllText(f)).Root!)]
			: [];

	public void StoreElement(XElement element, string friendlyName)
	{
		_ = fs.Directory.CreateDirectory(directory);
		var name = string.Concat(friendlyName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
		var path = fs.Path.Combine(directory, $"{(name.Length > 0 ? name : Guid.NewGuid().ToString("N"))}.xml");
		var temporary = path + ".tmp";
		fs.File.WriteAllText(temporary, element.ToString(SaveOptions.DisableFormatting));
		fs.File.Move(temporary, path, overwrite: true);
	}
}
