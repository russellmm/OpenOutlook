using OpenOutlook.Auth;

namespace OpenOutlook.Tests;

public sealed class ConnectedAccountRegistryTests
{
    [Fact]
    public void VerifiedLabelsAndPublicClientIdsPersistWithoutTokens()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"openoutlook-accounts-{Guid.NewGuid():N}");
        try
        {
            var registry = new ConnectedAccountRegistry(root);
            var microsoft = new ConnectedAccount(OAuthProvider.MicrosoftConsumers, "graph-id", "owner@hotmail.test",
                "public-microsoft-client", DateTimeOffset.UtcNow);
            var google = new ConnectedAccount(OAuthProvider.Google, "owner@gmail.test", "owner@gmail.test",
                "public-google-client", DateTimeOffset.UtcNow);
            registry.Upsert(microsoft);
            registry.Upsert(google);

            var reloaded = new ConnectedAccountRegistry(root).Load();
            Assert.Equal([microsoft, google], reloaded);
            Assert.DoesNotContain("refresh_token", File.ReadAllText(registry.Path), StringComparison.OrdinalIgnoreCase);
            if (OperatingSystem.IsLinux())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(registry.Path));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(System.IO.Path.GetDirectoryName(registry.Path)!));
            }

            registry.Remove(OAuthProvider.MicrosoftConsumers, microsoft.AccountId);
            Assert.Equal([google], registry.Load());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CorruptOrOversizedRegistryCannotBeSilentlyOverwritten()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"openoutlook-accounts-{Guid.NewGuid():N}");
        try
        {
            var registry = new ConnectedAccountRegistry(root);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(registry.Path)!);
            File.WriteAllText(registry.Path, "[null]");
            Assert.Throws<InvalidDataException>(() => registry.Load());
            Assert.Throws<InvalidDataException>(() => registry.Upsert(new ConnectedAccount(
                OAuthProvider.Google, "user@gmail.test", "user@gmail.test", "public-client", DateTimeOffset.UtcNow)));
            Assert.Equal("[null]", File.ReadAllText(registry.Path));

            File.WriteAllText(registry.Path, new string('x', 65537));
            Assert.Throws<InvalidDataException>(() => registry.Load());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LimitsAccountsByProvider()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"openoutlook-accounts-{Guid.NewGuid():N}");
        try
        {
            var registry = new ConnectedAccountRegistry(root);
            for (var i = 0; i < 2; i++)
                registry.Upsert(new ConnectedAccount(OAuthProvider.Google, $"user{i}@gmail.test",
                    $"user{i}@gmail.test", "public-client", DateTimeOffset.UtcNow));
            Assert.Throws<InvalidDataException>(() => registry.Upsert(new ConnectedAccount(
                OAuthProvider.Google, "third@gmail.test", "third@gmail.test", "public-client", DateTimeOffset.UtcNow)));
            Assert.Equal(2, registry.Load().Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
