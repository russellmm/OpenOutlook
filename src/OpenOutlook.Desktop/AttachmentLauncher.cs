using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace OpenOutlook.Desktop;

/// <summary>
/// Opening an attachment directly: it is first written as a new file in a private temporary folder, then handed to the
/// operating system's default program. File types that can run code are never launched from here (they can still be saved).
/// </summary>
public static class AttachmentLauncher
{
    private static readonly string[] RiskyExtensions =
    [
        ".exe", ".com", ".scr", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".lnk", ".hta",
        ".jar", ".reg", ".cpl", ".dll", ".pif", ".gadget", ".application", ".appref-ms", ".msc", ".sh", ".appimage", ".desktop", ".command",
        ".docm", ".xlsm", ".pptm", ".dotm", ".xlam", ".iso", ".img", ".chm", ".url", ".scf", ".inf", ".apk", ".deb", ".rpm", ".app"
    ];

    private static string Root => Path.Combine(Path.GetTempPath(), "OpenOutlook-attachments");

    /// <summary>True when the file type is one that runs code (or a macro) when opened.</summary>
    public static bool IsRisky(string fileName)
    {
        var name = fileName.TrimEnd(' ', '.');
        return RiskyExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A fresh empty folder (owner-only on Unix) for one opened attachment.</summary>
    public static string NewDirectory()
    {
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return dir;
    }

    /// <summary>Removes the copies of attachments opened in earlier runs.</summary>
    public static void CleanUp()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a viewer still has one open: leave it */ }
    }

    /// <summary>Hands a file to the default program. Returns an error message, or null when it was launched.</summary>
    public static string? Launch(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return null;
            }
            var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            return process is null ? "Could not start a program to open this file." : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return "No program is set up to open this kind of file. Save it instead.";
        }
    }
}
