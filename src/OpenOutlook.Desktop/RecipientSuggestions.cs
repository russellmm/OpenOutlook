using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenOutlook.Desktop;

/// <summary>
/// Outlook's address suggestions under a To / Cc / Bcc box (owner's screenshot 2026-10-17): clicking into the box lists the most recently used
/// addresses, typing narrows them (contacts too); each row is an avatar with initials, the name and the address. Up / Down move, Enter or Tab
/// takes the highlighted one, Escape closes, a click takes the clicked one.
/// </summary>
public sealed class RecipientSuggestions
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x88, 0x17, 0x98), Color.FromRgb(0xD1, 0x34, 0x38), Color.FromRgb(0x7A, 0x75, 0x74), Color.FromRgb(0x00, 0x78, 0xD4),
        Color.FromRgb(0x10, 0x7C, 0x10), Color.FromRgb(0xCA, 0x50, 0x10), Color.FromRgb(0x00, 0x83, 0x7A), Color.FromRgb(0x5C, 0x2E, 0x91),
    ];

    private readonly TextBox _box;
    private readonly Func<IReadOnlyList<RecipientEntry>> _directory;
    private readonly Popup _popup;
    private readonly ListBox _list = new() { MaxHeight = 380, MinWidth = 420 };
    private bool _accepting;

    public RecipientSuggestions(TextBox box, Func<IReadOnlyList<RecipientEntry>> directory)
    {
        _box = box; _directory = directory;
        _list.ItemTemplate = new FuncDataTemplate<RecipientEntry>((e, _) => e is null ? new TextBlock() : Row(e));
        _list.AddHandler(InputElement.PointerReleasedEvent, (_, _) => { if (_list.SelectedItem is RecipientEntry e) Accept(e); }, RoutingStrategies.Bubble, handledEventsToo: true);
        _popup = new Popup
        {
            PlacementTarget = box, Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = false,
            Child = new Border
            {
                Background = Themed.Brush("OlCard", Brushes.White), BorderBrush = Themed.Brush("OlHairline", Brushes.LightGray), BorderThickness = new Thickness(1),
                BoxShadow = new BoxShadows(new BoxShadow { Blur = 8, OffsetY = 2, Color = Color.FromArgb(0x40, 0, 0, 0) }), Child = _list,
            },
        };
        ((ISetLogicalParent)_popup).SetParent(box);
        box.GotFocus += (_, _) => Update();
        box.TextChanged += (_, _) => { if (!_accepting && box.IsFocused) Update(); };
        box.LostFocus += (_, _) => Dispatcher.UIThread.Post(() => { if (!box.IsFocused && !_list.IsPointerOver) Close(); }, DispatcherPriority.Background);
        box.AddHandler(InputElement.KeyDownEvent, KeyDown, RoutingStrategies.Tunnel);
    }

    public bool IsOpen => _popup.IsOpen;
    internal IReadOnlyList<RecipientEntry> Shown => _list.ItemsSource?.OfType<RecipientEntry>().ToList() ?? [];

    /// <summary>Shows what fits the part being typed (the most recent addresses when nothing is typed yet).</summary>
    public void Update()
    {
        var all = _directory();
        var already = RecipientDirectory.Addresses(_box.Text).ToList();
        var token = RecipientDirectory.CurrentToken(_box.Text);
        var shown = RecipientDirectory.Suggest(all, token, token.Length == 0 ? already : already.Take(Math.Max(0, already.Count - 1)));
        _list.ItemsSource = shown;
        if (shown.Count == 0) { Close(); return; }
        _list.SelectedIndex = token.Length > 0 ? 0 : -1;
        _popup.IsOpen = true;
    }

    public void Close() => _popup.IsOpen = false;

    internal void Accept(RecipientEntry entry)
    {
        _accepting = true;
        try
        {
            _box.Text = RecipientDirectory.Accept(_box.Text, entry.Address);
            _box.CaretIndex = _box.Text.Length;
        }
        finally { _accepting = false; }
        Close();
        _box.Focus();
    }

    private void KeyDown(object? sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen)
        {
            if (e.Key == Key.Down) { Update(); e.Handled = _popup.IsOpen; }
            return;
        }
        var count = _list.ItemCount;
        switch (e.Key)
        {
            case Key.Down: _list.SelectedIndex = Math.Min(count - 1, _list.SelectedIndex + 1); e.Handled = true; break;
            case Key.Up: _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1); e.Handled = true; break;
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Enter or Key.Tab when _list.SelectedItem is RecipientEntry chosen: Accept(chosen); e.Handled = true; break;
        }
    }

    internal static Control Avatar(RecipientEntry e, double size = 36)
    {
        var hash = 0;
        foreach (var c in e.Address.ToLowerInvariant()) hash = unchecked(hash * 31 + c);
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = new SolidColorBrush(Palette[(hash & 0x7fffffff) % Palette.Length]),
            Child = new TextBlock { Text = e.Initials, Foreground = Brushes.White, FontSize = size * 0.4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    private static Control Row(RecipientEntry e) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(4, 3),
        Children =
        {
            Avatar(e),
            new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = e.Shown, FontSize = 15 },
                    new TextBlock { Text = e.Address, FontSize = 12.5, Foreground = Themed.Brush("OlTextSecondary", Brushes.Gray), IsVisible = !string.IsNullOrWhiteSpace(e.Name) },
                }
            },
        }
    };
}
