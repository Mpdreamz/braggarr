using Nullean.Entities;

namespace Braggarr.Domain;

/// <summary>Which diagram a delivery follows, by its network.</summary>
public enum DeliveryKind
{
	/// <summary>A network Braggarr posts to: Bluesky, Mastodon, LinkedIn, Threads, X, YouTube.</summary>
	Automatic,

	/// <summary>A network the owner posts to by hand: Reddit, Hacker News.</summary>
	Manual,

	/// <summary>The owner's blog: committed to its site's repository, live once the site answers.</summary>
	Blog
}

/// <summary>
/// The lifecycle of a delivery, as three diagrams: one for the networks Braggarr posts to, one for the networks the owner posts
/// to by hand, one for the blog. <see cref="DeliveryRules.Lifecycle"/> allows exactly each kind's edges, so the rule cannot drift
/// from the diagrams, and no kind borrows another's edge.
/// </summary>
public static class DeliveryLifecycle
{
	public static readonly StateMachine<DeliveryState, string> Automatic = StateMachine
		.Define<DeliveryState, string>(DeliveryState.Pending)
		.Permit("Begin", DeliveryState.Pending, DeliveryState.Sending)
		.Permit("Succeed", DeliveryState.Sending, DeliveryState.Published)
		.Permit("Fail", DeliveryState.Sending, DeliveryState.Failed)
		.Permit("Retry", DeliveryState.Failed, DeliveryState.Pending)
		.Permit("Recover", DeliveryState.Sending, DeliveryState.Unknown)
		.Permit("ConfirmPublished", DeliveryState.Unknown, DeliveryState.Published)
		.Permit("ConfirmMissing", DeliveryState.Unknown, DeliveryState.Pending)
		.Build();

	public static readonly StateMachine<DeliveryState, string> Manual = StateMachine
		.Define<DeliveryState, string>(DeliveryState.Pending)
		.Permit("OpenManual", DeliveryState.Pending, DeliveryState.ManualDue)
		.Permit("CloseManual", DeliveryState.ManualDue, DeliveryState.ManualDone)
		.Build();

	/// <summary>
	/// A blog post is committed, then live once its site answers. Committing is safe to repeat (the file is found already there),
	/// so an interrupted send goes back to waiting; a site that never answers is the owner's to look at.
	/// </summary>
	public static readonly StateMachine<DeliveryState, string> Blog = StateMachine
		.Define<DeliveryState, string>(DeliveryState.Pending)
		.Permit("Begin", DeliveryState.Pending, DeliveryState.Sending)
		.Permit("Commit", DeliveryState.Sending, DeliveryState.Committed)
		.Permit("Fail", DeliveryState.Sending, DeliveryState.Failed)
		.Permit("Retry", DeliveryState.Failed, DeliveryState.Pending)
		.Permit("Recover", DeliveryState.Sending, DeliveryState.Pending)
		.Permit("GoLive", DeliveryState.Committed, DeliveryState.Published)
		.Permit("TimeOut", DeliveryState.Committed, DeliveryState.Unknown)
		.Permit("ConfirmPublished", DeliveryState.Unknown, DeliveryState.Published)
		.Permit("ConfirmMissing", DeliveryState.Unknown, DeliveryState.Pending)
		.Build();

	public static DeliveryKind KindOf(Network network) =>
		network.IsManual() ? DeliveryKind.Manual : network == Network.Blog ? DeliveryKind.Blog : DeliveryKind.Automatic;

	public static StateMachine<DeliveryState, string> For(DeliveryKind kind) => kind switch
	{
		DeliveryKind.Manual => Manual,
		DeliveryKind.Blog => Blog,
		_ => Automatic
	};

	public static StateMachine<DeliveryState, string> For(Network network) => For(KindOf(network));
}

[Constraints<Delivery>]
public static partial class DeliveryRules
{
	/// <summary>
	/// The edges of the three diagrams, as an allow-list the axes attack and whose every edge must be taken. Each edge is allowed
	/// for its own kind only, so a blog post's way back from sending to waiting is never one a Bluesky post may take, and no
	/// kind reaches another's states: no Reddit post is sent, no Bluesky post waits in an issue or is committed.
	/// </summary>
	public static readonly Constraint<Delivery> Lifecycle = Enum.GetValues<DeliveryKind>()
		.SelectMany(kind => DeliveryLifecycle.For(kind).Transitions.Select(t => (Kind: kind, t.From, t.To)))
		.Aggregate(
			Constraints<Delivery>.Transitions(d => (DeliveryLifecycle.KindOf(d.Key.Network), d.State)),
			(rule, edge) => rule.Allow((edge.Kind, edge.From), (edge.Kind, edge.To))
		);

	public static readonly Constraint<Delivery> KeyNeverChanges = Constraints<Delivery>.Conserves(d => d.Key, "A delivery is always for the same post and network.");

	public static readonly Constraint<Delivery> AttemptsNeverGoBack = Constraints<Delivery>.Monotonic(d => d.Attempts, MonotonicDirection.NonDecreasing);

	public static readonly Constraint<Delivery> PublishedHasAReceipt = Constraints<Delivery>
		.When(d => d.State is DeliveryState.Published or DeliveryState.Committed)
		.Requires(d => d.Receipt is not null, "A published delivery records where it was published; a committed blog post, where it will be.");

	public static readonly Constraint<Delivery> OnlyPublishedHasAReceipt = Constraints<Delivery>.Always(
		d => d.Receipt is null || d.State is DeliveryState.Published or DeliveryState.Committed,
		"Only a published delivery, or a committed blog post, has a receipt: one in any other state would claim a post that is not known to be there."
	);

	public static readonly Constraint<Delivery> AnAttemptIsCounted = Constraints<Delivery>
		.When(d => !d.IsManual && d.State is DeliveryState.Sending or DeliveryState.Published or DeliveryState.Failed or DeliveryState.Unknown or DeliveryState.Committed)
		.Requires(d => d.Attempts >= 1 && d.LastAttempt is not null, "A delivery that was sent counts the attempt and records when it began.");

	public static readonly Constraint<Delivery> AFailureSaysWhy = Constraints<Delivery>
		.When(d => d.State == DeliveryState.Failed)
		.Requires(d => !string.IsNullOrWhiteSpace(d.Error), "A failed delivery says what the network answered, for the owner to read.");

	public static readonly Constraint<Delivery> ManualPostsHaveTheirIssue = Constraints<Delivery>
		.When(d => d.State is DeliveryState.ManualDue or DeliveryState.ManualDone)
		.Requires(d => d.Issue is > 0, "A manual post waits in an issue, and keeps its number.");

	public static readonly Constraint<Delivery> ManualPostsAreNeverAttempted = Constraints<Delivery>.Always(
		d => !d.IsManual || (d.Attempts == 0 && d.LastAttempt is null),
		"Braggarr never attempts a Reddit or Hacker News post, so none counts an attempt."
	);

}
