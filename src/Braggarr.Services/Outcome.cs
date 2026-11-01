namespace Braggarr.Services;

/// <summary>What a request to a service led to: done, or refused with the reasons in words the owner can act on.</summary>
public abstract record Outcome<T>
{
	public sealed record Done(T Value) : Outcome<T>;

	public sealed record Refused(IReadOnlyList<string> Reasons) : Outcome<T>
	{
		public Refused(string reason) : this([reason]) { }
	}
}
