using System.IO.Abstractions;
using Nullean.Entities;
using Nullean.Entities.FileSystem;
using Nullean.ScopedFileSystem;

namespace Braggarr.Adapters;

/// <summary>
/// Everything Braggarr reads and writes on disk is under <c>/config</c>, through one <see cref="ScopedFileSystem"/>: a path that
/// resolves outside it, passes through a symbolic link, or touches a hidden file or folder is refused before any byte moves.
/// Git's own <c>.git</c> folder is the one hidden name allowed, because the posts clone has one.
/// </summary>
public static class ConfigFileSystem
{
	public static IFileSystem Create(string configDirectory, IFileSystem? inner = null) =>
		new ScopedFileSystem(
			inner ?? new FileSystem(),
			new ScopedFileSystemOptions((inner ?? new FileSystem()).Path.GetFullPath(configDirectory)) { AllowedHiddenFolderNames = [".git"] }
		);
}

/// <summary>Files in one campaign's folder of the posts clone, as a post or the owner's media names them.</summary>
public sealed record CampaignFolder(IDirectoryInfo Root, IReadOnlyList<IFileInfo> Files)
{
	public static CampaignFolder Of(IFileSystem fs, string folder, params string[] relative) =>
		new(fs.DirectoryInfo.New(folder), [.. relative.Select(r => fs.FileInfo.New(fs.Path.Combine(folder, r)))]);
}

/// <summary>A path from a post file, a delivery or an edit names a file in its campaign's folder, and nothing else.</summary>
[Constraints<CampaignFolder>]
public static partial class CampaignFolderRules
{
	public static readonly Constraint<CampaignFolder> StayInTheCampaign = Files<CampaignFolder>.StayInside(f => f.Root, f => f.Files);

	public static readonly Constraint<CampaignFolder> NoLinks = Files<CampaignFolder>.NotSymlink(f => f.Files, f => f.Root);

	public static readonly Constraint<CampaignFolder> NoHiddenFolders = Files<CampaignFolder>.NoHiddenSegments(f => f.Root, f => f.Files);

	public static readonly Constraint<CampaignFolder> KnownKinds =
		Files<CampaignFolder>.HaveExtension(f => f.Files, [".md", ".yml", ".png", ".jpg", ".jpeg", ".webp", ".gif"]);
}

/// <summary>The videos kept on the server: one folder of MP4s named by their content hash.</summary>
public sealed record VideoLibrary(IDirectoryInfo Root, IReadOnlyList<IFileInfo> Videos);

[Constraints<VideoLibrary>]
public static partial class VideoLibraryRules
{
	public static readonly Constraint<VideoLibrary> StayInTheLibrary = Files<VideoLibrary>.StayInside(l => l.Root, l => l.Videos);

	public static readonly Constraint<VideoLibrary> NoLinks = Files<VideoLibrary>.NotSymlink(l => l.Videos, l => l.Root);

	public static readonly Constraint<VideoLibrary> NoHiddenFolders = Files<VideoLibrary>.NoHiddenSegments(l => l.Root, l => l.Videos);

	public static readonly Constraint<VideoLibrary> OnlyMp4 = Files<VideoLibrary>.HaveExtension(l => l.Videos, [".mp4"]);

	public static readonly Constraint<VideoLibrary> NoLargerThanAnUpload = Files<VideoLibrary>.MaxSize(l => l.Videos, Services.CampaignService.MaxVideoBytes);
}
