namespace Braggarr.Services;

public sealed class SchedulingOptions
{
	/// <summary>How often the posts repo is fetched. Edits pushed from an editor show up within this time.</summary>
	public TimeSpan SyncInterval { get; set; } = TimeSpan.FromMinutes(5);

	/// <summary>How long one network call may take before it counts as failed.</summary>
	public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(60);

	/// <summary>The same for a post with a video: uploading it and waiting for the network to process it takes minutes.</summary>
	public TimeSpan VideoPublishTimeout { get; set; } = TimeSpan.FromMinutes(15);
}
