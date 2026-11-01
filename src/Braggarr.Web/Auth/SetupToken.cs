using System.Security.Cryptography;
using System.Text;

namespace Braggarr.Web.Auth;

/// <summary>
/// The one-time token that opens the setup page. Made at startup while Braggarr is not set up, printed to the container log, kept
/// only in memory, and gone once the owner signs in.
/// </summary>
public sealed class SetupToken
{
	private string? _token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

	public string? Value => _token;

	public bool Matches(string? presented) =>
		_token is { } token && presented is not null
		&& CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(presented));

	public void Spend() => _token = null;
}
