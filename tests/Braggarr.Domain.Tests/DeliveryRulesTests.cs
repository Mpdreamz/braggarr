using AwesomeAssertions;
using Nullean.Entities.CsCheck;

namespace Braggarr.Domain.Tests;

public class DeliveryRulesTests
{
	private static readonly Receipt Posted = new("1", "https://bsky.app/1");

	/// <summary>
	/// Each counter-example breaks its own rule and no other, from any generated delivery it applies to; a delivery it cannot
	/// break that way is returned as it is, and skipped.
	/// </summary>
	public static AutoTest<Delivery> Auto() =>
		AutoTest.For(DeliveryRules.Instance, Gens.Deliveries)
			.CounterExample(DeliveryRules.OnlyPublishedHasAReceipt, d => d.State == DeliveryState.Published ? d : d with { Receipt = Posted })
			.CounterExample(DeliveryRules.PublishedHasAReceipt, d => d.State == DeliveryState.Published ? d with { Receipt = null } : d)
			.CounterExample(DeliveryRules.AttemptsNeverGoBack, d => d.Attempts > 1 ? d with { Attempts = d.Attempts - 1 } : d)
			.CounterExample(DeliveryRules.ManualPostsHaveTheirIssue, d => d.State is DeliveryState.ManualDue or DeliveryState.ManualDone ? d with { Issue = null } : d)
			.Deeper(Closed);

	private static readonly Delivery Bluesky = Delivery.New(new DeliveryKey("curb-1-0", "01-launch", Network.Bluesky));
	private static readonly Delivery Reddit = Delivery.New(new DeliveryKey("curb-1-0", "01-launch", Network.Reddit));
	private static readonly Delivery Blog = Delivery.New(new DeliveryKey("curb-1-0", "00-blog", Network.Blog));

	/// <summary>
	/// The whole state space of one automatic, one manual and one blog delivery, closed by stopping at four attempts: every rule
	/// is proved over it, and every edge of the three diagrams must be taken.
	/// </summary>
	private static EntitySpec<Delivery> Closed(EntitySpec<Delivery> spec) =>
		spec
			.From(Bluesky, Reddit, Blog)
			.Domain(Gens.Start, Gens.Start.AddHours(1), Gens.Start.AddDays(2))
			.Domain(Posted)
			.Domain("503 from the server")
			.Domain(7)
			.Boundary(d => d.Attempts <= 4);

	public static IEnumerable<string> Operations() => DeliveryRules.Actions.Select(a => a.Name);

	[Test]
	[MethodDataSource(nameof(Operations))]
	public void An_operation_that_does_nothing_is_noticed(string operation) =>
		NoOps.GoesUnnoticed(DeliveryRules.All, DeliveryRules.Actions, operation, Closed).Should().BeFalse($"{operation} doing nothing must fail the proof");

	/// <summary>
	/// Bugs that could be written, each injected into one operation in turn: some rule must notice every one. One start per
	/// spec, because a fault that can only happen from one start reads as uncaught from the other (see docs/entities-notes.md).
	/// </summary>
	[Test]
	public void Every_bug_an_automatic_delivery_could_have_is_caught_by_a_rule() =>
		_ = Closed(DeliveryRules.Instance.Spec()).From(Bluesky)
			.Fault("Begin does not count the attempt", "Begin", (before, after) => after with { Attempts = before.Attempts })
			.Fault("Succeed forgets the receipt", "Succeed", (_, after) => after with { Receipt = null })
			.Fault("Fail forgets why", "Fail", (_, after) => after with { Error = null })
			.Fault("Recover sends again", "Recover", (before, after) => before.State == DeliveryState.Sending ? before with { State = DeliveryState.Pending } : after)
			.Fault("Retry starts over", "Retry", (_, after) => after with { Attempts = 0 })
			.Fault("ConfirmMissing keeps a receipt", "ConfirmMissing", (before, after) => before.State == DeliveryState.Unknown ? after with { Receipt = Posted } : after)
			.Fault("ConfirmPublished without a receipt", "ConfirmPublished", (_, after) => after with { Receipt = null })
			.Faults();

	[Test]
	public void Every_bug_a_blog_post_could_have_is_caught_by_a_rule() =>
		_ = Closed(DeliveryRules.Instance.Spec()).From(Blog)
			.Fault("Commit forgets where the post will be", "Commit", (_, after) => after with { Receipt = null })
			.Fault("GoLive skips the commit", "Begin", (before, after) => before.State == DeliveryState.Pending ? after with { State = DeliveryState.Published, Receipt = Posted } : after)
			.Fault("TimeOut keeps claiming the address", "TimeOut", (before, after) => before.State == DeliveryState.Committed ? after with { Receipt = before.Receipt } : after)
			.Fault("Recover leaves it to the owner", "Recover", (before, after) => before.State == DeliveryState.Sending ? before with { State = DeliveryState.Unknown } : after)
			.Fault("Succeed posts a blog post without committing it", "Succeed", (before, after) => before.State == DeliveryState.Sending ? before with { State = DeliveryState.Published, Receipt = Posted } : after)
			.Fault("Commit forgets the attempt", "Commit", (before, after) => after with { Attempts = 0, LastAttempt = null })
			.Faults();

	[Test]
	public void Every_bug_a_manual_delivery_could_have_is_caught_by_a_rule() =>
		_ = Closed(DeliveryRules.Instance.Spec()).From(Reddit)
			.Fault("Begin sends a manual post", "Begin", (before, after) => before.State == DeliveryState.Pending ? before with { State = DeliveryState.Sending } : after)
			.Fault("OpenManual loses the issue", "OpenManual", (_, after) => after with { Issue = null })
			.Fault("CloseManual reopens", "CloseManual", (before, after) => before.State == DeliveryState.ManualDone ? before with { State = DeliveryState.ManualDue } : after)
			.Fault("OpenManual counts an attempt", "OpenManual", (_, after) => after with { Attempts = 1 })
			.Faults();

	public static IEnumerable<Func<AxisCase>> Axes() => Auto().AsDataSource();

	[Test]
	[MethodDataSource(nameof(Axes))]
	public Task EveryDeclaredAxisHolds(AxisCase axis) => axis.RunAsync();

	[Test]
	public void The_automatic_diagram_reaches_every_state_through_every_edge() => DeliveryLifecycle.Automatic.Spec().Exhaustive();

	[Test]
	public void The_manual_diagram_reaches_every_state_through_every_edge() => DeliveryLifecycle.Manual.Spec().Exhaustive();

	[Test]
	public void The_blog_diagram_reaches_every_state_through_every_edge() => DeliveryLifecycle.Blog.Spec().Exhaustive();

	[Test]
	public void A_blog_post_follows_its_diagram() =>
		DeliveryLifecycle.Blog.ConformTo(
			Blog,
			d => d.State,
			bindings:
			[
				("Begin", d => d.Begin(Gens.Start)),
				("Commit", d => d.Commit(Posted)),
				("Fail", d => d.Fail("409 from GitHub")),
				("Retry", d => d.Retry(Gens.Start.AddDays(1))),
				("Recover", d => d.Recover()),
				("GoLive", d => d.GoLive()),
				("TimeOut", d => d.TimeOut()),
				("ConfirmPublished", d => d.ConfirmPublished(Posted)),
				("ConfirmMissing", d => d.ConfirmMissing())
			]
		);

	[Test]
	public void An_automatic_delivery_follows_its_diagram() =>
		DeliveryLifecycle.Automatic.ConformTo(
			Delivery.New(new DeliveryKey("curb-1-0", "01-launch", Network.Bluesky)),
			d => d.State,
			bindings:
			[
				("Begin", d => d.Begin(Gens.Start)),
				("Succeed", d => d.Succeed(new Receipt("1", "https://bsky.app/1"))),
				("Fail", d => d.Fail("boom")),
				("Retry", d => d.Retry(Gens.Start.AddDays(1))),
				("Recover", d => d.Recover()),
				("ConfirmPublished", d => d.ConfirmPublished(new Receipt("1", "https://bsky.app/1"))),
				("ConfirmMissing", d => d.ConfirmMissing())
			]
		);

	[Test]
	public void A_manual_delivery_follows_its_diagram() =>
		DeliveryLifecycle.Manual.ConformTo(
			Delivery.New(new DeliveryKey("curb-1-0", "01-launch", Network.HackerNews)),
			d => d.State,
			bindings: [("OpenManual", d => d.OpenManual(1)), ("CloseManual", d => d.CloseManual())]
		);

	[Test]
	public void A_failed_delivery_waits_for_its_backoff_and_stops_after_three_attempts()
	{
		var d = Delivery.New(new DeliveryKey("c", "p", Network.Mastodon)).Begin(Gens.Start).Fail("503");

		d.Retry(Gens.Start.AddSeconds(59)).State.Should().Be(DeliveryState.Failed);
		d.Retry(Gens.Start.AddMinutes(1)).State.Should().Be(DeliveryState.Pending);

		var third = d.Retry(Gens.Start.AddMinutes(1)).Begin(Gens.Start.AddMinutes(1)).Fail("503")
			.Retry(Gens.Start.AddMinutes(5)).Begin(Gens.Start.AddMinutes(5)).Fail("503");
		third.Attempts.Should().Be(3);
		third.HasAttemptsLeft.Should().BeFalse();
		third.IsSettled.Should().BeTrue();
		var schedule = new Schedule([], [third]);
		schedule.Retry(Gens.Start.AddYears(1)).Deliveries.Single().State.Should().Be(DeliveryState.Failed);
	}

	[Test]
	public void Braggarr_never_sends_to_a_manual_network()
	{
		var reddit = Delivery.New(new DeliveryKey("c", "p", Network.Reddit));

		reddit.Begin(Gens.Start).State.Should().Be(DeliveryState.Pending);
		reddit.OpenManual(12).State.Should().Be(DeliveryState.ManualDue);
		reddit.OpenManual(0).Should().Be(reddit, "GitHub numbers issues from 1");
	}
}
