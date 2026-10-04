using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OpenPst
{
    /// <summary>
    /// Locates the native OpenPST library: next to the application, then runtimes/&lt;rid&gt;/native.
    /// <see cref="IsAvailable"/> is false (never throws) when the library is missing, so callers can fall back
    /// to the managed engine.
    /// </summary>
    public static class NativeLibraryLoader
    {
        /// <summary>Native library version this wrapper was written against (major.minor).</summary>
        public const string ExpectedVersionPrefix = "0.3";

        static readonly object Gate = new object();
        static bool _installed;
        static bool? _available;
        static string _version;

        static string RuntimeRid()
        {
            string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";
            string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            return os + "-" + arch;
        }

        static string FileName() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "openpst.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libopenpst.dylib" : "libopenpst.so";

        /// <summary>Registers the resolver for this assembly (idempotent).</summary>
        public static void Install()
        {
            lock (Gate)
            {
                if (_installed) return;
                _installed = true;
                NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, Resolve);
            }
        }

        static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
        {
            if (name != "openpst") return IntPtr.Zero;
            string file = FileName(), baseDir = AppContext.BaseDirectory;
            foreach (var candidate in new[]
            {
                Path.Combine(baseDir, file),
                Path.Combine(baseDir, "runtimes", RuntimeRid(), "native", file),
            })
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var h)) return h;
            return IntPtr.Zero; // fall through to the default probing
        }

        /// <summary>True when the library loads and its version matches <see cref="ExpectedVersionPrefix"/>.</summary>
        public static bool IsAvailable
        {
            get
            {
                lock (Gate)
                {
                    if (_available is bool a) return a;
                    Install();
                    try
                    {
                        _version = Marshal.PtrToStringUTF8(Native.opst_version());
                        _available = _version != null && _version.StartsWith(ExpectedVersionPrefix, StringComparison.Ordinal);
                    }
                    catch (Exception e) when (e is DllNotFoundException || e is BadImageFormatException || e is EntryPointNotFoundException)
                    {
                        _available = false;
                    }
                    return _available.Value;
                }
            }
        }

        /// <summary>Native library version string, or null when unavailable.</summary>
        public static string Version { get { _ = IsAvailable; return _version; } }
    }
}
