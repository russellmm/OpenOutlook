using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenOutlook.JunkCleaner;

namespace OpenOutlook.Desktop;

/// <summary>
/// The Junk Cleaner's "Other rules..." window (owner 2026-10-25: one button instead of a long list of check boxes): every rule besides the keywords,
/// grouped by what it looks at, with Select all / Clear all, the crowd size, the bulk level and the company list. OK sets <see cref="Result"/>.
/// </summary>
public sealed class JunkRulesWindow : Window
{
    /// <summary>One rule: its key in <see cref="With"/>, group, check box text, short name for the summary line and tooltip.</summary>
    internal sealed record Rule(string Key, string Group, string Label, string Short, string Tip, Func<JunkRuleOptions, bool> Get);

    internal static readonly IReadOnlyList<Rule> Rules =
    [
        new("unauth", "Sender", "Sender is not verified (fails Microsoft's SPF / DKIM / DMARC checks)", "Sender not verified",
            "Real companies sign their mail. Uses Microsoft's own verdict (compauth) in the Authentication-Results header.", o => o.DeleteUnauthenticated),
        new("fake", "Sender", "Sender address is fake (impossible domain such as info@-----mail.x.com)", "Fake sender address",
            "A domain part that starts or ends with \"-\", is empty, or ends in something that is not a real ending (\".us-SnlvK6...\").", o => o.DeleteFakeSenderAddress),
        new("brand", "Sender", "Sender's name pretends to be a company the address does not belong to", "Pretends to be a company",
            "\"McAfee Account Services\" <info@mail.zkreemux2k.com>: the name says McAfee, the address does not. Uses the company list below.", o => o.DeleteImpersonation),
        new("behalf", "Sender", "Sent \"on behalf of\" someone else", "On behalf of",
            "The sending address differs from the author shown in From.", o => o.DeleteOnBehalfOf),
        new("free", "Sender", "Sender uses a free email service (Gmail, Yahoo, Outlook.com, AOL, iCloud...)", "Free email address",
            "Businesses do not send from free mail services; invoice and payment scams do. Friends do too: add them to Safe Senders.", o => o.DeleteFreeMail),
        new("noto", "Recipients", "No To address", "No To address", "The To line is empty.", o => o.DeleteMissingTo),
        new("nottome", "Recipients", "Not addressed to me (my address is not on the To line)", "Not addressed to me",
            "Junk that lists other people, or nobody, on its To line instead of this account's address. Cc and Bcc do not count. " +
            "Mail sent to another of your addresses (an alias) is not addressed to this account either, so it would be removed.", o => o.DeleteNotToMe),
        new("bcc", "Recipients", "Anyone in Bcc", "Bcc recipients",
            "Real mail never shows you its Bcc list; junk that names recipients in a Bcc field is a mass mailing.", o => o.DeleteWithBcc),
        new("crowd", "Recipients", "Sent to a crowd: at least this many people on To and Cc", "Sent to a crowd",
            "Spam sent from hijacked accounts lists hundreds of strangers.", o => o.DeleteCrowd),
        new("hidden", "Subject", "Hidden or reversed characters in the subject", "Hidden characters",
            "Invisible right-to-left or zero-width characters hide words from filters (\"gnidnatstuO llitS sI etadpU tnemyaP ruoY\").", o => o.DeleteHiddenText),
        new("script", "Subject", "Subject in another alphabet (Chinese, Korean, Cyrillic, Arabic...)", "Other alphabet",
            "Three or more letters of a non-Latin alphabet in the subject.", o => o.DeleteForeignScript),
        new("username", "Subject", "Subject or sender's name uses my username (the part of my address before @)", "Uses my username",
            "\"rmarrash Start Your Window Upgrade...\": mail that greets you by your mailbox name was sent to a list of harvested addresses.", o => o.DeleteUsernameInSubject),
        new("emoji", "Subject", "Subject starts with an emoji", "Emoji subject",
            "Most of the junk in the survey started with ⚡ 🎰 💊 🚨. Some real newsletters do it too.", o => o.DeleteEmojiSubject),
        new("code", "Subject", "Subject contains a long random code (K-#0BL9C5G1BC2_LE4PIUU...)", "Random code",
            "16 or more letters, digits, _ # - mixed together. Tracking and order numbers are mostly digits and are not matched.", o => o.DeleteRandomCode),
        new("high", "Message", "Marked High importance (any sender)", "High importance", "Importance set to High.", o => o.DeleteHighImportance),
        new("flagged", "Message", "Flagged", "Flagged", "Has a follow-up flag.", o => o.DeleteFlagged),
        new("invite", "Message", "Calendar invitation (\"Invitation: Auto-payment will be processed...\")", "Calendar invitation",
            "Scammers send fake meeting invitations with payment warnings from hacked accounts.", o => o.DeleteInvitations),
        new("bulk", "Message", "Microsoft rates it as bulk mail at this level or higher (1-9)", "Bulk rating",
            "Microsoft's Bulk Complaint Level (BCL): how often people complain about the sender. 5 or higher is heavy bulk mail.", o => o.DeleteBulk),
    ];

    internal static JunkRuleOptions With(JunkRuleOptions o, string key, bool on) => key switch
    {
        "unauth" => o with { DeleteUnauthenticated = on }, "fake" => o with { DeleteFakeSenderAddress = on },
        "brand" => o with { DeleteImpersonation = on }, "behalf" => o with { DeleteOnBehalfOf = on },
        "free" => o with { DeleteFreeMail = on }, "noto" => o with { DeleteMissingTo = on },
        "nottome" => o with { DeleteNotToMe = on }, "bcc" => o with { DeleteWithBcc = on },
        "crowd" => o with { DeleteCrowd = on }, "hidden" => o with { DeleteHiddenText = on },
        "script" => o with { DeleteForeignScript = on }, "username" => o with { DeleteUsernameInSubject = on },
        "emoji" => o with { DeleteEmojiSubject = on }, "code" => o with { DeleteRandomCode = on },
        "high" => o with { DeleteHighImportance = on }, "flagged" => o with { DeleteFlagged = on },
        "invite" => o with { DeleteInvitations = on }, "bulk" => o with { DeleteBulk = on },
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    /// <summary>"3 of 18 rules on: Sender not verified, Fake sender address, Emoji subject" for the Junk Cleaner tab.</summary>
    internal static string Summary(JunkRuleOptions o)
    {
        var on = Rules.Where(r => r.Get(o)).Select(r => r.Short).ToList();
        return on.Count == 0 ? $"No other rules are on (0 of {Rules.Count})." : $"{on.Count} of {Rules.Count} rules on: {string.Join(", ", on)}.";
    }

    private readonly Dictionary<string, CheckBox> _boxes = [];
    // Wide enough for the value beside the spinner's two buttons (90 cut the digit off, owner 2026-10-25).
    private readonly NumericUpDown _crowd = new() { Minimum = 2, Maximum = 1000, Increment = 1, MinWidth = 130, FormatString = "0" };
    private readonly NumericUpDown _bulk = new() { Minimum = 1, Maximum = 9, Increment = 1, MinWidth = 130, FormatString = "0" };
    private readonly TextBox _brands = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, MaxHeight = 140, FontSize = 12 };
    private readonly JunkRuleOptions _start;

    /// <summary>The rules and company list chosen with OK; null after Cancel.</summary>
    public (JunkRuleOptions Rules, IReadOnlyList<string>? Brands)? Result { get; private set; }
    internal IReadOnlyDictionary<string, CheckBox> Boxes => _boxes;
    internal NumericUpDown Crowd => _crowd;
    internal NumericUpDown Bulk => _bulk;
    internal TextBox Brands => _brands;

    public JunkRulesWindow(string address, JunkRuleOptions rules, IReadOnlyList<string>? brands)
    {
        _start = rules;
        Title = "Other rules - " + address;
        Width = 680; Height = 720; MinWidth = 520; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Themed.Brush("OlCard", Brushes.White);
        var text = Themed.Brush("OlText", Brushes.Black);
        var gray = Themed.Brush("OlTextSecondary", Brushes.Gray);

        var list = new StackPanel { Spacing = 4, Margin = new Thickness(14, 10, 14, 10) };
        list.Children.Add(new TextBlock
        {
            Text = "Junk that matches any rule ticked here is moved to Deleted Items (never deleted for good), along with junk whose sender matches a keyword. " +
                   "Mail from Safe Senders is never removed. Point at a rule for details.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = gray, Margin = new Thickness(0, 0, 0, 4)
        });
        foreach (var group in Rules.GroupBy(r => r.Group))
        {
            list.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeight.SemiBold, Foreground = text, Margin = new Thickness(0, 10, 0, 2) });
            foreach (var rule in group)
            {
                var box = new CheckBox { Content = rule.Label, IsChecked = rule.Get(rules), [ToolTip.TipProperty] = rule.Tip };
                _boxes[rule.Key] = box;
                list.Children.Add(rule.Key switch
                {
                    "crowd" => Row(box, _crowd, "people"),
                    "bulk" => Row(box, _bulk, null),
                    _ => box
                });
                if (rule.Key == "brand") list.Children.Add(BrandEditor(gray));
            }
        }
        _crowd.Value = rules.EffectiveCrowdSize;
        _bulk.Value = rules.EffectiveBulkLevel;
        _brands.Text = string.Join(", ", brands is { Count: > 0 } ? brands : JunkSignals.DefaultBrands);

        var selectAll = new Button { Content = "Select all", MinWidth = 100 };
        var clearAll = new Button { Content = "Clear all", MinWidth = 100 };
        var ok = new Button { Content = "OK", MinWidth = 100, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 100, IsCancel = true };
        selectAll.Click += (_, _) => SetAll(true);
        clearAll.Click += (_, _) => SetAll(false);
        ok.Click += (_, _) => { Accept(); Close(); };
        cancel.Click += (_, _) => Close();
        var buttons = new DockPanel { Margin = new Thickness(12), LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { selectAll, clearAll } };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { ok, cancel } };
        DockPanel.SetDock(left, Dock.Left); DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(left); buttons.Children.Add(right);
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Content = dock;
    }

    private static Control Row(CheckBox box, NumericUpDown number, string? after)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { box, number } };
        if (after is not null) row.Children.Add(new TextBlock { Text = after, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        return row;
    }

    private Control BrandEditor(IBrush gray)
    {
        var reset = new Button { Content = "Use the built-in list", FontSize = 12, Padding = new Thickness(8, 2) };
        reset.Click += (_, _) => _brands.Text = string.Join(", ", JunkSignals.DefaultBrands);
        return new StackPanel
        {
            Spacing = 4, Margin = new Thickness(28, 0, 0, 4),
            Children =
            {
                new TextBlock { Text = "Company names (separate them with commas). A sender whose domain contains the name, such as mcafee.com, is not matched.", FontSize = 12, Foreground = gray, TextWrapping = TextWrapping.Wrap },
                _brands, reset
            }
        };
    }

    internal void SetAll(bool on) { foreach (var box in _boxes.Values) box.IsChecked = on; }

    /// <summary>Reads the window into <see cref="Result"/>: the check boxes on top of the rules it opened with, the two numbers and the company list
    /// (the built-in list is stored as "none", so later additions to it reach the owner).</summary>
    internal void Accept()
    {
        var o = _boxes.Aggregate(_start, (acc, kv) => With(acc, kv.Key, kv.Value.IsChecked == true));
        o = o with { CrowdSize = Math.Clamp((int)(_crowd.Value ?? JunkSignals.DefaultCrowdSize), 2, 1000), BulkLevel = Math.Clamp((int)(_bulk.Value ?? JunkSignals.DefaultBulkLevel), 1, 9) };
        var brands = AccountSettingsWindow.ParseJunkKeywords(_brands.Text);
        var isDefault = brands.Count == 0 || brands.SequenceEqual(JunkSignals.DefaultBrands, StringComparer.OrdinalIgnoreCase);
        Result = (o, isDefault ? null : brands);
    }
}
