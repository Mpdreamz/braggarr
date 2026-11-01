namespace Braggarr.Web;

/// <summary>Braggarr running: the server, and the check a container makes that it answers.</summary>
public static class Server
{
	/// <summary>
	/// The application's name, as when this project was the entry point: the OpenAPI document is titled and tagged with it,
	/// whichever binary runs the server.
	/// </summary>
	public const string ApplicationName = "Braggarr.Web";

	/// <summary>
	/// Serves Braggarr until stopped. Its config is in <paramref name="configDirectory"/>, else BRAGGARR_CONFIG (an environment
	/// variable, or a setting in tests), else /config, the container's volume. <paramref name="args"/> are ASP.NET Core's own,
	/// such as <c>--urls</c>.
	/// </summary>
	public static async Task RunAsync(string? configDirectory, string[] args)
	{
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ApplicationName = ApplicationName });
		builder.Services.AddBraggarr(configDirectory ?? builder.Configuration["BRAGGARR_CONFIG"] ?? "/config");
		var app = builder.Build();
		app.UseBraggarr();
		await app.RunAsync();
	}

	/// <summary>The container's health check, since its image has no curl: 0 when /healthz answers on port 9494, else 1.</summary>
	public static async Task<int> HealthCheckAsync()
	{
		using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
		try
		{
			return (await client.GetAsync(new Uri("http://127.0.0.1:9494/healthz"))).IsSuccessStatusCode ? 0 : 1;
		}
		catch (HttpRequestException)
		{
			return 1;
		}
	}
}
