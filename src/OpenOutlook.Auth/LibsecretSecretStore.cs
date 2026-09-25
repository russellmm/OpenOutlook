using System.Runtime.InteropServices;

namespace OpenOutlook.Auth;

/// <summary>
/// Linux Secret Service storage through the system libsecret library. Calls run off the UI thread.
/// A missing, locked, or session-only default collection is rejected before every operation.
/// </summary>
public sealed class LibsecretSecretStore : ISecretStore
{
    private const string Unavailable = "A persistent unlocked default keyring is required for account tokens.";
    private const string Failure = "The keyring operation failed; account tokens were not changed.";

    /// <summary>Checks the persistent unlocked collection before starting browser authorization.</summary>
    public Task CheckAvailabilityAsync(CancellationToken cancellationToken = default) => Run(() =>
    {
        using var context = new NativeContext(cancellationToken);
        context.RequirePersistentCollection();
    }, cancellationToken);

    public Task StoreRefreshTokenAsync(OAuthProvider provider, string account, string refreshToken, CancellationToken cancellationToken = default)
    {
        SecretStoreKeys.Validate(provider, account);
        SecretStoreKeys.ValidateToken(refreshToken);
        return Run(() =>
        {
            using var context = new NativeContext(cancellationToken);
            context.RequirePersistentCollection();
            var error = IntPtr.Zero;
            try
            {
                var stored = Native.secret_password_store_sync(context.Schema, "default", "OpenOutlook refresh token",
                    refreshToken, context.Cancellable, out error,
                    "provider", ProviderKey(provider), "account", account, IntPtr.Zero);
                if (error != IntPtr.Zero || stored == 0) throw new SecretStoreException(Failure);
            }
            finally { Native.FreeError(error); }
        }, cancellationToken);
    }

    public Task<string?> GetRefreshTokenAsync(OAuthProvider provider, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreKeys.Validate(provider, account);
        return Run(() =>
        {
            using var context = new NativeContext(cancellationToken);
            context.RequirePersistentCollection();
            var error = IntPtr.Zero;
            var password = IntPtr.Zero;
            try
            {
                password = Native.secret_password_lookup_sync(context.Schema, context.Cancellable, out error,
                    "provider", ProviderKey(provider), "account", account, IntPtr.Zero);
                if (error != IntPtr.Zero) throw new SecretStoreException(Failure);
                var value = password == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(password);
                if (value is not null) SecretStoreKeys.ValidateToken(value);
                return value;
            }
            catch (ArgumentException) { throw new SecretStoreException("The keyring returned an invalid token."); }
            finally
            {
                if (password != IntPtr.Zero) Native.secret_password_free(password);
                Native.FreeError(error);
            }
        }, cancellationToken);
    }

    public Task DeleteRefreshTokenAsync(OAuthProvider provider, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreKeys.Validate(provider, account);
        return Run(() =>
        {
            using var context = new NativeContext(cancellationToken);
            context.RequirePersistentCollection();
            var error = IntPtr.Zero;
            try
            {
                // A false result also means the item did not exist; both outcomes satisfy deletion.
                Native.secret_password_clear_sync(context.Schema, context.Cancellable, out error,
                    "provider", ProviderKey(provider), "account", account, IntPtr.Zero);
                if (error != IntPtr.Zero) throw new SecretStoreException(Failure);
            }
            finally { Native.FreeError(error); }
        }, cancellationToken);
    }

    private static string ProviderKey(OAuthProvider provider) => provider switch
    {
        OAuthProvider.Google => "google",
        OAuthProvider.MicrosoftConsumers => "microsoft-consumers",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static async Task Run(Action work, CancellationToken cancellationToken) =>
        await Run(() => { work(); return true; }, cancellationToken).ConfigureAwait(false);

    private static async Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) throw new SecretStoreException(Unavailable);
        try { return await Task.Run(work, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SecretStoreException) { throw; }
        catch (DllNotFoundException) { throw new SecretStoreException(Unavailable); }
        catch (EntryPointNotFoundException) { throw new SecretStoreException(Unavailable); }
        catch (Exception) { throw new SecretStoreException(Failure); }
    }

    private sealed class NativeContext : IDisposable
    {
        private readonly CancellationTokenRegistration _registration;
        public IntPtr Cancellable { get; }
        public IntPtr Schema { get; }

        public NativeContext(CancellationToken cancellationToken)
        {
            Cancellable = Native.g_cancellable_new();
            if (Cancellable == IntPtr.Zero) throw new SecretStoreException(Unavailable);
            try
            {
                _registration = cancellationToken.Register(static state => Native.g_cancellable_cancel((IntPtr)state!), Cancellable);
                Schema = Native.secret_schema_new("org.openoutlook.RefreshToken", 0,
                    "provider", 0, "account", 0, IntPtr.Zero);
                if (Schema == IntPtr.Zero) throw new SecretStoreException(Unavailable);
            }
            catch
            {
                _registration.Dispose();
                Native.g_object_unref(Cancellable);
                throw;
            }
        }

        public void RequirePersistentCollection()
        {
            var error = IntPtr.Zero;
            var service = IntPtr.Zero;
            var collection = IntPtr.Zero;
            var session = IntPtr.Zero;
            try
            {
                service = Native.secret_service_get_sync(0, Cancellable, out error);
                if (error != IntPtr.Zero || service == IntPtr.Zero) throw new SecretStoreException(Unavailable);
                collection = Native.secret_collection_for_alias_sync(service, "default", 0, Cancellable, out error);
                if (error != IntPtr.Zero || collection == IntPtr.Zero) throw new SecretStoreException(Unavailable);
                if (Native.secret_collection_get_locked(collection) != 0) throw new SecretStoreException(Unavailable);
                session = Native.secret_collection_for_alias_sync(service, "session", 0, Cancellable, out error);
                if (error != IntPtr.Zero) throw new SecretStoreException(Unavailable);
                if (session != IntPtr.Zero)
                {
                    var defaultPath = Marshal.PtrToStringUTF8(Native.g_dbus_proxy_get_object_path(collection));
                    var sessionPath = Marshal.PtrToStringUTF8(Native.g_dbus_proxy_get_object_path(session));
                    if (string.IsNullOrEmpty(defaultPath) || defaultPath == sessionPath)
                        throw new SecretStoreException(Unavailable);
                }
            }
            finally
            {
                if (collection != IntPtr.Zero) Native.g_object_unref(collection);
                if (session != IntPtr.Zero) Native.g_object_unref(session);
                if (service != IntPtr.Zero) Native.g_object_unref(service);
                Native.FreeError(error);
            }
        }

        public void Dispose()
        {
            _registration.Dispose();
            if (Schema != IntPtr.Zero) Native.secret_schema_unref(Schema);
            if (Cancellable != IntPtr.Zero) Native.g_object_unref(Cancellable);
        }
    }

    private static class Native
    {
        private const string Secret = "libsecret-1.so.0";
        private const string Gio = "libgio-2.0.so.0";
        private const string GObject = "libgobject-2.0.so.0";
        private const string Glib = "libglib-2.0.so.0";

        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr secret_schema_new([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute1, int type1,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute2, int type2, IntPtr terminator);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)] internal static extern void secret_schema_unref(IntPtr schema);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr secret_service_get_sync(int flags, IntPtr cancellable, out IntPtr error);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr secret_collection_for_alias_sync(IntPtr service,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string alias, int flags, IntPtr cancellable, out IntPtr error);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)] internal static extern int secret_collection_get_locked(IntPtr collection);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int secret_password_store_sync(IntPtr schema,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string collection,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string password, IntPtr cancellable, out IntPtr error,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute1, [MarshalAs(UnmanagedType.LPUTF8Str)] string value1,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute2, [MarshalAs(UnmanagedType.LPUTF8Str)] string value2,
            IntPtr terminator);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr secret_password_lookup_sync(IntPtr schema, IntPtr cancellable, out IntPtr error,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute1, [MarshalAs(UnmanagedType.LPUTF8Str)] string value1,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute2, [MarshalAs(UnmanagedType.LPUTF8Str)] string value2,
            IntPtr terminator);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int secret_password_clear_sync(IntPtr schema, IntPtr cancellable, out IntPtr error,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute1, [MarshalAs(UnmanagedType.LPUTF8Str)] string value1,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute2, [MarshalAs(UnmanagedType.LPUTF8Str)] string value2,
            IntPtr terminator);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)] internal static extern void secret_password_free(IntPtr password);
        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_cancellable_new();
        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_cancellable_cancel(IntPtr cancellable);
        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_dbus_proxy_get_object_path(IntPtr proxy);
        [DllImport(GObject, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_object_unref(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] private static extern void g_error_free(IntPtr error);
        internal static void FreeError(IntPtr error) { if (error != IntPtr.Zero) g_error_free(error); }
    }
}
