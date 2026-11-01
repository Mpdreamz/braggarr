using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;
using Braggarr.Adapters;
using Braggarr.Adapters.Config;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Braggarr.Web.Tests;

/// <summary>The Data Protection keys through the scoped file system: the keys already on the server keep working.</summary>
public class KeyRepositoryTests
{
	private static IDataProtector Protector(IXmlRepository keys)
	{
		var services = new ServiceCollection();
		_ = services.AddDataProtection().SetApplicationName("Braggarr");
		_ = services.Configure<KeyManagementOptions>(o => o.XmlRepository = keys);
		return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector("Braggarr.Secrets.v1");
	}

	[Test]
	public void Keys_written_by_aspnets_own_repository_are_read_and_still_unprotect()
	{
		var folder = Directory.CreateTempSubdirectory("braggarr-keys-");
		try
		{
			var keys = Path.Combine(folder.FullName, "keys");
			var secret = Protector(new FileSystemXmlRepository(Directory.CreateDirectory(keys), NullLoggerFactory.Instance)).Protect("the owner's credentials");

			var ours = new KeyRepository(ConfigFileSystem.Create(folder.FullName), keys);

			ours.GetAllElements().Should().ContainSingle().Which.Name.LocalName.Should().Be("key");
			Protector(ours).Unprotect(secret).Should().Be("the owner's credentials");
		}
		finally
		{
			folder.Delete(recursive: true);
		}
	}

	[Test]
	public void A_key_written_here_is_read_back_by_a_restarted_app()
	{
		var fs = new MockFileSystem();
		var secret = Protector(new KeyRepository(fs, "/config/keys")).Protect("token");

		fs.Directory.GetFiles("/config/keys").Should().ContainSingle().Which.Should().MatchRegex(@"/config/keys/key-[0-9a-f-]{36}\.xml$");
		Protector(new KeyRepository(fs, "/config/keys")).Unprotect(secret).Should().Be("token");
	}
}
