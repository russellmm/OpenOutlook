using Avalonia.Interactivity;
using OpenOutlook.Auth;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>The People section on the navigation rail: Hotmail / Outlook.com contacts through Microsoft Graph (PeoplePage).</summary>
public sealed partial class MainWindow
{
    private PeoplePage? _peoplePage;

    private async void ShowPeopleClicked(object? sender, RoutedEventArgs e)
    {
        CloseBackstage();
        _calendarPage?.Deactivate();
        ToolbarBand.IsVisible = false;
        CalendarToolbarBand.IsVisible = false;
        MailContentBorder.IsVisible = false;
        CalendarContentBorder.IsVisible = false;
        PeopleToolbarBand.IsVisible = true;
        PeopleContentBorder.IsVisible = true;
        MailNavButton.Classes.Set("olRailActive", false);
        CalendarNavButton.Classes.Set("olRailActive", false);
        PeopleNavButton.Classes.Set("olRailActive", true);
        if (_peoplePage is null)
        {
            _peoplePage = new PeoplePage(this, _graphHttp,
                account => GetMicrosoftSession(account).GetAccessTokenAsync(),
                account => ShowAccountSetupAsync(account),
                () => OpenBackstage("info"),
                EmailContact, ScheduleMeetingWithAsync);
            _peoplePage.Status += text => { if (text.Length > 0) StatusText.Text = text; };
            _peoplePage.ContactsChanged += ForgetRecipientContacts;
            PeopleContent.Content = _peoplePage;
            PeopleRibbonContent.Content = _peoplePage.Ribbon;
            RefreshPeopleAccounts();
        }
        await _peoplePage.ActivateAsync();
    }

    /// <summary>Leaves the People section (the Mail and Calendar buttons call this before showing their own page).</summary>
    private void HidePeople()
    {
        _peoplePage?.Deactivate();
        PeopleToolbarBand.IsVisible = false;
        PeopleContentBorder.IsVisible = false;
        PeopleNavButton.Classes.Set("olRailActive", false);
    }

    private void RefreshPeopleAccounts()
    {
        if (_peoplePage is null) return;
        try { _peoplePage.SetAccounts(_accountRegistry.Load()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { StatusText.Text = "Saved accounts could not be read."; }
    }

    /// <summary>Email from a contact: a new message from that Microsoft account, addressed to the contact.</summary>
    private void EmailContact(ConnectedAccount account, string name, string address) =>
        OpenCompose(account, new ComposeSeed(To: address));

    /// <summary>Meeting from a contact: the appointment window in meeting mode with the contact invited, saved in the account's default calendar.</summary>
    private async Task ScheduleMeetingWithAsync(ConnectedAccount account, string address)
    {
        if (!account.CanWriteMicrosoftCalendar)
        {
            StatusText.Text = $"Calendar access is not saved for {account.DisplayAddress}. Repair this account's sign-in to schedule meetings.";
            return;
        }
        var draft = await new CalendarEditorWindow(DateOnly.FromDateTime(DateTime.Today), meeting: true, attendees: address).ShowDialog<NewCalendarEvent?>(this);
        if (draft is null) return;
        StatusText.Text = "Saving meeting and sending invitations…";
        try
        {
            var token = await GetMicrosoftSession(account).GetAccessTokenAsync();
            var client = new GraphCalendarClient(_graphHttp, account.AccountId);
            var calendars = await client.ListCalendarsAsync(token);
            var calendar = calendars.FirstOrDefault(c => c.IsDefault && c.CanEdit) ?? calendars.FirstOrDefault(c => c.CanEdit);
            if (calendar is null) { StatusText.Text = "No editable calendar was found for this account."; return; }
            await client.CreateEventAsync(token, calendar.Id, draft);
            StatusText.Text = $"Meeting saved in {calendar.Name}; invitations were sent by Outlook.";
        }
        catch (Exception ex) when (ex is GraphMailException or HttpRequestException or ArgumentException or InvalidOperationException)
        {
            StatusText.Text = "The meeting could not be saved: " + ex.Message;
        }
    }
}
