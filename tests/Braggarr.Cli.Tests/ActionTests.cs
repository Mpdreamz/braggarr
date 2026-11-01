using System.Runtime.CompilerServices;
using AwesomeAssertions;
using SharpYaml;

namespace Braggarr.Cli.Tests;

/// <summary>The action manifests and workflows are YAML that GitHub can load, and every input the action declares reaches the build.</summary>
public class ActionTests
{
	private static string Repository([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

	private static Dictionary<string, object> Load(string relative) =>
		YamlSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(Repository(), relative)))!;

	public static IEnumerable<string> Manifests() =>
		new[] { "action.yml", Path.Combine("actions", "publish", "action.yml") }
			.Concat(Directory.GetFiles(Path.Combine(Repository(), ".github", "workflows"), "*.yml").Select(f => Path.GetRelativePath(Repository(), f)));

	[Test]
	[MethodDataSource(nameof(Manifests))]
	public void Each_manifest_is_yaml_github_can_load(string relative)
	{
		var document = Load(relative);

		document.Should().ContainKey(relative.EndsWith("action.yml", StringComparison.Ordinal) ? "runs" : "jobs");
	}

	[Test]
	public void Every_input_of_the_action_reaches_the_build()
	{
		var inputs = ((Dictionary<string, object>)Load("action.yml")["inputs"]).Keys.ToList();

		inputs.Should().NotBeEmpty();
		foreach (var input in inputs)
		{
			var name = "INPUT_" + input.ToUpperInvariant();
			GitHubActions.Args(n => n == name ? "true" : null).Should().HaveCountGreaterThan(2, $"the action declares {input}, so the build must read {name}");
		}
	}
}
