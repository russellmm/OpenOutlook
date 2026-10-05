using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenOutlook.Desktop;

/// <summary>
/// Makes a ribbon tab squeeze down like Outlook's when the window gets narrower. The tab's groups are laid out in a row; when the row no longer fits, groups are
/// collapsed one after another from the right into a single button (the group's icon, its name and a drop-down arrow) whose drop-down holds the group's own
/// buttons. Widening the window expands them again. Nothing is rebuilt: the group's content is moved between the ribbon and the drop-down, so button names, handlers
/// and enabled states stay as they are. The scroll bar of the tab remains as the last resort when even the collapsed groups do not fit.
/// </summary>
public sealed class RibbonResponsiveLayout
{
    private sealed class Group
    {
        public required Border Border;
        public required Control Content;               // the group's own layout (buttons and caption)
        public required string Label;
        public double FullWidth;                       // measured while expanded, including the margin
        public bool Collapsed;
        public Thickness Padding, Margin, Border2;       // the group border as the style gives it, restored when the group opens again
        public Button? Button;
        public Flyout? Flyout;
    }

    private const double CollapsedWidth = 70;            // a collapsed group's button plus margin
    private readonly ScrollViewer _host;
    private readonly Panel _row;
    private readonly List<Group> _groups = [];
    private bool _pending;

    private RibbonResponsiveLayout(ScrollViewer host, Panel row)
    {
        _host = host;
        _row = row;
    }

    /// <summary>Attaches the behaviour to every ribbon tab whose content is a scroll viewer holding a horizontal row of group borders.</summary>
    public static IReadOnlyList<RibbonResponsiveLayout> AttachAll(TabControl ribbon)
    {
        var list = new List<RibbonResponsiveLayout>();
        foreach (var tab in ribbon.Items.OfType<TabItem>())
            if (tab.Content is ScrollViewer { Content: Panel row } host) list.Add(Attach(host, row));
        return list;
    }

    public static RibbonResponsiveLayout Attach(ScrollViewer host, Panel row)
    {
        var layout = new RibbonResponsiveLayout(host, row);
        host.PropertyChanged += (_, e) => { if (e.Property == Visual.BoundsProperty || e.Property == ScrollViewer.ViewportProperty) layout.Schedule(); };
        host.AttachedToVisualTree += (_, _) => layout.Schedule();
        host.LayoutUpdated += (_, _) => { if (layout._groups.Count == 0) layout.Schedule(); };
        return layout;
    }

    /// <summary>Re-evaluates which groups fit (for tests and for tab changes).</summary>
    public void Update() => Apply();

    private void Schedule()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.UIThread.Post(() => { _pending = false; Apply(); }, DispatcherPriority.Background);
    }

    private void Discover()
    {
        if (_groups.Count > 0) return;
        foreach (var child in _row.Children.OfType<Border>())
        {
            if (!child.Classes.Contains("olGroup") || child.Child is not Control content) continue;
            var label = content.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("olGroupLabel"))?.Text
                        ?? content.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("olCaption") || t.Classes.Contains("olSmallCaption"))?.Text    // a group without a caption is named after its first button
                        ?? "More";
            _groups.Add(new Group { Border = child, Content = content, Label = label });
        }
    }

    private void Apply()
    {
        Discover();
        if (_groups.Count == 0 || _host.Bounds.Width < 50) return;

        // widths of the groups that are showing in full
        foreach (var g in _groups.Where(g => !g.Collapsed))
        {
            g.Border.Measure(Size.Infinity);
            var w = g.Border.DesiredSize.Width + g.Border.Margin.Right + g.Border.Margin.Left;
            if (w > 0) g.FullWidth = w;
        }
        if (_groups.Any(g => g.FullWidth <= 0)) { Schedule(); return; }          // a group has not been laid out yet (hidden tab): try again when it is shown

        var available = _host.Bounds.Width - _row.Margin.Left - _row.Margin.Right - 4;
        var collapse = new bool[_groups.Count];
        double Total() => _groups.Select((g, i) => collapse[i] ? CollapsedWidth : g.FullWidth).Sum();
        for (var i = _groups.Count - 1; i >= 1 && Total() > available; i--) collapse[i] = true;     // from the right; the first group stays open as long as possible
        if (Total() > available) collapse[0] = true;

        for (var i = 0; i < _groups.Count; i++)
        {
            if (collapse[i] && !_groups[i].Collapsed) Collapse(_groups[i]);
            else if (!collapse[i] && _groups[i].Collapsed) Expand(_groups[i]);
        }
    }

    private void Collapse(Group g)
    {
        if (g.Button is null)
        {
            var icon = g.Content.GetLogicalDescendants().OfType<ContentControl>().FirstOrDefault(c => c.GetType() == typeof(ContentControl) && c.Template is not null);       // the icon (a plain ContentControl with its own template), not a button
            Control glyph = icon is null
                ? new TextBlock { Text = "…", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center }
                : new ContentControl { Template = icon.Template, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center };
            var caption = new TextBlock { Text = g.Label + " ▾", FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 70, HorizontalAlignment = HorizontalAlignment.Center };
            g.Button = new Button { Classes = { "olLarge" }, Content = new StackPanel { Children = { glyph, caption } }, Margin = new Thickness(0, 0, 0, 0) };
            ToolTip.SetTip(g.Button, g.Label);
            g.Flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.Standard };
            var flyout = g.Flyout;
            g.Button.Flyout = flyout;
        }
        g.Padding = g.Border.Padding; g.Margin = g.Border.Margin; g.Border2 = g.Border.BorderThickness;
        g.Border.Padding = new Thickness(0); g.Border.Margin = new Thickness(0, 0, 2, 0); g.Border.BorderThickness = new Thickness(0);       // a collapsed group needs no separator and little room
        g.Border.Child = null;
        var holder = new Border { Padding = new Thickness(6), Child = g.Content };
        holder.AddHandler(Button.ClickEvent, (_, _) => g.Flyout!.Hide(), handledEventsToo: true);
        g.Flyout!.Content = holder;
        g.Border.Child = g.Button;
        g.Collapsed = true;
    }

    private void Expand(Group g)
    {
        g.Flyout?.Hide();
        if (g.Flyout?.Content is Border holder) holder.Child = null;
        g.Flyout!.Content = null;
        g.Border.Child = g.Content;
        g.Border.Padding = g.Padding; g.Border.Margin = g.Margin; g.Border.BorderThickness = g.Border2;
        g.Collapsed = false;
    }

    /// <summary>Names of the groups that are collapsed right now (for tests).</summary>
    public IReadOnlyList<string> CollapsedGroups => _groups.Where(g => g.Collapsed).Select(g => g.Label).ToList();
    public int GroupCount => _groups.Count;
    /// <summary>What the layout currently knows, for diagnostics and test messages.</summary>
    public string Describe() => $"available {_host.Bounds.Width:0} (row margin {_row.Margin.Left + _row.Margin.Right:0}); " +
        string.Join(", ", _groups.Select(g => $"{g.Label}={(g.Collapsed ? "collapsed" : g.FullWidth.ToString("0"))}"));
}
