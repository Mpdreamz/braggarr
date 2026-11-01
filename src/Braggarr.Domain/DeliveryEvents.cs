namespace Braggarr.Domain;

/// <summary>What happened to a delivery, in the order it happened. The log the "never twice" and "always finishes" rules read.</summary>
public abstract record DeliveryEvent(DeliveryKey Key)
{
	public sealed record Started(DeliveryKey Key, int Attempt) : DeliveryEvent(Key);

	public sealed record Published(DeliveryKey Key, Receipt Receipt) : DeliveryEvent(Key);

	public sealed record Failed(DeliveryKey Key, string Error) : DeliveryEvent(Key);

	public sealed record MarkedUnknown(DeliveryKey Key) : DeliveryEvent(Key);

	public sealed record ManualOpened(DeliveryKey Key, int Issue) : DeliveryEvent(Key);

	public sealed record ManualClosed(DeliveryKey Key) : DeliveryEvent(Key);

	/// <summary>A blog post is in its site's repository, to appear at the receipt's address.</summary>
	public sealed record Committed(DeliveryKey Key, Receipt Receipt) : DeliveryEvent(Key);

	/// <summary>The committed blog post answers at its address.</summary>
	public sealed record WentLive(DeliveryKey Key) : DeliveryEvent(Key);

	/// <summary>The committed blog post never answered: the owner looks.</summary>
	public sealed record TimedOut(DeliveryKey Key) : DeliveryEvent(Key);

	/// <summary>A blog post interrupted while committing waits again: committing it once more finds it there if it got in.</summary>
	public sealed record Requeued(DeliveryKey Key) : DeliveryEvent(Key);
}
