using AwesomeAssertions;

namespace Braggarr.Blog.Tests;

public class DependencyTests
{
	/// <summary>The blog is published on its own, in an AOT binary: it knows nothing of Braggarr's domain, services or web app.</summary>
	[Test]
	public void The_blog_depends_on_nothing_of_braggarrs_own()
	{
		var references = typeof(Site).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();

		references.Should().NotContain(n => n.StartsWith("Braggarr.", StringComparison.Ordinal));
	}
}
