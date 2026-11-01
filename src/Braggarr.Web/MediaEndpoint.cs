using Braggarr.Domain;
using Braggarr.Services;
using Braggarr.Web.Auth;

namespace Braggarr.Web;

/// <summary>
/// The owner's view of a campaign's images: from the clone for an approved campaign, from the branch for a proposal. Only files
/// with an image extension, only inside the campaign's folder.
/// </summary>
public static class MediaEndpoint
{
	public static void MapMedia(this WebApplication app)
	{
		// A stored video, by its content hash, for the owner to watch before approving; served from disk with ranges, so it seeks.
		_ = app.MapGet("/videos/{name}", (string name, Adapters.FileVideoStore videos) =>
			videos.Readable(name) is { } path
				? Results.Stream(videos.FileSystem.File.OpenRead(path), "video/mp4", enableRangeProcessing: true)
				: Results.NotFound()
		).RequireAuthorization(Roles.OwnerPolicy);

		_ = app.MapGet("/campaigns/{slug}/media/{file}", async (string slug, string file, ICampaignRepository repository, CampaignService campaigns,
			SchedulingService scheduling, CancellationToken ct) =>
		{
			var path = $"media/{file}";
			if (!Slugs.IsValid(slug) || MediaFile.ContentTypeOf(file) is not { } type || file.Contains("..", StringComparison.Ordinal))
			{
				return Results.NotFound();
			}
			var bytes = scheduling.Current.Campaign(slug) is not null ? await repository.ReadMediaAsync(slug, path, ct) : null;
			if (bytes is null && (await campaigns.ProposalsAsync(ct)).FirstOrDefault(p => p.Campaign.Slug == slug) is { } proposal)
			{
				bytes = await campaigns.ReadProposalMediaAsync(proposal, path, ct);
			}
			return bytes is null ? Results.NotFound() : Results.File(bytes, type);
		}).RequireAuthorization(Roles.OwnerPolicy);
	}
}
