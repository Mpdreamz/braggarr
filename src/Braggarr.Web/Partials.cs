using Braggarr.Domain;

namespace Braggarr.Web;

/// <summary>Words for states, as the pages say them.</summary>
public static class Partials
{
	public static string State(DeliveryState state) => state switch
	{
		DeliveryState.Pending => "waiting",
		DeliveryState.Sending => "posting",
		DeliveryState.Published => "posted",
		DeliveryState.Failed => "failed",
		DeliveryState.Unknown => "did it go out?",
		DeliveryState.ManualDue => "your turn",
		DeliveryState.ManualDone => "posted by you",
		_ => state.ToString()
	};
}
