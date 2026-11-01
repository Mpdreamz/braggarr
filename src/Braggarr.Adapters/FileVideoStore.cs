using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Adapters;

/// <summary>
/// Videos in a folder on the server (<c>/config/media</c>), each named <c>sha256-&lt;hash&gt;.mp4</c> after its bytes. A file is
/// written under a temporary name and moved into place, so a half-written video is never read. Nothing is ever deleted here:
/// a campaign that is gone may still be in git history, pointing at its video. Every read is held to
/// <see cref="VideoLibraryRules"/>: inside the folder, no link, no hidden folder, an MP4, no larger than an upload.
/// </summary>
public sealed partial class FileVideoStore(IFileSystem fs, string directory) : IVideoStore
{
	public string Directory => directory;

	public IFileSystem FileSystem => fs;

	public async Task<MediaRef> SaveAsync(byte[] bytes, string alt, CancellationToken ct)
	{
		var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
		var reference = MediaRef.Video(hash, alt);
		var path = PathOf(reference.Path)!;
		if (!fs.File.Exists(path))
		{
			_ = fs.Directory.CreateDirectory(directory);
			var temporary = $"{path}.{Guid.NewGuid():N}.partial";
			await fs.File.WriteAllBytesAsync(temporary, bytes, ct);
			fs.File.Move(temporary, path, overwrite: true);
		}
		return reference;
	}

	public async Task<byte[]?> ReadAsync(string path, CancellationToken ct) =>
		Readable(path) is { } file ? await fs.File.ReadAllBytesAsync(file, ct) : null;

	/// <summary>The file for a reference, when it exists and the library's rules allow reading it; null otherwise.</summary>
	public string? Readable(string reference) =>
		PathOf(reference) is { } file
		&& fs.File.Exists(file)
		&& VideoLibraryRules.All.Validate(new VideoLibrary(fs.DirectoryInfo.New(directory), [fs.FileInfo.New(file)])).Count == 0
			? file
			: null;

	/// <summary>The file for a <c>video:sha256-….mp4</c> reference (or its bare name); null for anything else, so no path escapes the folder.</summary>
	public string? PathOf(string reference)
	{
		var name = reference.StartsWith(MediaRef.VideoPrefix, StringComparison.Ordinal) ? reference[MediaRef.VideoPrefix.Length..] : reference;
		return Name().IsMatch(name) ? fs.Path.Combine(directory, name) : null;
	}

	[GeneratedRegex("^sha256-[0-9a-f]{64}\\.mp4$")]
	private static partial Regex Name();
}
