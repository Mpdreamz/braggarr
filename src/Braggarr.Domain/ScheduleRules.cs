using Nullean.Entities;

namespace Braggarr.Domain;

[Constraints<Schedule>]
public static partial class ScheduleRules
{
	public static readonly Constraint<Schedule> OneDeliveryPerPostAndNetwork = Constraints<Schedule>.Always(
		s => s.Deliveries.Select(d => d.Key).Distinct().Count() == s.Deliveries.Count,
		"Each post goes to each network through exactly one delivery."
	);

	public static readonly Constraint<Schedule> EveryApprovedPostIsOwed = Constraints<Schedule>.Always(
		s => Schedule.Owed(s.Campaigns).All(k => s.Find(k) is not null),
		"Every post of an approved campaign has a delivery for every one of its networks."
	);

	/// <summary>
	/// A delivery that failed three times waits for the owner: nothing the schedule does brings it back. Said as a count that never
	/// goes down, so the monotonicity axis attacks it with every operation (a hand-written transition rule would only be checked).
	/// </summary>
	public static readonly Constraint<Schedule> RetriesStopAfterThreeAttempts = Constraints<Schedule>.Monotonic(
		s => s.Deliveries.Count(d => d.State == DeliveryState.Failed && d.Attempts >= Delivery.MaxAttempts),
		MonotonicDirection.NonDecreasing
	);

	/// <summary>
	/// Whatever happened to a delivery stays on record, a sync that drops its post included: what was published, what failed,
	/// what the owner was asked about. Said as a count that never goes down, so every operation is attacked with it.
	/// </summary>
	public static readonly Constraint<Schedule> NothingThatHappenedIsForgotten = Constraints<Schedule>.Monotonic(
		s => s.WithHistory,
		MonotonicDirection.NonDecreasing
	);

	/// <summary>
	/// A delivery that may or may not have gone out, or a blog post whose site never answered, is the owner's to settle: nothing
	/// the schedule does settles it. Said as a count that never goes down, so every operation is attacked with it.
	/// </summary>
	public static readonly Constraint<Schedule> UnknownWaitsForTheOwner = Constraints<Schedule>.Monotonic(
		s => s.Deliveries.Count(d => d.State == DeliveryState.Unknown),
		MonotonicDirection.NonDecreasing
	);

	public static readonly Constraint<Schedule> AttemptsNeverGoBackForAnyDelivery = Constraints<Schedule>.MonotonicPerKey(
		s => s.Deliveries,
		d => d.Key,
		d => d.Attempts,
		MonotonicDirection.NonDecreasing,
		describe: (key, before, after) => $"{key} went from {before} attempts to {after}: an attempt, once made, is never forgotten."
	);

	/// <summary>
	/// A post that links the campaign's blog post is only ever sent once the blog post is live, so its link never leads to a page
	/// that is not there yet.
	/// </summary>
	public static readonly Constraint<Schedule> LinksToTheBlogWaitForIt = Constraints<Schedule>.Always(
		s => s.Deliveries.All(d =>
			d.State == DeliveryState.Pending
			|| s.Variant(d.Key) is not { } variant || !variant.LinksToBlog()
			|| (s.Campaign(d.Key.Campaign) is { } campaign && s.BlogPost(campaign) is { State: DeliveryState.Published })),
		"A post that links {blog} is sent only once the blog post answers at its address."
	);

	public static readonly Constraint<Schedule> RecoveringTwiceIsRecoveringOnce = Constraints<Schedule>.Idempotent(s => s.Recover());

	/// <summary>Retrying is a decision about now: asked twice at the same time, it decides the same.</summary>
	public static readonly Constraint<Schedule> RetryingTwiceIsRetryingOnce = Constraints<Schedule>.Idempotent(s => s.Retry(DateTimeOffset.MaxValue));

	public static readonly Constraint<Schedule> SyncIgnoresCampaignOrder = Constraints<Schedule>.OrderIndependent(
		s => s.Campaigns,
		(s, campaigns) => s.Sync(campaigns),
		s => s.Deliveries
	);
}
