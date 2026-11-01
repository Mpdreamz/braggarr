using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Braggarr.Adapters.Config;

/// <summary>
/// Credentials, encrypted at rest with ASP.NET Data Protection (keys in <c>/config/keys</c>) in one file, <c>/config/secrets.json</c>.
/// Never in the posts repo. Values are read whole and written whole; there are only a handful.
/// </summary>
public sealed class SecretStore(IDataProtectionProvider protection, IFileSystem fs, string path)
{
	private readonly IDataProtector _protector = protection.CreateProtector("Braggarr.Secrets.v1");
	private readonly Lock _lock = new();
	private Dictionary<string, string>? _cache;

	public event Action? Changed;

	public string? Get(string name)
	{
		lock (_lock)
		{
			return Load().GetValueOrDefault(name);
		}
	}

	public T? Get<T>(string name) where T : class => Get(name) is { } json ? JsonSerializer.Deserialize(json, StoredJson.Info<T>()) : null;

	public void Set(string name, string? value)
	{
		lock (_lock)
		{
			var all = Load();
			if (value is null)
			{
				_ = all.Remove(name);
			}
			else
			{
				all[name] = value;
			}
			Save(all);
		}
		Changed?.Invoke();
	}

	public void Set<T>(string name, T? value) where T : class => Set(name, value is null ? null : JsonSerializer.Serialize(value, StoredJson.Info<T>()));

	/// <summary>The names of every secret, for what is kept under a prefix (the accounts).</summary>
	public IReadOnlyCollection<string> Names()
	{
		lock (_lock)
		{
			return [.. Load().Keys];
		}
	}

	/// <summary>Every secret value, so the log filter can redact them wherever they appear.</summary>
	public IReadOnlyCollection<string> Values()
	{
		lock (_lock)
		{
			return [.. Load().Values];
		}
	}

	private Dictionary<string, string> Load()
	{
		if (_cache is not null)
		{
			return new(_cache);
		}
		_cache = fs.File.Exists(path) ? JsonSerializer.Deserialize(_protector.Unprotect(fs.File.ReadAllText(path)), StoredJson.Default.DictionaryStringString) ?? [] : [];
		return new(_cache);
	}

	private void Save(Dictionary<string, string> all)
	{
		_ = fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(path)!);
		var temporary = path + ".tmp";
		fs.File.WriteAllText(temporary, _protector.Protect(JsonSerializer.Serialize(all, StoredJson.Default.DictionaryStringString)));
		if (!OperatingSystem.IsWindows())
		{
			fs.File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
		fs.File.Move(temporary, path, overwrite: true);
		_cache = all;
	}
}
