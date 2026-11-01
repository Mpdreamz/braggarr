using Braggarr.Web;
using Nullean.Argh;

namespace Braggarr.Cli;

/// <summary>Braggarr itself: the server the owner reviews campaigns on and agents propose them to.</summary>
public sealed class ServerCommands
{
	/// <summary>Run Braggarr: the owner's pages, the API and MCP for agents, and the scheduler that posts.</summary>
	/// <param name="config">-c, The folder for its config, secrets and clone of the posts repo. Defaults to BRAGGARR_CONFIG, else /config.</param>
	/// <param name="urls">Where it listens, such as http://+:9494. Defaults to ASPNETCORE_URLS.</param>
	[CommandName("serve")]
	public static async Task<int> Serve(string? config = null, string? urls = null)
	{
		await Server.RunAsync(config, urls is null ? [] : ["--urls", urls]);
		return 0;
	}

	/// <summary>Whether Braggarr answers on port 9494: the container's health check. Exits 0 when it does.</summary>
	[CommandName("healthcheck")]
	public static Task<int> HealthCheck() => Server.HealthCheckAsync();
}
