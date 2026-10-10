using Avalonia.Interactivity;
using OpenOutlook.Auth;

namespace OpenOutlook.Desktop;

public sealed partial class MainWindow
{
    private CalendarPage? _calendarPage;

    private async void ShowCalendarClicked(object? sender, RoutedEventArgs e)
    {
        CloseBackstage();
        ToolbarBand.IsVisible = false;
        CalendarToolbarBand.IsVisible = true;
        MailContentBorder.IsVisible = false;
        CalendarContentBorder.IsVisible = true;
        MailNavButton.Classes.Set("olRailActive", false);
        CalendarNavButton.Classes.Set("olRailActive", true);
        if (_calendarPage is null)
        {
            _calendarPage = new CalendarPage(this, _graphHttp,
                account => GetMicrosoftSession(account).GetAccessTokenAsync(),
                account => ShowAccountSetupAsync(account),
                () => OpenBackstage("info"));
            CalendarContent.Content = _calendarPage;
            CalendarRibbonContent.Content = _calendarPage.Ribbon;
            RefreshCalendarAccounts();
        }
        await _calendarPage.ActivateAsync();
    }

    private void ShowMailClicked(object? sender, RoutedEventArgs e)
    {
        _calendarPage?.Deactivate();
        CalendarContentBorder.IsVisible = false;
        MailContentBorder.IsVisible = true;
        ToolbarBand.IsVisible = true;
        CalendarToolbarBand.IsVisible = false;
        MailNavButton.Classes.Set("olRailActive", true);
        CalendarNavButton.Classes.Set("olRailActive", false);
    }

    private void RefreshCalendarAccounts()
    {
        if (_calendarPage is null) return;
        try { _calendarPage.SetAccounts(_accountRegistry.Load()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { StatusText.Text = "Saved calendar accounts could not be read."; }
    }
}
