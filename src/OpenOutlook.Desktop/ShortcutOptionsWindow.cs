using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace OpenOutlook.Desktop;

internal sealed class ShortcutOptionsWindow : Window
{
    private readonly Dictionary<string, MailShortcut> _bindings;
    private readonly Dictionary<string, Button> _captureButtons = new(StringComparer.Ordinal);
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private string? _capturing;

    public ShortcutOptionsWindow(ShortcutSettings settings)
    {
        Title = "Keyboard shortcuts — OpenOutlook";
        Width = 550;
        Height = 590;
        MinWidth = 430;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _bindings = ShortcutSettingsStore.Validate(settings).Bindings
            .ToDictionary(binding => binding.Action, StringComparer.Ordinal);
        AddHandler(InputElement.KeyDownEvent, CaptureKey, RoutingStrategies.Tunnel, handledEventsToo: true);

        var rows = new StackPanel { Spacing = 8 };
        foreach (var (action, label) in ShortcutSettingsStore.AvailableActions)
        {
            var capture = new Button { MinWidth = 125, Content = ShortcutSettingsStore.Display(
                _bindings.GetValueOrDefault(action)) };
            var clear = new Button { Content = "Clear" };
            _captureButtons[action] = capture;
            capture.Click += (_, _) =>
            {
                if (_capturing is { } previous && previous != action)
                    _captureButtons[previous].Content = ShortcutSettingsStore.Display(
                        _bindings.GetValueOrDefault(previous));
                _capturing = action;
                _status.Text = $"Press the keys for {label}. Esc cancels.";
                capture.Content = "Press keys…";
                capture.Focus();
            };
            clear.Click += (_, _) =>
            {
                _bindings.Remove(action);
                if (_capturing == action) _capturing = null;
                capture.Content = "Not assigned";
                _status.Text = $"Shortcut cleared for {label}.";
            };
            rows.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Children =
                {
                    new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
                    capture, clear
                }
            });
            Grid.SetColumn(capture, 1);
            Grid.SetColumn(clear, 2);
        }

        var save = new Button { Content = "Save", MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", MinWidth = 80 };
        var defaults = new Button { Content = "Restore defaults" };
        save.Click += (_, _) => Close(ShortcutSettingsStore.Validate(new ShortcutSettings
        {
            Bindings = ShortcutSettingsStore.AvailableActions
                .Where(item => _bindings.ContainsKey(item.Action))
                .Select(item => _bindings[item.Action]).ToArray()
        }));
        cancel.Click += (_, _) => Close(null);
        defaults.Click += (_, _) =>
        {
            _capturing = null;
            _bindings.Clear();
            foreach (var binding in new ShortcutSettings().Bindings) _bindings[binding.Action] = binding;
            foreach (var (action, button) in _captureButtons)
                button.Content = ShortcutSettingsStore.Display(_bindings.GetValueOrDefault(action));
            _status.Text = "Default shortcuts restored. Select Save to keep them.";
        };

        var instructions = new StackPanel { Spacing = 8, Children =
        {
            new TextBlock { Text = "Select a shortcut, then press the keys you want to use.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new TextBlock { Text = "Shortcuts work while the message list has keyboard focus.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap }
        } };
        var footer = new StackPanel { Spacing = 10, Children =
        {
            _status,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { defaults, cancel, save } }
        } };
        var list = new ScrollViewer { Content = rows, Margin = new Thickness(0, 12, 0, 12) };
        Grid.SetRow(list, 1);
        Grid.SetRow(footer, 2);
        Content = new Grid { Margin = new Thickness(18), RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children = { instructions, list, footer } };
    }

    private void CaptureKey(object? sender, KeyEventArgs e)
    {
        if (_capturing is not { } action) return;
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            _captureButtons[action].Content = ShortcutSettingsStore.Display(_bindings.GetValueOrDefault(action));
            _capturing = null;
            _status.Text = "Shortcut capture canceled.";
            return;
        }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (e.Key is Key.None or Key.Tab or Key.Enter)
        {
            _status.Text = "Choose another key; Tab and Enter are reserved for navigation.";
            return;
        }
        var shortcut = new MailShortcut(action, e.Key, e.KeyModifiers);
        if (_bindings.Values.Any(existing => existing.Action != action &&
            existing.Key == shortcut.Key && existing.Modifiers == shortcut.Modifiers))
        {
            _status.Text = "Those keys are already assigned to another action. Choose different keys.";
            return;
        }
        _bindings[action] = shortcut;
        _captureButtons[action].Content = ShortcutSettingsStore.Display(shortcut);
        _capturing = null;
        _status.Text = $"{ShortcutSettingsStore.Display(shortcut)} assigned. Select Save to keep it.";
    }
}
