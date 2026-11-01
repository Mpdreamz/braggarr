using AwesomeAssertions;

namespace Braggarr.Services.Tests;

public class DependencyTests
{
	private static readonly string[] Allowed = ["Braggarr.Domain", "Nullean.Entities", "Microsoft.Extensions.Options"];

	[Test]
	public void Services_depend_only_on_the_domain_its_rules_and_microsoft_extensions_abstractions()
	{
		var references = typeof(SchedulingService).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

		references.Where(n => !n.StartsWith("System", StringComparison.Ordinal) && n != "netstandard")
			.Should().OnlyContain(n => Allowed.Contains(n) || IsAbstractions(n));
	}

	private static bool IsAbstractions(string name) =>
		name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) && name.EndsWith("Abstractions", StringComparison.Ordinal);
}
