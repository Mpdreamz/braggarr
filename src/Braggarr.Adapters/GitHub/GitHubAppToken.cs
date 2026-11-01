using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Braggarr.Adapters.Git;
using Microsoft.Extensions.Options;

namespace Braggarr.Adapters.GitHub;

/// <summary>
/// Installation tokens for the posts repo. Signs a short JWT with the app's key, exchanges it for a token that lasts an hour,
/// and keeps it until five minutes before it expires.
/// </summary>
public sealed class GitHubAppToken(HttpClient http, IOptionsMonitor<GitHubAppOptions> app, IOptionsMonitor<PostsRepositoryOptions> repo, TimeProvider time)
	: IGitHubToken, IDisposable
{
	private readonly SemaphoreSlim _lock = new(1, 1);
	private (string Token, DateTimeOffset Expires)? _cached;

	public async Task<string?> GetAsync(CancellationToken ct)
	{
		if (!app.CurrentValue.IsConfigured)
		{
			return null;
		}
		await _lock.WaitAsync(ct);
		try
		{
			if (_cached is { } cached && cached.Expires - TimeSpan.FromMinutes(5) > time.GetUtcNow())
			{
				return cached.Token;
			}
			var jwt = Jwt();
			var installation = await SendAsync<Installation>(HttpMethod.Get, $"repos/{repo.CurrentValue.Repository}/installation", jwt, ct);
			var token = await SendAsync<AccessToken>(HttpMethod.Post, $"app/installations/{installation.Id}/access_tokens", jwt, ct);
			_cached = (token.Token, token.ExpiresAt);
			return token.Token;
		}
		finally
		{
			_ = _lock.Release();
		}
	}

	public void Forget() => _cached = null;

	private async Task<T> SendAsync<T>(HttpMethod method, string path, string jwt, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(method, new Uri(new Uri(app.CurrentValue.ApiBase.TrimEnd('/') + "/"), path));
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
		GitHubClient.AddHeaders(request);
		using var response = await http.SendAsync(request, ct);
		if (!response.IsSuccessStatusCode)
		{
			throw new GitHubException($"GitHub refused the app ({(int)response.StatusCode}) for {path}: {await response.Content.ReadAsStringAsync(ct)}");
		}
		return (await response.Content.ReadFromJsonAsync(GitHubClient.Info<T>(), ct))!;
	}

	/// <summary>An app JWT: valid from a minute ago (clock skew) for nine minutes, signed RS256 with the app's private key.</summary>
	internal string Jwt()
	{
		var now = time.GetUtcNow().ToUnixTimeSeconds();
		var header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8);
		var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new JwtPayload(now - 60, now + 540, app.CurrentValue.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture)), GitHubClient.Info<JwtPayload>()));
		using var rsa = RSA.Create();
		rsa.ImportFromPem(app.CurrentValue.PrivateKeyPem);
		var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		return $"{header}.{payload}.{Base64Url(signature)}";
	}

	private static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	internal sealed record Installation(long Id);

	internal sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

	internal sealed record JwtPayload(long Iat, long Exp, string Iss);

	public void Dispose() => _lock.Dispose();
}
