using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.Extensions.Primitives;

namespace Braggarr.Web.Settings;

/// <summary>Reads and writes <c>/config/config.json</c>, and tells the options system when it changed.</summary>
public sealed class SettingsStore : IDisposable
{
	private readonly string _path;
	private readonly IFileSystem _fs;
	private readonly Lock _lock = new();
	private CancellationTokenSource _changed = new();

	public SettingsStore(IFileSystem fs, string configDirectory)
	{
		_fs = fs;
		ConfigDirectory = configDirectory;
		_path = fs.Path.Combine(configDirectory, "config.json");
		Current = fs.File.Exists(_path) ? JsonSerializer.Deserialize(fs.File.ReadAllText(_path), SettingsJson.Default.BraggarrSettings) ?? new() : new();
	}

	public string ConfigDirectory { get; }

	public BraggarrSettings Current { get; private set; }

	public IChangeToken Watch() => new CancellationChangeToken(_changed.Token);

	public BraggarrSettings Update(Func<BraggarrSettings, BraggarrSettings> change)
	{
		CancellationTokenSource fired;
		lock (_lock)
		{
			Current = change(Current);
			_ = _fs.Directory.CreateDirectory(ConfigDirectory);
			var temporary = _path + ".tmp";
			_fs.File.WriteAllText(temporary, JsonSerializer.Serialize(Current, SettingsJson.Default.BraggarrSettings));
			_fs.File.Move(temporary, _path, overwrite: true);
			fired = _changed;
			_changed = new CancellationTokenSource();
		}
		fired.Cancel();
		fired.Dispose();
		return Current;
	}

	public void Dispose() => _changed.Dispose();

	/// <summary>Lets an options type follow this store: its <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/> is rebuilt on every change.</summary>
	public sealed class ChangeTokenSource<T>(SettingsStore store, Adapters.Config.SecretStore secrets) : Microsoft.Extensions.Options.IOptionsChangeTokenSource<T>
	{
		public string Name => Microsoft.Extensions.Options.Options.DefaultName;

		public IChangeToken GetChangeToken() => new CompositeChangeToken([store.Watch(), SecretsWatch(secrets)]);

		private static CancellationChangeToken SecretsWatch(Adapters.Config.SecretStore secrets)
		{
			var source = new CancellationTokenSource();
			void Fire()
			{
				secrets.Changed -= Fire;
				source.Cancel();
			}
			secrets.Changed += Fire;
			return new CancellationChangeToken(source.Token);
		}
	}
}
