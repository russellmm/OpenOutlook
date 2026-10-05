using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace OpenOutlook.Auth;

/// <summary>
/// Windows storage in Credential Manager (the per-user Windows vault, encrypted with the signed-in user's credentials). Tokens are never written to
/// ordinary files. A credential blob holds at most 2560 bytes, so a longer refresh token is split over several numbered credentials.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const string Failure = "Windows Credential Manager failed; account tokens were not changed.";
    private const int ChunkBytes = 2000;
    private const int MaxChunks = 32;
    private readonly string _prefix;

    /// <param name="targetPrefix">Prefix of the credential names ("OpenOutlook" in the app; tests use their own so they cannot touch real accounts).</param>
    public WindowsCredentialSecretStore(string targetPrefix = "OpenOutlook")
    {
        if (string.IsNullOrWhiteSpace(targetPrefix) || targetPrefix.Length > 64 || targetPrefix.Any(char.IsControl))
            throw new ArgumentException("Invalid credential prefix.", nameof(targetPrefix));
        _prefix = targetPrefix;
    }

    public Task CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new SecretStoreException("Windows Credential Manager is only available on Windows.");
        return Task.CompletedTask;
    }

    public Task StoreRefreshTokenAsync(OAuthProvider provider, string account, string refreshToken, CancellationToken cancellationToken = default)
    {
        SecretStoreKeys.Validate(provider, account);
        SecretStoreKeys.ValidateToken(refreshToken);
        return Task.Run(() =>
        {
            var bytes = Encoding.UTF8.GetBytes(refreshToken);
            var chunks = (bytes.Length + ChunkBytes - 1) / ChunkBytes;
            if (chunks > MaxChunks) throw new SecretStoreException("The refresh token is too large to store.");
            for (var i = 0; i < chunks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = bytes.AsSpan(i * ChunkBytes, Math.Min(ChunkBytes, bytes.Length - i * ChunkBytes)).ToArray();
                Write(Target(provider, account, i), account, part);
            }
            for (var i = chunks; i < MaxChunks; i++)                      // a shorter token than before: drop the old tail
                if (!Delete(Target(provider, account, i), missingIsFine: true)) break;
        }, cancellationToken);
    }

    public Task<string?> GetRefreshTokenAsync(OAuthProvider provider, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreKeys.Validate(provider, account);
        return Task.Run(() =>
        {
            var all = new List<byte>();
            for (var i = 0; i < MaxChunks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = Read(Target(provider, account, i));
                if (part is null) break;
                all.AddRange(part);
            }
            if (all.Count == 0) return null;
            try
            {
                var token = Encoding.UTF8.GetString(all.ToArray());
                SecretStoreKeys.ValidateToken(token);
                return token;
            }
            catch (ArgumentException) { throw new SecretStoreException("Credential Manager returned an invalid token."); }
        }, cancellationToken);
    }

    public Task DeleteRefreshTokenAsync(OAuthProvider provider, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreKeys.Validate(provider, account);
        return Task.Run(() =>
        {
            for (var i = 0; i < MaxChunks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Delete(Target(provider, account, i), missingIsFine: true)) break;
            }
        }, cancellationToken);
    }

    private string Target(OAuthProvider provider, string account, int chunk) =>
        $"{_prefix}/{(provider == OAuthProvider.Google ? "google" : "microsoft")}/{account}/{chunk}";

    // ---- advapi32 ----
    private const uint CredTypeGeneric = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private static void Write(string target, string userName, byte[] blob)
    {
        var pin = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, pin, blob.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric, TargetName = target, CredentialBlobSize = (uint)blob.Length, CredentialBlob = pin,
                Persist = PersistLocalMachine, UserName = userName, Comment = "OpenOutlook refresh token"
            };
            if (!CredWrite(ref credential, 0)) throw new SecretStoreException(Failure);
        }
        finally
        {
            Marshal.Copy(new byte[blob.Length], 0, pin, blob.Length);     // do not leave the token in unmanaged memory
            Marshal.FreeHGlobal(pin);
        }
    }

    private static byte[]? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var ptr))
        {
            if (Marshal.GetLastWin32Error() == ErrorNotFound) return null;
            throw new SecretStoreException(Failure);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(ptr);
            var blob = new byte[credential.CredentialBlobSize];
            if (blob.Length > 0) Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return blob;
        }
        finally { CredFree(ptr); }
    }

    /// <returns>true when a credential was deleted, false when there was none.</returns>
    private static bool Delete(string target, bool missingIsFine)
    {
        if (CredDelete(target, CredTypeGeneric, 0)) return true;
        if (Marshal.GetLastWin32Error() == ErrorNotFound && missingIsFine) return false;
        throw new SecretStoreException(Failure);
    }
}
