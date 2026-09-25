using System.Text.Json;
using OpenOutlook.JunkCleaner;

namespace OpenOutlook.Tests;

public sealed class JunkCleanerSettingsTests
{
    [Fact]
    public void Missing_config_does_not_create_file_or_enable_accounts()
    {
        using var fixture = new TemporaryDirectory();
        var store = new JunkCleanerSettingsStore(Path.Combine(fixture.Path, "app"));
        Assert.Empty(store.Load().Accounts);
        Assert.False(File.Exists(store.ConfigPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(store.ConfigPath)));
    }

    [Fact]
    public void Save_and_load_preserve_per_account_opt_in_and_normalized_keywords()
    {
        using var fixture = new TemporaryDirectory();
        var store = new JunkCleanerSettingsStore(Path.Combine(fixture.Path, "app"));
        store.Save(new JunkCleanerSettings
        {
            Accounts =
            [
                new JunkCleanerAccountSettings { AccountId = "account-1", Enabled = true,
                    Keywords = ["  Offer ", "offer", "", "  ", "Deal"], IntervalMinutes = 60,
                    Rules = new JunkRuleOptions(DeleteMissingTo: true) },
                new JunkCleanerAccountSettings { AccountId = "account-2" }
            ]
        });
        var accounts = store.Load().Accounts;
        Assert.Equal(2, accounts.Count);
        Assert.True(accounts[0].Enabled);
        Assert.Equal(["Offer", "Deal"], accounts[0].Keywords);
        Assert.Equal(60, accounts[0].IntervalMinutes);
        Assert.True(accounts[0].Rules.DeleteMissingTo);
        Assert.False(accounts[1].Enabled);
        Assert.Equal(15, accounts[1].IntervalMinutes);
        Assert.Equal(new JunkRuleOptions(), accounts[1].Rules);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.ConfigPath)!, "*.tmp"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Out_of_range_interval_is_rejected_without_overwriting_existing_config(int interval)
    {
        using var fixture = new TemporaryDirectory();
        var store = new JunkCleanerSettingsStore(fixture.Path);
        store.Save(new JunkCleanerSettings());
        var original = File.ReadAllText(store.ConfigPath);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(new JunkCleanerSettings
        {
            Accounts = [new JunkCleanerAccountSettings { AccountId = "account", IntervalMinutes = interval }]
        }));
        Assert.Equal(original, File.ReadAllText(store.ConfigPath));
    }

    [Fact]
    public void Duplicate_account_ids_are_rejected()
    {
        using var fixture = new TemporaryDirectory();
        var store = new JunkCleanerSettingsStore(fixture.Path);
        Assert.Throws<ArgumentException>(() => store.Save(new JunkCleanerSettings
        {
            Accounts = [new JunkCleanerAccountSettings { AccountId = "account" },
                        new JunkCleanerAccountSettings { AccountId = "account" }]
        }));
        Assert.False(File.Exists(store.ConfigPath));
    }

    [Fact]
    public void Invalid_existing_config_is_not_replaced_by_load()
    {
        using var fixture = new TemporaryDirectory();
        var store = new JunkCleanerSettingsStore(fixture.Path);
        File.WriteAllText(store.ConfigPath, "{ invalid json");
        Assert.Throws<JsonException>(() => store.Load());
        Assert.Equal("{ invalid json", File.ReadAllText(store.ConfigPath));
    }

    [Fact]
    public void Legacy_preview_is_explicit_read_only_and_import_remains_disabled()
    {
        using var fixture = new TemporaryDirectory();
        var legacyPath = Path.Combine(fixture.Path, "legacy.json");
        File.WriteAllText(legacyPath, """
            { "keywords": ["  Red ", "red", "Blue", " "], "alwaysClean": true,
              "intervalMinutes": 90, "deleteHighImportance": true, "deleteMissingTo": true,
              "deleteOnBehalfOf": true, "startWithWindows": true, "startInTray": true }
            """);
        var store = new JunkCleanerSettingsStore(Path.Combine(fixture.Path, "app"));
        Assert.Empty(store.Load().Accounts); // No automatic legacy lookup.
        var preview = JunkCleanerSettingsStore.PreviewLegacyConfig(legacyPath);
        Assert.Equal(["Red", "Blue"], preview.Keywords);
        Assert.True(preview.AlwaysClean);
        Assert.Equal(60, preview.IntervalMinutes);
        Assert.Equal(new JunkRuleOptions(true, true, true), preview.Rules);
        Assert.False(File.Exists(store.ConfigPath));

        var imported = preview.ImportForAccount("account-1");
        Assert.False(imported.Enabled); // Legacy alwaysClean never opts an account in.
        store.Save(new JunkCleanerSettings { Accounts = [imported] });
        var json = File.ReadAllText(store.ConfigPath);
        Assert.DoesNotContain("alwaysClean", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("startWithWindows", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("startInTray", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Red", "Blue"], store.Load().Accounts[0].Keywords);
    }

    [Fact]
    public void Legacy_missing_optional_rules_default_off_and_interval_clamps_low()
    {
        using var fixture = new TemporaryDirectory();
        var path = Path.Combine(fixture.Path, "legacy.json");
        File.WriteAllText(path, "{\"intervalMinutes\":0,\"keywords\":null}");
        var preview = JunkCleanerSettingsStore.PreviewLegacyConfig(path);
        Assert.Equal(1, preview.IntervalMinutes);
        Assert.Empty(preview.Keywords);
        Assert.False(preview.AlwaysClean);
        Assert.Equal(new JunkRuleOptions(), preview.Rules);
        Assert.False(preview.ImportForAccount("account").Enabled);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "openoutlook-junk-tests-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
