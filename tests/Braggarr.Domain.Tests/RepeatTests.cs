using AwesomeAssertions;

namespace Braggarr.Domain.Tests;

public class RepeatTests
{
	private static readonly DateTimeOffset Launch = new(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(1));

	private static Campaign Curb(int months, DateTimeOffset? lastRepeat = null) =>
		new Campaign(
			"curb-1-0", "Curb 1.0", "curb", CampaignStatus.Proposed, [Network.Bluesky, Network.Reddit], [], "", "owner:me",
			[new Post("01-launch", Launch, [new Variant(Network.Bluesky, "Out."), new Variant(Network.Reddit, "Out")])]
		)
		{ RepeatMonths = months, LastRepeat = lastRepeat }.Approve(new(Gens.Owner, Gens.Start));

	private static Delivery[] AllOut(Campaign c) =>
	[
		Delivery.New(new DeliveryKey(c.Slug, "01-launch", Network.Bluesky)).Begin(Launch).Succeed(new Receipt("1", "u")),
		Delivery.New(new DeliveryKey(c.Slug, "01-launch", Network.Reddit)).OpenManual(1).CloseManual()
	];

	[Test]
	public void A_repeat_is_due_the_months_after_the_last_post_once_everything_went_out()
	{
		var curb = Curb(3);

		curb.RepeatIsDue(AllOut(curb), Launch.AddMonths(3).AddMinutes(-1)).Should().BeFalse();
		curb.RepeatIsDue(AllOut(curb), Launch.AddMonths(3)).Should().BeTrue();
	}

	[Test]
	public void Not_while_something_has_not_gone_out_and_not_when_it_never_repeats()
	{
		var curb = Curb(3);
		var waiting = AllOut(curb)[..1].Append(Delivery.New(new DeliveryKey(curb.Slug, "01-launch", Network.Reddit))).ToArray();

		curb.RepeatIsDue(waiting, Launch.AddYears(1)).Should().BeFalse();
		Curb(0).RepeatIsDue(AllOut(curb), Launch.AddYears(1)).Should().BeFalse();
		curb.Hold(Gens.Owner).RepeatIsDue(AllOut(curb), Launch.AddYears(1)).Should().BeFalse();
	}

	[Test]
	public void After_a_repeat_the_next_counts_from_it()
	{
		var curb = Curb(3, lastRepeat: Launch.AddMonths(3));

		curb.RepeatIsDue(AllOut(curb), Launch.AddMonths(5)).Should().BeFalse();
		curb.RepeatIsDue(AllOut(curb), Launch.AddMonths(6)).Should().BeTrue();
	}
}
