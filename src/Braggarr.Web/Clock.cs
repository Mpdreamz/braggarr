using Braggarr.Web.Settings;

namespace Braggarr.Web;

/// <summary>Times in the owner's time zone, for the pages. Stored times keep their offset; this is only how they are shown.</summary>
public sealed class Clock(SettingsStore settings, TimeProvider time) : Braggarr.Services.IOwnerTime
{
	public TimeZoneInfo Zone => TimeZoneInfo.TryFindSystemTimeZoneById(settings.Current.TimeZone, out var zone) ? zone : TimeZoneInfo.Utc;

	public DateTimeOffset Now => TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone);

	public DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Zone);

	public string Day(DateTimeOffset at) => Local(at).ToString("dddd d MMMM", System.Globalization.CultureInfo.InvariantCulture);

	public string Time(DateTimeOffset at) => Local(at).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

	public string Full(DateTimeOffset at) => Local(at).ToString("ddd d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
