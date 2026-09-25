using System.Text.Json;

namespace OpenOutlook.Auth;

/// <summary>Public client IDs and verified account labels only. Refresh tokens belong in ISecretStore.</summary>
public sealed record ConnectedAccount(OAuthProvider Provider, string AccountId, string DisplayAddress,
    string ClientId, DateTimeOffset ConnectedAt);

/// <summary>Small interim local registry for the desktop onboarding milestone.</summary>
public sealed class ConnectedAccountRegistry
{
    public string Path { get; }

    public ConnectedAccountRegistry(string? dataRoot = null)
    {
        var root = dataRoot ?? Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(root))
            root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        Path = System.IO.Path.Combine(root, "OpenOutlook", "accounts.json");
    }

    public IReadOnlyList<ConnectedAccount> Load()
    {
        if (!File.Exists(Path)) return [];
        if (new FileInfo(Path).Length > 65536)
            throw new InvalidDataException("Account registry is too large.");
        try
        {
            var accounts = JsonSerializer.Deserialize<ConnectedAccount[]>(File.ReadAllText(Path))
                ?? throw new InvalidDataException("Account registry is invalid.");
            Validate(accounts);
            return accounts;
        }
        catch (JsonException) { throw new InvalidDataException("Account registry is invalid."); }
    }

    public void Save(IReadOnlyList<ConnectedAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        Validate(accounts);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = System.IO.Path.Combine(directory, $".accounts-{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
            };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, accounts);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Upsert(ConnectedAccount account)
    {
        Validate([account]);
        var accounts = Load().ToList();
        var index = accounts.FindIndex(existing => existing.Provider == account.Provider &&
            string.Equals(existing.AccountId, account.AccountId, StringComparison.Ordinal));
        if (index < 0) accounts.Add(account);
        else accounts[index] = account;
        Save(accounts);
    }

    public void Remove(OAuthProvider provider, string accountId)
    {
        SecretStoreKeys.Validate(provider, accountId);
        var accounts = Load().Where(account => account.Provider != provider ||
            !string.Equals(account.AccountId, accountId, StringComparison.Ordinal)).ToArray();
        Save(accounts);
    }

    private static void Validate(IReadOnlyList<ConnectedAccount> accounts)
    {
        if (accounts.Any(account => account is null) || accounts.Count > 4 ||
            accounts.GroupBy(account => account.Provider).Any(group => group.Count() > 2) ||
            accounts.DistinctBy(account => (account.Provider, account.AccountId)).Count() != accounts.Count)
            throw new InvalidDataException("Account registry exceeds the supported account limits.");
        foreach (var account in accounts)
        {
            try { SecretStoreKeys.Validate(account.Provider, account.AccountId); }
            catch (ArgumentException) { throw new InvalidDataException("Account registry contains an invalid account."); }
            if (string.IsNullOrWhiteSpace(account.DisplayAddress) || account.DisplayAddress.Length > 320 ||
                account.DisplayAddress != account.DisplayAddress.Trim() || account.DisplayAddress.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(account.ClientId) || account.ClientId.Length > 1024 ||
                account.ClientId != account.ClientId.Trim() || account.ClientId.Any(char.IsControl) ||
                account.ConnectedAt == default)
                throw new InvalidDataException("Account registry contains an invalid account.");
        }
    }
}
