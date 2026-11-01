using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Braggarr.Adapters.Claude;
using Braggarr.Adapters.Social;

namespace Braggarr.Adapters;

/// <summary>
/// The networks' JSON in camelCase (<see cref="JsonSerializerDefaults.Web"/>), generated so no reflection is needed. A type
/// missing here fails the first call that uses it, in the tests.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(BlueskyPublisher.Session))]
[JsonSerializable(typeof(BlueskyPublisher.SignIn))]
[JsonSerializable(typeof(MastodonPublisher.Status))]
[JsonSerializable(typeof(MastodonPublisher.Account))]
[JsonSerializable(typeof(MastodonPublisher.Attachment))]
[JsonSerializable(typeof(LinkedInPublisher.UserInfo))]
[JsonSerializable(typeof(LinkedInPublisher.Initialized))]
[JsonSerializable(typeof(LinkedInPublisher.VideoInitialized))]
[JsonSerializable(typeof(LinkedInPublisher.VideoState))]
[JsonSerializable(typeof(ThreadsPublisher.Created))]
[JsonSerializable(typeof(ThreadsPublisher.Me))]
[JsonSerializable(typeof(GoogleOAuth.TokenAnswer))]
internal sealed partial class WebJson : JsonSerializerContext
{
	public static JsonTypeInfo<T> Info<T>() =>
		(JsonTypeInfo<T>?)Default.GetTypeInfo(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T)} is not in WebJson.");

	/// <summary>A request body written as it is built: its keys are the network's own, no naming policy applies.</summary>
	public static System.Net.Http.Json.JsonContent Body(JsonObject body) => System.Net.Http.Json.JsonContent.Create<JsonNode>(body, Default.JsonNode);
}

/// <summary>The networks' JSON in snake_case: X, and Threads' answers.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(ThreadsPublisher.ContainerStatus))]
[JsonSerializable(typeof(ThreadsPublisher.Token))]
[JsonSerializable(typeof(ThreadsPublisher.Published))]
internal sealed partial class SnakeJson : JsonSerializerContext
{
	public static JsonTypeInfo<T> Info<T>() =>
		(JsonTypeInfo<T>?)Default.GetTypeInfo(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T)} is not in SnakeJson.");
}

/// <summary>
/// What Braggarr keeps on its own disk with the serializer's defaults (property names as declared): the secrets, each account's
/// credentials, and the Claude usage log. Changing these options would make what is already stored unreadable.
/// </summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(BlueskyCredentials))]
[JsonSerializable(typeof(MastodonCredentials))]
[JsonSerializable(typeof(LinkedInCredentials))]
[JsonSerializable(typeof(ThreadsCredentials))]
[JsonSerializable(typeof(XCredentials))]
[JsonSerializable(typeof(YouTubeCredentials))]
[JsonSerializable(typeof(Blog.BlogSite))]
[JsonSerializable(typeof(YouTubeClient))]
[JsonSerializable(typeof(UsageLog.Entry))]
internal sealed partial class StoredJson : JsonSerializerContext
{
	public static JsonTypeInfo<T> Info<T>() =>
		(JsonTypeInfo<T>?)Default.GetTypeInfo(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T)} is not in StoredJson.");
}

/// <summary>JSON written by hand, as nodes: the schemas Claude answers to, and the campaigns put in front of it.</summary>
internal static class Schemas
{
	/// <summary>
	/// Indented, with the default encoder, which escapes &lt; and &gt;: nothing in a brief or a post can close the
	/// <c>&lt;campaign&gt;</c> tag it sits in. Nodes write themselves, so no type information is needed.
	/// </summary>
	public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

	/// <summary>Through the constructor: a collection expression would call <c>JsonArray.Add&lt;T&gt;</c>, which needs reflection.</summary>
	public static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

	public static JsonElement Element(JsonObject schema) => JsonDocument.Parse(schema.ToJsonString()).RootElement.Clone();
}
