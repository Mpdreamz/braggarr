using Braggarr.Adapters.Claude;
using Braggarr.Domain;
using Braggarr.Adapters.Social;
using Braggarr.Services;
using Braggarr.Web.Hosting;
using Braggarr.Web.Settings;

namespace Braggarr.Web.Ui;

/// <summary>How Braggarr is doing: the scheduler, the posts repo, the connections and their tokens, what drafting cost.</summary>
public sealed class SystemModel(SettingsStore settings, AccountStore accounts, SchedulingService scheduling, SchedulerHost host, UsageLog usage, DraftingService drafting, Clock clock, TimeProvider time) : UiPage
{
	public BraggarrSettings Settings => settings.Current;

	public Clock Clock => clock;

	public DateTimeOffset? LastSync => scheduling.LastSync;

	public string? Problem => host.LastProblem;

	public bool Started => host.Started;

	public int Campaigns => scheduling.Current.Campaigns.Count;

	public int Deliveries => scheduling.Current.Deliveries.Count;

	public bool Drafting => drafting.IsAvailable;

	public static string Version => typeof(SystemModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
		.OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";

	public IReadOnlyList<(string Network, string Account, string? Expires)> Connections { get; private set; } = [];

	public (decimal Dollars, int Calls) ThisMonth { get; private set; }

	public void OnGet()
	{
		var now = time.GetUtcNow();
		string Days(DateTimeOffset at) => at <= now ? "expired" : $"{(int)(at - now).TotalDays} days left";
		var connections = new List<(string, string, string?)>();
		foreach (var account in accounts.All())
		{
			DateTimeOffset? expires = account.Network switch
			{
				Network.LinkedIn => accounts.Get<LinkedInCredentials>(account.Network, account.Name)?.ExpiresAt,
				Network.Threads => accounts.Get<ThreadsCredentials>(account.Network, account.Name)?.ExpiresAt,
				_ => null
			};
			connections.Add((NetworkIcons.Name(account.Network), account.Name, expires is { } e ? Days(e) : null));
		}
		Connections = connections;
		var entries = usage.Since(new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero));
		ThisMonth = (entries.Sum(e => e.Dollars), entries.Count);
	}

	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) => (method, handler) switch
	{
		("GET", null) => Shown(OnGet),
		_ => null
	};

	protected override RazorSlices.RazorSlice Slice() => SystemPage.Create(this);
}
