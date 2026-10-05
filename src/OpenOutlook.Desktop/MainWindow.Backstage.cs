using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using OpenOutlook.Auth;

namespace OpenOutlook.Desktop;

/// <summary>
/// The Backstage view: classic Outlook's File menu. It is not a ribbon page — selecting the File tab
/// replaces everything below the title bar with a grey nav column and a content pane, so the TabControl
/// keeps no content for it and the overlay in MainWindow.axaml is shown instead. Returning restores the
/// ribbon tab that was active before File was pressed; Esc does the same.
///
/// Commands that already exist elsewhere are routed to their real handlers (add account, open/detach a
/// PST, export EML, save attachments, printable PDF, appearance and shortcut settings). The rest match
/// the reference layout and report themselves as planned when clicked, so nothing pretends to work.
/// </summary>
public partial class MainWindow
{
    private int _ribbonTabBeforeBackstage = 1;

    private sealed record BackstageAccountOption(string Address, string Kind, string WebUrl);

    private void RibbonTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Read the index from the sender: this fires while InitializeComponent is still building the tab
        // strip (before fields like BackstageHost exist), so opening the backstage has to wait for load.
        if (sender is not TabControl tabs) return;
        if (tabs.SelectedIndex == 0)
        {
            if (IsLoaded) OpenBackstage("info");
        }
        else _ribbonTabBeforeBackstage = tabs.SelectedIndex;
    }

    private void OpenBackstage(string page)
    {
        PopulateBackstageAccounts();
        PopulateBackstageMailbox();
        BsVersionText.Text = "Version " + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0");
        BackstageHost.IsVisible = true;
        NavInfo.IsChecked = true;
        SelectBackstagePage(page);
        BackstageHost.Focus();
    }

    private void CloseBackstage()
    {
        BackstageHost.IsVisible = false;
        if (RibbonTabs.SelectedIndex == 0) RibbonTabs.SelectedIndex = _ribbonTabBeforeBackstage;
    }

    private void CloseBackstageClicked(object? sender, RoutedEventArgs e) => CloseBackstage();

    private void BackstageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseBackstage();
        e.Handled = true;
    }

    private void BackstageNavClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key }) SelectBackstagePage(key);
    }

    private void SelectBackstagePage(string key)
    {
        foreach (var page in new Control[]
                 { PageInfo, PageOpenExport, PageSaveAs, PageAttachments, PagePrint, PageOfficeAccount, PageFeedback })
            page.IsVisible = false;
        (key switch
        {
            "openexport" => PageOpenExport,
            "saveas" => PageSaveAs,
            "attachments" => PageAttachments,
            "print" => PagePrint,
            "officeaccount" => PageOfficeAccount,
            "feedback" => PageFeedback,
            _ => PageInfo
        }).IsVisible = true;
    }

    private void PopulateBackstageAccounts()
    {
        var options = new List<BackstageAccountOption>();
        try
        {
            foreach (var account in _accountRegistry.Load())
                options.Add(new BackstageAccountOption(
                    account.DisplayAddress,
                    account.Provider == OAuthProvider.MicrosoftConsumers ? "Microsoft Exchange" : "Google",
                    account.Provider == OAuthProvider.Google ? "https://mail.google.com/" : "https://outlook.live.com/owa/"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
        var previous = BackstageAccounts.SelectedItem as BackstageAccountOption;
        BackstageAccounts.ItemsSource = options;
        BackstageAccounts.SelectedItem = previous is not null && options.Contains(previous) ? previous : options.FirstOrDefault();
    }

    private void BackstageAccountSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: BackstageAccountOption option })
            BsWebLinkUrl.Text = option.WebUrl;
    }

    /// <summary>
    /// Mailbox usage, honestly sourced: for open PST archives the real file sizes against the 50 GB data
    /// file ceiling. Connected-account quota is a server-side number this client does not receive yet, so
    /// the bar stays hidden rather than showing an invented figure.
    /// </summary>
    private void PopulateBackstageMailbox()
    {
        BsUsageFiles.Children.Clear();
        var files = _stores.Keys.Where(File.Exists)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            BsUsageBar.IsVisible = false;
            BsUsageText.Text = "Mailbox usage is not available for connected accounts yet.";
            return;
        }
        const long limit = 50L * 1024 * 1024 * 1024; // the classic Outlook Data File ceiling
        long total = files.Sum(file => file.Length);
        double percent = Math.Clamp(total * 100.0 / limit, 1.0, 100.0);
        BsUsageBar.IsVisible = true;
        BsUsageGrid.ColumnDefinitions = new ColumnDefinitions(string.Create(CultureInfo.InvariantCulture,
            $"{percent:0.##}*,{(100 - percent):0.##}*"));
        BsUsageText.Text = string.Create(CultureInfo.InvariantCulture,
            $"{FormatBytes(total)} across {files.Count} Outlook Data File(s) of the 50 GB limit.");
        foreach (var file in files)
            BsUsageFiles.Children.Add(new TextBlock
            {
                Text = $"{file.Name} — {FormatBytes(file.Length)} on disk",
                FontSize = 12.5
            });
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Format(CultureInfo.InvariantCulture, "{0} {1}",
            value.ToString(unit == 0 || value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture), units[unit]);
    }

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { StatusText.Text = "Could not open the system browser."; }
    }

    private void BsWebLinkClicked(object? sender, RoutedEventArgs e) => OpenUrl(BsWebLinkUrl.Text);

    private void BsGetAppClicked(object? sender, RoutedEventArgs e) =>
        OpenUrl("https://www.microsoft.com/en-us/microsoft-365/outlook/email-and-calendar-software-microsoft-outlook");

    private void BsProjectHomeClicked(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/russellmm/OpenOutlook");

    private void BsFeedbackClicked(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/russellmm/OpenOutlook/issues/new");

    private async void BsAddAccountClicked(object? sender, RoutedEventArgs e)
    {
        await ShowAccountSetupAsync();
        PopulateBackstageAccounts();
    }

    private void BsAccountSettingsClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control anchor) ShowAccountSettingsMenu(anchor);
    }

    private void BsOpenDataFilesClicked(object? sender, RoutedEventArgs e)
    {
        NavOpenExport.IsChecked = true;
        SelectBackstagePage("openexport");
    }

    private void BsOpenPstClicked(object? sender, RoutedEventArgs e) => OpenPstClicked(sender, e);

    private void BsDetachClicked(object? sender, RoutedEventArgs e) => DetachClicked(sender, e);

    private void BsExportFolderClicked(object? sender, RoutedEventArgs e) => ExportFolderClicked(sender, e);

    private void BsSaveAsClicked(object? sender, RoutedEventArgs e) => ExportMessageClicked(sender, e);

    private void BsSaveAttachmentsClicked(object? sender, RoutedEventArgs e) => ExportAttachmentClicked(sender, e);

    private void BsPrintClicked(object? sender, RoutedEventArgs e) => SavePrintablePdfClicked(sender, e);

    private void BackstageExitClicked(object? sender, RoutedEventArgs e) =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
}
