using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using OpenOutlook.Auth;

namespace OpenOutlook.Desktop;

/// <summary>Guided, local-only account onboarding. Mail sync is intentionally a separate milestone.</summary>
public sealed class AccountSetupWindow : Window
{
    private readonly ConnectedAccountRegistry _registry = new();
    private readonly LibsecretSecretStore _secrets = new();
    private readonly ComboBox _provider = new();
    private readonly ListBox _accounts = new();
    private readonly TextBlock _status = new();
    private readonly Button _connect = new();
    private readonly Button _disconnect = new();
    private readonly Button _newAccount = new();
    private readonly Button _close = new();
    private CancellationTokenSource? _operation;
    private bool _isBusy;
    private string? _declinedReason;
    private OAuthClientConfiguration? _clientConfiguration;
    private bool _configurationInvalid;

    public AccountSetupWindow()
    {
        Title = "Accounts — OpenOutlook";
        Width = 640;
        Height = 625;
        MinWidth = 480;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _provider.ItemsSource = new[]
        {
            new ProviderChoice(OAuthProvider.MicrosoftConsumers, "Outlook.com / Hotmail"),
            new ProviderChoice(OAuthProvider.Google, "Gmail")
        };
        _provider.SelectedIndex = 0;
        _provider.MinWidth = 220;
        _provider.SelectionChanged += (_, _) => UpdateAvailability();
        _accounts.Height = 130;
        _accounts.SelectionChanged += AccountSelected;
        _connect.Content = "Sign in";
        _connect.Click += ConnectClicked;
        _disconnect.Content = "Disconnect selected";
        _disconnect.Click += DisconnectClicked;
        _newAccount.Content = "New account";
        _newAccount.Click += (_, _) => { _accounts.SelectedItem = null; UpdateAvailability(); };
        _close.Content = "Close";
        _close.Click += (_, _) => { if (_operation is not null) _operation.Cancel(); else if (!_isBusy) Close(); };
        Closing += (_, args) =>
        {
            if (!_isBusy) return;
            args.Cancel = true;
            _operation?.Cancel();
        };

        var content = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = "Connect a personal mail account", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "Choose your email provider. OpenOutlook will open its sign-in page in your browser. Your password is entered only on the provider's page, and OpenOutlook keeps account access in the Linux keyring.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock { Text = "Provider" });
        content.Children.Add(_provider);
        content.Children.Add(new TextBlock
        {
            Text = "This preview requests read-only mail access. Microsoft Inbox can show the newest 50 messages; full sync and sending are not available yet.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_connect);
        actions.Children.Add(_newAccount);
        content.Children.Add(actions);
        content.Children.Add(new TextBlock { Text = "Saved accounts", FontWeight = Avalonia.Media.FontWeight.SemiBold });
        content.Children.Add(_accounts);
        content.Children.Add(_disconnect);
        _status.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        content.Children.Add(_status);
        content.Children.Add(_close);
        Content = new ScrollViewer { Content = content };
        RefreshAccounts();
        try { _clientConfiguration = OAuthClientConfiguration.Load(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        { _configurationInvalid = true; }
        UpdateAvailability();
    }

    private OAuthProvider SelectedProvider => (_provider.SelectedItem as ProviderChoice)?.Provider
        ?? OAuthProvider.MicrosoftConsumers;
    private ConnectedAccount? SelectedAccount => (_accounts.SelectedItem as ListBoxItem)?.Tag as ConnectedAccount;

    private void RefreshAccounts()
    {
        _accounts.Items.Clear();
        try
        {
            foreach (var account in _registry.Load())
                _accounts.Items.Add(new ListBoxItem
                {
                    Content = $"{account.DisplayAddress} · {(account.Provider == OAuthProvider.Google ? "Gmail" : "Microsoft")}",
                    Tag = account
                });
            _disconnect.IsEnabled = false;
            _status.Text = "Saved identities are listed here. Their tokens remain in the system keyring.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        { _status.Text = "The local account list could not be read. Resolve its file permissions before connecting."; }
    }

    private void AccountSelected(object? sender, SelectionChangedEventArgs e)
    {
        var account = SelectedAccount;
        _disconnect.IsEnabled = account is not null && !_isBusy;
        _connect.Content = account is null ? "Sign in" : "Sign in again";
        if (account is not null)
            _provider.SelectedIndex = account.Provider == OAuthProvider.Google ? 1 : 0;
        UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        if (_isBusy) return;
        _provider.IsEnabled = SelectedAccount is null;
        var provider = SelectedAccount?.Provider ?? SelectedProvider;
        _connect.IsEnabled = _clientConfiguration?.GetClientId(provider) is not null;
        if (_configurationInvalid)
            _status.Text = "This OpenOutlook build has an invalid sign-in configuration. Contact the build maintainer.";
        else if (!_connect.IsEnabled)
            _status.Text = $"{(provider == OAuthProvider.Google ? "Google" : "Microsoft")} sign-in is not configured in this build yet. You do not need to register an application or enter a client ID.";
        else
            _status.Text = "Ready to sign in. Your browser will open when you select Sign in.";
    }

    private async void ConnectClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _declinedReason = null;
        var selected = SelectedAccount;
        var provider = selected?.Provider ?? SelectedProvider;
        var clientId = _clientConfiguration?.GetClientId(provider);
        if (clientId is null) { UpdateAvailability(); return; }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _operation = cancellation;
        SetBusy(true);
        try
        {
            _status.Text = "Checking the persistent Linux keyring…";
            await _secrets.CheckAvailabilityAsync(cancellation.Token);
            var prior = _registry.Load();
            _registry.Save(prior); // Check writable private metadata storage before browser authorization.
            _status.Text = "Opening your system browser. Return here after approving the requested access…";
            var scopes = provider == OAuthProvider.Google
                ? new[] { "https://www.googleapis.com/auth/gmail.readonly" }
                : ["offline_access", "User.Read", "Mail.Read"];
            var identity = await DesktopAccountConnector.ConnectAsync(provider, clientId, scopes,
                SystemAuthorizationBrowser.LaunchAsync,
                (verified, token) => ConfirmIdentityAsync(verified, selected, prior, token),
                _secrets, cancellationToken: cancellation.Token);
            try
            {
                _registry.Upsert(new ConnectedAccount(provider, identity.AccountId, identity.DisplayAddress,
                    clientId, DateTimeOffset.UtcNow));
            }
            catch
            {
                if (!prior.Any(account => account.Provider == provider && account.AccountId == identity.AccountId))
                {
                    try { await _secrets.DeleteRefreshTokenAsync(provider, identity.AccountId, CancellationToken.None); }
                    catch (SecretStoreException) { /* A token may remain; do not claim the account was saved. */ }
                }
                throw;
            }
            RefreshAccounts();
            _status.Text = provider == OAuthProvider.MicrosoftConsumers
                ? $"{identity.DisplayAddress} is connected. Close this window and select Inbox to load the newest messages read-only."
                : $"{identity.DisplayAddress} is connected. Gmail mailbox display is still being built.";
        }
        catch (OperationCanceledException) { _status.Text = "Account connection canceled."; }
        catch (SecretStoreException exception) { _status.Text = exception.Message; }
        catch (TimeoutException) { _status.Text = "Sign-in timed out. Close the browser tab and try again."; }
        catch (InvalidOperationException) when (_declinedReason is not null) { _status.Text = _declinedReason; }
        catch (Exception) { _status.Text = "Account connection failed. Check the browser and keyring, then retry."; }
        finally { _operation = null; SetBusy(false); }
    }

    private async Task<bool> ConfirmIdentityAsync(VerifiedAccountIdentity identity, ConnectedAccount? selected,
        IReadOnlyList<ConnectedAccount> prior, CancellationToken cancellationToken)
    {
        return await Dispatcher.UIThread.InvokeAsync(
            () => ShowIdentityConfirmationAsync(identity, selected, prior, cancellationToken));
    }

    private async Task<bool> ShowIdentityConfirmationAsync(VerifiedAccountIdentity identity, ConnectedAccount? selected,
        IReadOnlyList<ConnectedAccount> prior, CancellationToken cancellationToken)
    {
        if (selected is not null &&
            (selected.Provider != identity.Provider || selected.AccountId != identity.AccountId))
        { _declinedReason = "The browser returned a different account. Use New account to add it separately."; return false; }
        if (selected is null && !prior.Any(account => account.Provider == identity.Provider && account.AccountId == identity.AccountId) &&
            (prior.Count >= 4 || prior.Count(account => account.Provider == identity.Provider) >= 2))
        { _declinedReason = "This build supports at most two accounts per provider and four accounts total."; return false; }

        var dialog = new Window
        {
            Title = "Confirm account identity", Width = 460, Height = 230,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var yes = new Button { Content = "Connect this account" };
        var no = new Button { Content = "Cancel" };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 14,
            Children =
            {
                new TextBlock { Text = $"The provider verified: {identity.DisplayAddress}",
                    FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "Connect this identity to OpenOutlook? Its refresh token will be saved only in the Linux keyring. Full mailbox sync is not yet enabled.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                buttons
            }
        };
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        var confirmed = await dialog.ShowDialog<bool>(this);
        if (!confirmed) _declinedReason = "Account identity was not confirmed. No token was saved.";
        return confirmed;
    }

    private async void DisconnectClicked(object? sender, RoutedEventArgs e)
    {
        var account = SelectedAccount;
        if (account is null || _isBusy) return;
        var dialog = new Window
        {
            Title = "Disconnect account", Width = 460, Height = 225,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var yes = new Button { Content = "Disconnect locally" };
        var no = new Button { Content = "Cancel" };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 14,
            Children =
            {
                new TextBlock { Text = $"Disconnect {account.DisplayAddress}?", FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "This removes OpenOutlook's local keyring token and saved account entry. Provider-side authorization revocation is not yet available.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } }
            }
        };
        if (await dialog.ShowDialog<bool>(this) != true) return;
        SetBusy(true);
        try
        {
            await _secrets.DeleteRefreshTokenAsync(account.Provider, account.AccountId);
            _registry.Remove(account.Provider, account.AccountId);
            RefreshAccounts();
            _status.Text = $"{account.DisplayAddress} was disconnected locally.";
        }
        catch (Exception) { _status.Text = "Could not remove the account. Unlock the keyring and check local account storage, then retry."; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        _provider.IsEnabled = !busy && SelectedAccount is null;
        _accounts.IsEnabled = _newAccount.IsEnabled = !busy;
        _connect.IsEnabled = !busy && _clientConfiguration?.GetClientId(SelectedAccount?.Provider ?? SelectedProvider) is not null;
        _disconnect.IsEnabled = !busy && SelectedAccount is not null;
        _close.IsEnabled = !busy || _operation is not null;
        _close.Content = busy ? _operation is not null ? "Cancel sign-in" : "Working…" : "Close";
    }

    private sealed record ProviderChoice(OAuthProvider Provider, string Label)
    {
        public override string ToString() => Label;
    }
}
