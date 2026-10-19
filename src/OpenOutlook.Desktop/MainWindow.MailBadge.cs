using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace OpenOutlook.Desktop;

/// <summary>
/// The program icon (taskbar, dock, Alt+Tab) shows a red dot while mail has arrived that the owner has not looked at: an Inbox's unread number grew
/// during a check while this window was not the active one. Coming back to the window takes the dot away. On Linux the dock (Ubuntu's, Dash to Dock,
/// KDE) takes its icon from the desktop entry, so there the number of new messages goes to the dock icon as a badge through the Unity launcher-entry
/// D-Bus signal (sent with gdbus, which Ubuntu ships); Windows shows the changed window icon in its taskbar.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, int> _inboxUnreadSeen = new(StringComparer.Ordinal);
    private WindowIcon? _plainIcon, _newMailIcon;
    private bool _newMailBadgeOn;
    private int _newMailCount;        // messages that arrived since the window was last active

    /// <summary>Called with an account's Inbox unread number after every check; the first number of an account is only a baseline.</summary>
    private void NoteInboxUnread(string accountId, int unread)
    {
        var grew = _inboxUnreadSeen.TryGetValue(accountId, out var before) && unread > before;
        _inboxUnreadSeen[accountId] = unread;
        if (!grew || IsActive) return;
        _newMailCount += unread - before;
        SetNewMailBadge(true);
        SetLauncherBadge(_newMailCount);
    }

    private void StartNewMailBadge() => Activated += (_, _) =>
    {
        if (_newMailCount == 0 && !_newMailBadgeOn) return;
        _newMailCount = 0;
        SetNewMailBadge(false);
        SetLauncherBadge(0);
    };

    private static Tmds.DBus.Protocol.Connection? _launcherBus;

    /// <summary>The count on the dock icon (0 removes it). Linux only; no session bus or a dock that ignores the signal is not an error. The dock drops
    /// the badge when the sender's bus connection closes, so the connection stays open for the life of the program (a one-shot gdbus emit shows nothing).</summary>
    private static void SetLauncherBadge(int count)
    {
        if (!OperatingSystem.IsLinux() || Tmds.DBus.Protocol.Address.Session is not { } address) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (_launcherBus is null)
                {
                    var bus = new Tmds.DBus.Protocol.Connection(address);
                    await bus.ConnectAsync();
                    _launcherBus = bus;
                }
                _launcherBus.TrySendMessage(BuildBadgeMessage(_launcherBus, count));
            }
            catch (Exception e)
            {
                _launcherBus = null;
                AppLog.Note("new-mail", "the dock badge could not be set: " + e.Message);
            }
        });
    }

    private static Tmds.DBus.Protocol.MessageBuffer BuildBadgeMessage(Tmds.DBus.Protocol.Connection bus, int count)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteSignalHeader(null, "/com/canonical/unity/launcherentry/1", "com.canonical.Unity.LauncherEntry", "Update", "sa{sv}");
        writer.WriteString("application://openoutlook.desktop");
        var entries = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("count");
        writer.WriteVariantInt64(count);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("count-visible");
        writer.WriteVariantBool(count > 0);
        writer.WriteDictionaryEnd(entries);
        return writer.CreateMessage();
    }

    private void SetNewMailBadge(bool on)
    {
        if (_newMailBadgeOn == on) return;
        try
        {
            _plainIcon ??= Icon;
            if (on) _newMailIcon ??= new WindowIcon(DrawBadgedIcon());
            Icon = on ? _newMailIcon : _plainIcon;
            _newMailBadgeOn = on;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or NotSupportedException)
        {
            AppLog.Note("new-mail", "the new-mail icon could not be drawn: " + e.Message);
        }
    }

    /// <summary>The program's icon with a red dot with a white rim in its upper right corner.</summary>
    private static RenderTargetBitmap DrawBadgedIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://OpenOutlook.Desktop/Assets/openoutlook.png"));
        using var plain = new Bitmap(stream);
        var size = new PixelSize(256, 256);
        var target = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            context.DrawImage(plain, new Rect(plain.Size), new Rect(0, 0, 256, 256));
            var centre = new Point(190, 66);
            context.DrawEllipse(Brushes.White, null, centre, 64, 64);
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE0, 0x24, 0x24)), null, centre, 50, 50);
        }
        return target;
    }
}
