using System.Text.Json;
using System.Text.Json.Serialization;
using Braggarr.Web.Api;
using Braggarr.Web.Auth;
using Braggarr.Web.Mcp;
using Braggarr.Web.Settings;

namespace Braggarr.Web;

/// <summary>
/// What agents send and read, through /api/v1 and MCP: campaigns, posts and the calendar, in camelCase. Generated, so no
/// reflection; tests use it first, as the AOT binary does.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CampaignView))]
[JsonSerializable(typeof(IEnumerable<CampaignView>))]
[JsonSerializable(typeof(IEnumerable<BraggarrTools.PlannedPost>))]
[JsonSerializable(typeof(IEnumerable<CalendarEntry>))]
[JsonSerializable(typeof(CampaignRequest))]
[JsonSerializable(typeof(PostRequest))]
[JsonSerializable(typeof(PostRequest[]))]
[JsonSerializable(typeof(PointRequest[]))]
[JsonSerializable(typeof(RedraftRequest))]
[JsonSerializable(typeof(IntroduceRequest))]
[JsonSerializable(typeof(BlogRequest))]
[JsonSerializable(typeof(Accepted))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(DateTimeOffset?))]
// A refusal, and the OpenAPI document's upload: written by ASP.NET Core, which needs their metadata here once nothing reflects.
[JsonSerializable(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
[JsonSerializable(typeof(IFormFile))]
internal sealed partial class ApiJson : JsonSerializerContext;

/// <summary><c>/config/config.json</c>: camelCase and indented, as it has always been written, so a file already there still reads.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(BraggarrSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;

/// <summary>GitHub's answers during setup and sign-in, in snake_case.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubSignIn.OAuthToken))]
[JsonSerializable(typeof(GitHubSignIn.GitHubUser))]
[JsonSerializable(typeof(GitHubSignIn.CreatedApp))]
internal sealed partial class SignInJson : JsonSerializerContext;
