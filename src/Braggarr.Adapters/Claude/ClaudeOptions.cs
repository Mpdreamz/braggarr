namespace Braggarr.Adapters.Claude;

public sealed class ClaudeOptions
{
	public const string ApiKeySecret = "anthropic:api-key";

	public static readonly IReadOnlyList<string> Models = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"];

	public string? ApiKey { get; set; }

	public string Model { get; set; } = "claude-opus-5-5";

	/// <summary>low, medium or high. Writing short posts needs little thinking; medium is plenty.</summary>
	public string Effort { get; set; } = "medium";

	/// <summary>Where the requests go; tests point it at a stub.</summary>
	public string? BaseUrl { get; set; }

	public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

	/// <summary>Dollars per million tokens: input, output, cache read. From the published prices, 2026-10.</summary>
	public static (decimal Input, decimal Output, decimal CacheRead) Price(string model) => model switch
	{
		"claude-opus-5-5" => (4.00m, 20.00m, 0.20m),
		"claude-sonnet-5-5" => (2.00m, 10.00m, 0.20m),
		"claude-haiku-5-5" => (0.10m, 0.50m, 0.01m),
		_ => (0m, 0m, 0m)
	};
}
