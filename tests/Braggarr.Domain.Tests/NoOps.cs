using Nullean.Entities;
using Nullean.Entities.CsCheck;

namespace Braggarr.Domain.Tests;

/// <summary>
/// The fault <c>Faults()</c> only reports: an operation that does nothing at all. The rules say what must never happen, and
/// a do-nothing operation breaks none of them; what notices it is coverage, since an edge of the lifecycle is then never taken
/// and the exhaustive proof refuses to pass vacuously. So each operation is replaced by one that returns its input, and the
/// proof must fail.
/// </summary>
public static class NoOps
{
	/// <summary>Whether the proof still passes with <paramref name="action"/> doing nothing: true is a missing rule.</summary>
	public static bool GoesUnnoticed<T>(ConstraintSet<T> rules, EntityActions<T> actions, string action, Func<EntitySpec<T>, EntitySpec<T>> close)
	{
		// The control: unbroken, the proof passes. Otherwise every do-nothing operation would look noticed, for the wrong reason.
		_ = close(EntitySpec.For(rules, actions)).Exhaustive();
		var broken = new EntityActions<T>([.. actions.Select(a => a.Name == action ? a with { Apply = (entity, _) => entity } : a)]);
		try
		{
			_ = close(EntitySpec.For(rules, broken)).Exhaustive();
			return true;
		}
		catch (Exception e) when (e is not OutOfMemoryException)
		{
			return false;
		}
	}
}
