using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Braggarr.Web.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Braggarr.Web.Auth;

/// <summary>
/// Agents' keys: 32 random bytes, shown once, kept only as a SHA-256 hash. A key names its agent; that name is the actor on every
/// proposal it makes. There is nothing else a key can be allowed to do.
/// </summary>
public static class ApiKeys
{
	public const string Scheme = "ApiKey";
	public const string Header = "X-Api-Key";
	public const string Prefix = "brag_";

	public static string New() => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

	/// <summary>The key's entry, compared in constant time so a timing difference never tells how much of a guess was right.</summary>
	public static ApiKeySettings? Find(IEnumerable<ApiKeySettings> keys, string presented)
	{
		var hash = Encoding.ASCII.GetBytes(Hash(presented));
		ApiKeySettings? found = null;
		foreach (var key in keys)
		{
			if (CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(key.Hash)))
			{
				found = key;
			}
		}
		return found;
	}
}

public sealed class ApiKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, SettingsStore settings)
	: AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		if (!Request.Headers.TryGetValue(ApiKeys.Header, out var presented) || presented.Count != 1 || string.IsNullOrEmpty(presented[0]))
		{
			return Task.FromResult(AuthenticateResult.NoResult());
		}
		if (ApiKeys.Find(settings.Current.ApiKeys, presented[0]!) is not { } key)
		{
			return Task.FromResult(AuthenticateResult.Fail("Unknown API key."));
		}
		var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, key.Name)], ApiKeys.Scheme);
		return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), ApiKeys.Scheme)));
	}
}
