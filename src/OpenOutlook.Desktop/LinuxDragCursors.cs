using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace OpenOutlook.Desktop;

/// <summary>
/// Linux: drag-and-drop cursors from the desktop's cursor theme. Avalonia's X11 back end draws its drag cursors from the old X cursor font
/// (DragMove = diamond_cross, which Yaru shows as a plus sign; DragCopy = center_ptr), so a drag showed no move or copy symbol (owner, 2026-10-21).
/// When Avalonia's drag code puts one of those on a window, the theme's dnd-move / dnd-copy / dnd-link / dnd-no-drop is set in its place,
/// loaded through libXcursor with the X server's theme and size (Xcursor.theme / Xcursor.size, which GNOME sets for X apps).
/// Anything missing (no X display, no libXcursor, a theme without the cursor) leaves Avalonia's cursor as it was.
/// </summary>
internal static class LinuxDragCursors
{
    /// <summary>Avalonia's drag cursor (its name) -> the theme's cursor names, best first.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> ThemeNames = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [nameof(StandardCursorType.DragMove)] = ["dnd-move", "move"],
        [nameof(StandardCursorType.DragCopy)] = ["dnd-copy", "copy"],
        [nameof(StandardCursorType.DragLink)] = ["dnd-link", "alias", "link"],
        [nameof(StandardCursorType.No)] = ["dnd-no-drop", "no-drop", "not-allowed"],      // Avalonia shows No over a place that takes no drop (the app uses No nowhere else)
    };

    private static readonly Dictionary<string, Cursor?> Loaded = new(StringComparer.Ordinal);
    private static Func<string, Cursor?>? _loaderOverride;
    private static bool _attached;

    public static void Install()
    {
        if (OperatingSystem.IsLinux()) Attach();
    }

    /// <summary>Test seam: a loader in place of libXcursor (null restores it); the cache is emptied.</summary>
    internal static void SetLoaderForTests(Func<string, Cursor?>? loader)
    {
        _loaderOverride = loader;
        Loaded.Clear();
        Attach();
    }

    private static void Attach()
    {
        if (_attached) return;
        _attached = true;
        ((IObservable<AvaloniaPropertyChangedEventArgs>)InputElement.CursorProperty.Changed).AddClassHandler<TopLevel>((top, e) => Replace(top, e.NewValue as Cursor));
    }

    private static void Replace(TopLevel top, Cursor? cursor)
    {
        // Cursor(StandardCursorType).ToString() is the type's name; a themed cursor's is not, so setting it here does not come back round
        if (cursor is null || !ThemeNames.ContainsKey(cursor.ToString())) return;
        if (Get(cursor.ToString()) is { } themed) top.Cursor = themed;
    }

    private static Cursor? Get(string key)
    {
        if (Loaded.TryGetValue(key, out var cached)) return cached;
        Cursor? cursor = null;
        foreach (var name in ThemeNames[key])
        {
            try { cursor = _loaderOverride is { } load ? load(name) : Xcursor.Load(name); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException or ArgumentException)
            { AppLog.Note("cursor", $"theme cursor {name} could not be loaded: {e.Message}"); }
            if (cursor is not null) break;
        }
        return Loaded[key] = cursor;
    }

    /// <summary>The theme's cursor image through libXcursor, the lookup every X program uses (theme inheritance, nearest size).</summary>
    private static class Xcursor
    {
        private static (string? Theme, int Size)? _settings;

        public static Cursor? Load(string name)
        {
            var (theme, size) = _settings ??= ReadSettings();
            var image = XcursorLibraryLoadImage(name, theme, size > 0 ? size : 24);
            if (image == IntPtr.Zero) return null;
            try
            {
                var img = Marshal.PtrToStructure<XcursorImage>(image);
                if (img.width is 0 or > 512 || img.height is 0 or > 512) return null;
                var pixels = new int[img.width * img.height];          // premultiplied ARGB words = BGRA bytes on x86 / ARM little-endian
                Marshal.Copy(img.pixels, pixels, 0, pixels.Length);
                var bitmap = new WriteableBitmap(new PixelSize((int)img.width, (int)img.height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var fb = bitmap.Lock())
                    for (var y = 0; y < img.height; y++) Marshal.Copy(pixels, y * (int)img.width, fb.Address + y * fb.RowBytes, (int)img.width);
                return new Cursor(bitmap, new PixelPoint((int)img.xhot, (int)img.yhot));
            }
            finally { XcursorImageDestroy(image); }
        }

        private static (string?, int) ReadSettings()
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return (Environment.GetEnvironmentVariable("XCURSOR_THEME"), int.TryParse(Environment.GetEnvironmentVariable("XCURSOR_SIZE"), out var s) ? s : 24);
            try { return (Marshal.PtrToStringUTF8(XcursorGetTheme(display)), XcursorGetDefaultSize(display)); }
            finally { XCloseDisplay(display); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XcursorImage { public uint version, size, width, height, xhot, yhot, delay; public IntPtr pixels; }

        [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
        [DllImport("libXcursor.so.1")] private static extern IntPtr XcursorGetTheme(IntPtr display);
        [DllImport("libXcursor.so.1")] private static extern int XcursorGetDefaultSize(IntPtr display);
        [DllImport("libXcursor.so.1")] private static extern IntPtr XcursorLibraryLoadImage([MarshalAs(UnmanagedType.LPUTF8Str)] string file, [MarshalAs(UnmanagedType.LPUTF8Str)] string? theme, int size);
        [DllImport("libXcursor.so.1")] private static extern void XcursorImageDestroy(IntPtr image);
    }
}
