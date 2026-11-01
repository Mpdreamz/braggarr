using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Adapters;

/// <summary>The last few hundred delivery events, for the history page. The durable record is <c>deliveries.yml</c>.</summary>
public sealed class InMemoryJournal : IDeliveryJournal
{
	private const int Keep = 500;
	private readonly Lock _lock = new();
	private readonly LinkedList<(DateTimeOffset At, DeliveryEvent Event)> _events = new();

	public void Record(DeliveryEvent change, DateTimeOffset at)
	{
		lock (_lock)
		{
			_ = _events.AddFirst((at, change));
			if (_events.Count > Keep)
			{
				_events.RemoveLast();
			}
		}
	}

	public IReadOnlyList<(DateTimeOffset At, DeliveryEvent Event)> Recent()
	{
		lock (_lock)
		{
			return [.. _events];
		}
	}
}
