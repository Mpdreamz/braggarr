namespace Braggarr.Web.Auth;

public static class Roles
{
	public const string ClaimType = "braggarr:role";
	public const string OwnerIdClaim = "braggarr:owner-id";
	public const string Owner = "owner";
	public const string Setup = "setup";

	public const string OwnerPolicy = "owner";
	public const string SetupPolicy = "setup";
	public const string AgentPolicy = "agent";
}
