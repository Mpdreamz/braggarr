using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using AwesomeAssertions;
using Braggarr.Web.Settings;

namespace Braggarr.Web.Tests;

/// <summary>config.json as written by any earlier version: a setting it does not hold yet reads as that setting's default.</summary>
public class SettingsStoreTests
{
	private static string Written(BraggarrSettings settings) => JsonSerializer.Serialize(settings, SettingsJson.Default.BraggarrSettings);

	private static BraggarrSettings Read(string json)
	{
		var fs = new MockFileSystem();
		fs.AddFile("/config/config.json", new MockFileData(json));
		using var store = new SettingsStore(fs, "/config");
		return store.Current;
	}

	[Test]
	[Arguments("{}")]
	[Arguments("""{"postsRepository":"owner/posts"}""")]
	public void A_setting_the_file_leaves_out_is_its_default(string json)
	{
		var read = Read(json);

		Written(read with { PostsRepository = null }).Should().Be(Written(new BraggarrSettings()));
	}

	[Test]
	public void A_nested_setting_the_file_leaves_out_is_its_default()
	{
		var read = Read("""{"rebalance":{"perDay":3},"gitHubApp":{"appId":7}}""");

		read.Rebalance.Should().BeEquivalentTo(new RebalanceSettings() with { PerDay = 3 });
		read.GitHubApp.AppId.Should().Be(7);
		read.TimeZone.Should().Be(new BraggarrSettings().TimeZone);
	}

	[Test]
	public void What_the_file_holds_is_kept()
	{
		var settings = new BraggarrSettings { PostsRepository = "owner/posts", TimeZone = "UTC", TrustedProxies = ["10.0.0.0/8"], Rebalance = new() { RestDays = [], PerDay = 2 } };

		Written(Read(Written(settings))).Should().Be(Written(settings));
	}
}
