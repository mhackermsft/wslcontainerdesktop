// WSL Container Desktop - a WinUI 3 manager for WSL containers.
// Copyright (C) 2026 Michael Hacker
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Dialogs;

/// <summary>
/// Guided flow to add an Azure Container Registry: verifies the Azure CLI, signs in,
/// lists subscriptions, then registries. On confirm it fetches a short-lived ACR token
/// from the signed-in identity so no admin username/password is needed.
/// </summary>
public sealed class AddAcrDialog : ContentDialog
{
    private const string AadTokenUsername = "00000000-0000-0000-0000-000000000000";
    private const string InstallUrl = "https://aka.ms/installazurecliwindows";

    private readonly IAzureCliService _az;

    private readonly StackPanel _body;
    private readonly TextBlock _statusText;
    private readonly ProgressRing _spinner;
    private readonly Button _signInButton;
    private readonly HyperlinkButton _installLink;
    private readonly ComboBox _subscriptionBox;
    private readonly ComboBox _registryBox;
    private readonly TextBlock _authNote;

    private IReadOnlyList<AzureSubscription> _subscriptions = Array.Empty<AzureSubscription>();
    private IReadOnlyList<AzureRegistry> _registries = Array.Empty<AzureRegistry>();

    /// <summary>The registry entry to persist (host + username), populated on confirm.</summary>
    public RegistryEntry? Registry { get; private set; }

    /// <summary>The AAD token to log in with (used as the password), populated on confirm.</summary>
    public string? Token { get; private set; }

    /// <summary>The username to log in with (the well-known AAD token user).</summary>
    public string TokenUsername => AadTokenUsername;

    public AddAcrDialog(IAzureCliService az)
    {
        _az = az;

        Title = "Add Azure Container Registry";
        PrimaryButtonText = "Add";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;

        _spinner = new ProgressRing { Width = 20, Height = 20, IsActive = false };
        _statusText = new TextBlock { TextWrapping = TextWrapping.Wrap };

        _installLink = new HyperlinkButton
        {
            Content = "Install the Azure CLI",
            NavigateUri = new Uri(InstallUrl),
            Visibility = Visibility.Collapsed,
        };

        _signInButton = new Button
        {
            Content = "Sign in to Azure",
            Visibility = Visibility.Collapsed,
        };
        _signInButton.Click += OnSignInClicked;

        _subscriptionBox = new ComboBox
        {
            Header = "Subscription",
            MinWidth = 420,
            Visibility = Visibility.Collapsed,
        };
        _subscriptionBox.SelectionChanged += OnSubscriptionChanged;

        _registryBox = new ComboBox
        {
            Header = "Container registry",
            MinWidth = 420,
            Visibility = Visibility.Collapsed,
        };
        _registryBox.SelectionChanged += OnRegistryChanged;

        _authNote = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var statusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { _spinner, _statusText },
        };

        _body = new StackPanel
        {
            Spacing = 12,
            MinWidth = 440,
            Children = { statusRow, _installLink, _signInButton, _subscriptionBox, _registryBox, _authNote },
        };

        Content = _body;

        Opened += OnOpened;
        PrimaryButtonClick += OnPrimary;
    }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        SetBusy("Checking for the Azure CLI…");

        if (!await _az.IsAvailableAsync())
        {
            SetIdle("The Azure CLI (az) is not installed or not on your PATH. Install it, then reopen this dialog. " +
                    "You can still add a registry manually with the \"Add registry\" button.");
            _installLink.Visibility = Visibility.Visible;
            return;
        }

        var user = await _az.GetSignedInUserAsync();
        if (string.IsNullOrWhiteSpace(user))
        {
            SetIdle("You're not signed in to Azure.");
            _signInButton.Visibility = Visibility.Visible;
            return;
        }

        await LoadSubscriptionsAsync(user!);
    }

    private async void OnSignInClicked(object sender, RoutedEventArgs e)
    {
        _signInButton.Visibility = Visibility.Collapsed;
        SetBusy("A browser will open for Azure sign-in…");

        var result = await _az.LoginAsync();
        if (!result.Success)
        {
            SetIdle("Azure sign-in did not complete. Try again.");
            _signInButton.Visibility = Visibility.Visible;
            return;
        }

        var user = await _az.GetSignedInUserAsync();
        if (string.IsNullOrWhiteSpace(user))
        {
            SetIdle("Signed in, but could not read the account. Try again.");
            _signInButton.Visibility = Visibility.Visible;
            return;
        }

        await LoadSubscriptionsAsync(user!);
    }

    private async Task LoadSubscriptionsAsync(string user)
    {
        SetBusy($"Signed in as {user}. Loading subscriptions…");

        _subscriptions = await _az.ListSubscriptionsAsync();
        if (_subscriptions.Count == 0)
        {
            SetIdle($"Signed in as {user}, but no subscriptions were found.");
            return;
        }

        _subscriptionBox.Items.Clear();
        foreach (var s in _subscriptions)
        {
            _subscriptionBox.Items.Add(s.Name);
        }

        SetIdle($"Signed in as {user}. Choose a subscription and registry.");
        _subscriptionBox.Visibility = Visibility.Visible;
        _subscriptionBox.SelectedIndex = 0; // triggers registry load
    }

    private async void OnSubscriptionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = _subscriptionBox.SelectedIndex;
        if (idx < 0 || idx >= _subscriptions.Count)
        {
            return;
        }

        _registryBox.Visibility = Visibility.Collapsed;
        _registryBox.Items.Clear();
        _authNote.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = false;

        SetBusy("Loading container registries…");
        _registries = await _az.ListRegistriesAsync(_subscriptions[idx].Id);

        if (_registries.Count == 0)
        {
            SetIdle("No container registries were found in this subscription.");
            return;
        }

        foreach (var r in _registries)
        {
            _registryBox.Items.Add($"{r.Name} ({r.LoginServer})");
        }

        SetIdle("Choose a container registry to add.");
        _registryBox.Visibility = Visibility.Visible;
        _registryBox.SelectedIndex = 0;
    }

    private void OnRegistryChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = _registryBox.SelectedIndex;
        if (idx < 0 || idx >= _registries.Count)
        {
            IsPrimaryButtonEnabled = false;
            return;
        }

        _authNote.Text = "You'll be added using your Azure sign-in — a short-lived token is fetched " +
                         "automatically, so no registry username or password is needed.";
        _authNote.Visibility = Visibility.Visible;
        IsPrimaryButtonEnabled = true;
    }

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var subIdx = _subscriptionBox.SelectedIndex;
        var regIdx = _registryBox.SelectedIndex;
        if (subIdx < 0 || regIdx < 0 || regIdx >= _registries.Count || subIdx >= _subscriptions.Count)
        {
            args.Cancel = true;
            return;
        }

        // Keep the dialog open while we fetch the token.
        var deferral = args.GetDeferral();
        try
        {
            SetBusy("Getting an access token from Azure…");
            IsPrimaryButtonEnabled = false;

            var registry = _registries[regIdx];
            var token = await _az.GetAcrTokenAsync(registry.Name, _subscriptions[subIdx].Id);
            if (token is null)
            {
                SetIdle("Could not get an access token for this registry. You may lack pull access, or the token expired.");
                IsPrimaryButtonEnabled = true;
                args.Cancel = true;
                return;
            }

            Registry = new RegistryEntry
            {
                Name = registry.Name,
                Host = string.IsNullOrWhiteSpace(registry.LoginServer) ? token.Value.LoginServer : registry.LoginServer,
                Username = AadTokenUsername,
                IsAzure = true,
                SubscriptionId = _subscriptions[subIdx].Id,
                AzureAcrName = registry.Name,
            };
            Token = token.Value.Token;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void SetBusy(string message)
    {
        _spinner.IsActive = true;
        _statusText.Text = message;
    }

    private void SetIdle(string message)
    {
        _spinner.IsActive = false;
        _statusText.Text = message;
    }
}
