using AwesomeAssertions;

namespace Braggarr.Domain.Tests;

public class DependencyTests
{
	[Test]
	public void The_domain_depends_on_nothing_but_the_runtime_and_nullean_entities()
	{
		var references = typeof(Campaign).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

		references.Where(n => !n.StartsWith("System", StringComparison.Ordinal) && n != "netstandard")
			.Should().BeEquivalentTo(["Nullean.Entities"]);
	}
}
