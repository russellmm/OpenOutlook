using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace OpenOutlook.Desktop;

/// <summary>
/// What a newly due reminder does besides showing the Reminders window (docs/reminders-plan.md section 4): a sound - Windows' own "Calendar Reminder"
/// sound (Control Panel > Sound > Calendar Reminder; the default beep when none is set), on Linux the desktop's alarm sound when canberra-gtk-play is
/// installed - and, on Windows, the taskbar button flashes until the window is looked at. Never throws.
/// </summary>
public static class ReminderAlert
{
    public static void PlaySound()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!PlaySoundW("Notification.Reminder", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault)) MessageBeep(MbIconAsterisk);
            }
            else if (OperatingSystem.IsLinux())
            {
                var start = new ProcessStartInfo("canberra-gtk-play") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                start.ArgumentList.Add("-i"); start.ArgumentList.Add("alarm-clock-elapsed");
                start.ArgumentList.Add("-d"); start.ArgumentList.Add("OpenOutlook reminder");
                Process.Start(start)?.Dispose();
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        { AppLog.Note("reminders", "reminder sound not played: " + e.Message); }
    }

    /// <summary>Flashes the window's taskbar button until it comes to the front (Windows; elsewhere nothing).</summary>
    public static void Flash(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
            var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = hwnd, Flags = FlashAll | FlashUntilForeground, Count = 0, Timeout = 0 };
            FlashWindowEx(ref info);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndAlias = 0x00010000, MbIconAsterisk = 0x40;
    private const uint FlashAll = 0x3, FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags; public uint Count; public uint Timeout; }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "PlaySoundW")] private static extern bool PlaySoundW(string sound, IntPtr module, uint flags);
    [DllImport("user32.dll")] private static extern bool MessageBeep(uint type);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
}
