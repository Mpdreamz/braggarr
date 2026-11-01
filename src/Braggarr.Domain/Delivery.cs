namespace Braggarr.Domain;

/// <summary>One post on one network: the unit that is published exactly once.</summary>
public sealed record DeliveryKey(string Campaign, string Post, Network Network) : IComparable<DeliveryKey>
{
	public int CompareTo(DeliveryKey? other) =>
		other is null ? 1
		: string.CompareOrdinal(Campaign, other.Campaign) is var c and not 0 ? c
		: string.CompareOrdinal(Post, other.Post) is var p and not 0 ? p
		: Network.CompareTo(other.Network);

	public override string ToString() => $"{Campaign}/{Post}/{Network.Slug()}";

	public static bool operator <(DeliveryKey left, DeliveryKey right) => left.CompareTo(right) < 0;

	public static bool operator <=(DeliveryKey left, DeliveryKey right) => left.CompareTo(right) <= 0;

	public static bool operator >(DeliveryKey left, DeliveryKey right) => left.CompareTo(right) > 0;

	public static bool operator >=(DeliveryKey left, DeliveryKey right) => left.CompareTo(right) >= 0;
}

public enum DeliveryState
{
	/// <summary>Waiting for its time.</summary>
	Pending,

	/// <summary>Recorded before the network call. Seen again after a restart, it becomes <see cref="Unknown"/>, never a retry.</summary>
	Sending,

	Published,

	/// <summary>The network said no. Retried with a backoff until <see cref="Delivery.MaxAttempts"/>.</summary>
	Failed,

	/// <summary>Braggarr stopped while sending, so it cannot know whether the post went out. The owner decides.</summary>
	Unknown,

	/// <summary>A manual network: an issue asks the owner to post it.</summary>
	ManualDue,

	/// <summary>The owner closed the issue.</summary>
	ManualDone,

	/// <summary>
	/// A blog post committed to its site's repository, at the address its receipt names, but not answering there yet: the site
	/// is still building. It is live (<see cref="Published"/>) once that address answers.
	/// </summary>
	Committed
}

/// <summary>What a network answered when a post went out.</summary>
public sealed record Receipt(string RemoteId, string Url);

public sealed record Delivery(
	DeliveryKey Key,
	DeliveryState State,
	int Attempts,
	DateTimeOffset? LastAttempt,
	Receipt? Receipt,
	string? Error,
	int? Issue
)
{
	public const int MaxAttempts = 3;

	public static Delivery New(DeliveryKey key) => new(key, DeliveryState.Pending, 0, null, null, null, null);

	public bool IsManual => Key.Network.IsManual();

	/// <summary>A post to the owner's blog: committed to its site, then live once the site answers.</summary>
	public bool IsBlog => Key.Network == Network.Blog;

	/// <summary>Something has happened to it: an attempt (even one the owner later found missing), or an issue opened.</summary>
	public bool HasHistory => State != DeliveryState.Pending || Attempts > 0 || Issue is not null;

	/// <summary>Nothing more will happen to it without the owner. A committed blog post still waits for its site.</summary>
	public bool IsSettled => State is DeliveryState.Published or DeliveryState.ManualDone or DeliveryState.Unknown
		|| (State == DeliveryState.Failed && Attempts >= MaxAttempts);

	/// <summary>How long after a failed attempt the next one may start: 1, 4, then 16 minutes.</summary>
	public static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(Math.Pow(4, Math.Max(0, attempts - 1)));

	/// <summary>Pending to sending, counting the attempt. Recorded durably before the network is called.</summary>
	public Delivery Begin(DateTimeOffset now) =>
		State == DeliveryState.Pending && !IsManual ? this with { State = DeliveryState.Sending, Attempts = Attempts + 1, LastAttempt = now, Error = null } : this;

	public Delivery Succeed(Receipt receipt) =>
		State == DeliveryState.Sending && !IsBlog ? this with { State = DeliveryState.Published, Receipt = receipt, Error = null } : this;

	/// <summary>A blog post is in its site's repository, to appear at the receipt's address once the site has built.</summary>
	public Delivery Commit(Receipt receipt) =>
		State == DeliveryState.Sending && IsBlog ? this with { State = DeliveryState.Committed, Receipt = receipt, Error = null } : this;

	/// <summary>The committed blog post answers at its address: it is live, and what links to it may go out.</summary>
	public Delivery GoLive() => State == DeliveryState.Committed ? this with { State = DeliveryState.Published } : this;

	/// <summary>
	/// The committed blog post never answered at its address: the site did not build, or not where it was expected. The owner
	/// decides; nothing that links to it goes out meanwhile.
	/// </summary>
	public Delivery TimeOut() => State == DeliveryState.Committed ? this with { State = DeliveryState.Unknown, Receipt = null } : this;

	public Delivery Fail(string error) => State == DeliveryState.Sending ? this with { State = DeliveryState.Failed, Error = error } : this;

	/// <summary>
	/// A failed delivery goes back to pending once its backoff has passed. How many tries a delivery gets is the schedule's
	/// policy (<see cref="Schedule.Retry"/>), not part of its lifecycle.
	/// </summary>
	public Delivery Retry(DateTimeOffset now) =>
		State == DeliveryState.Failed && LastAttempt is { } last && now >= last + Backoff(Attempts) ? this with { State = DeliveryState.Pending } : this;

	/// <summary>Whether the schedule may try again: failed, with attempts left.</summary>
	public bool HasAttemptsLeft => State == DeliveryState.Failed && Attempts < MaxAttempts;

	/// <summary>
	/// After a restart: a delivery still sending may or may not have gone out, so the owner decides. A blog post goes back to
	/// waiting instead: committing it again finds it already there, so it cannot appear twice.
	/// </summary>
	public Delivery Recover() =>
		State == DeliveryState.Sending ? this with { State = IsBlog ? DeliveryState.Pending : DeliveryState.Unknown } : this;

	/// <summary>The owner checked the network and the post is there.</summary>
	public Delivery ConfirmPublished(Receipt receipt) =>
		State == DeliveryState.Unknown ? this with { State = DeliveryState.Published, Receipt = receipt } : this;

	/// <summary>The owner checked the network and the post is not there.</summary>
	public Delivery ConfirmMissing() => State == DeliveryState.Unknown ? this with { State = DeliveryState.Pending } : this;

	/// <summary>A manual post waits in an issue. GitHub numbers issues from 1; anything else is not an issue, and changes nothing.</summary>
	public Delivery OpenManual(int issue) =>
		State == DeliveryState.Pending && IsManual && issue > 0 ? this with { State = DeliveryState.ManualDue, Issue = issue } : this;

	public Delivery CloseManual() => State == DeliveryState.ManualDue ? this with { State = DeliveryState.ManualDone } : this;
}
