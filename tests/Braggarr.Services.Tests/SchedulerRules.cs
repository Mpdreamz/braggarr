using Braggarr.Domain;
using Nullean.Entities;

namespace Braggarr.Services.Tests;

/// <summary>
/// The scheduler's promises over its own event log, shared by the run against in-memory fakes and the run against a real git
/// clone (compiled into the web tests as a link), so the two can never check different things.
/// </summary>
public static class SchedulerRules
{
	/// <summary>What the scheduler promises over any history of ticks, failures and crashes.</summary>
	public static readonly TemporalRuleSet<DeliveryEvent> All = TemporalRuleSet<DeliveryEvent>.From(
		("never-published-twice", Temporal<DeliveryEvent>.NeverTwice<DeliveryEvent.Published>(e => e.Key)),
		(
			"every-attempt-finishes",
			Temporal<DeliveryEvent>.Eventually(
				e => e.Key,
				e => e is DeliveryEvent.Started,
				e => e is DeliveryEvent.Published or DeliveryEvent.Failed or DeliveryEvent.MarkedUnknown,
				"Every attempt ends as published, failed or unknown, even across a crash."
			)
		),
		(
			"unknown-is-never-resent",
			Temporal<DeliveryEvent>.Never<DeliveryEvent.MarkedUnknown, DeliveryEvent.Started>((unknown, started) => unknown.Key == started.Key)
		),
		("manual-networks-are-never-sent", Temporal<DeliveryEvent>.Always<DeliveryEvent.Started>(e => !e.Key.Network.IsManual())),
		// No owner settles anything in these histories, so an attempt is the first, second or third, never a fourth.
		("at-most-three-attempts", Temporal<DeliveryEvent>.Always<DeliveryEvent.Started>(e => e.Attempt is >= 1 and <= Delivery.MaxAttempts)),
		(
			"published-is-never-sent-again",
			Temporal<DeliveryEvent>.Never<DeliveryEvent.Published, DeliveryEvent.Started>((published, started) => published.Key == started.Key)
		),
		("one-issue-per-manual-post", Temporal<DeliveryEvent>.NeverTwice<DeliveryEvent.ManualOpened>(e => e.Key))
	);
}
