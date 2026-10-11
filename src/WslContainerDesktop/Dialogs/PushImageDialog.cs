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
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Views.Controls;

using WslContainerDesktop.Services;

namespace WslContainerDesktop.Dialogs;

/// <summary>
/// Push a local image to a registry. The chosen registry alone decides where the image goes; the
/// user types only the name inside it, and the dialog checks sign-in before allowing the push.
/// </summary>
public sealed class PushImageDialog : ContentDialog
{
    private readonly ComboBox _registryBox;
    private readonly TextBlock _signInStatus;
    private readonly HyperlinkButton _openRegistries;
    private readonly TextBox _nameBox;
    private readonly TextBlock _preview;
    private readonly TextBlock _error;
    private readonly CheckBox _allTagsBox;
    private readonly IReadOnlyList<RegistryEntry> _registries;
    private readonly string? _localReference;
    private readonly Func<RegistryEntry, Task<(RegistryLoginState State, string? User)>> _checkSignIn;

    private string _lastSuggestion = string.Empty;
    private string? _dockerHubUser;
    private int _checkVersion;
    private bool _suppressNameChange;

    /// <summary>The fully-resolved reference to push.</summary>
    public string Reference { get; private set; } = string.Empty;
    /// <summary>Gets or sets a value indicating whether the all tags flag is set.</summary>
    public bool AllTags { get; private set; }

    /// <summary>True when the user chose to go to the Registries page to sign in.</summary>
    public bool OpenRegistriesRequested { get; private set; }

    /// <summary>Creates the dialog and starts with the known registries and optional local image reference.</summary>
    public PushImageDialog(
        IReadOnlyList<RegistryEntry> registries,
        string? localReference,
        Func<RegistryEntry, Task<(RegistryLoginState State, string? User)>> checkSignIn)
    {
        _registries = registries;
        _localReference = localReference;
        _checkSignIn = checkSignIn;

        Title = UiText.Get("Workload_Text_Push_image_7d7dad", "Push image");
        PrimaryButtonText = UiText.Get("Workload_Text_Push_731ce7", "Push");
        CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        _registryBox = new ComboBox
        {
            Header = UiText.Get("Workload_Text_Push_to_a503aa", "Push to"),
            MinWidth = 440,
        };
        foreach (var r in registries)
        {
            _registryBox.Items.Add(r.HasHost ? $"{r.Name} ({r.Host})" : $"{r.Name} (docker.io)");
        }

        // An image already named for a configured registry (for example built for it) starts there.
        var initial = PushDestination.HostIn(localReference ?? string.Empty) is { } host
            ? PushDestination.FindRegistry(registries, host)
            : null;
        _registryBox.SelectedIndex = initial is null ? 0 : IndexOf(initial);

        _signInStatus = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _openRegistries = new HyperlinkButton
        {
            Content = UiText.Get("Workload_Text_Go_to_Registries_to_sign_in_06b3b9", "Go to Registries to sign in"),
            Padding = new Thickness(0),
            Visibility = Visibility.Collapsed,
        };
        _openRegistries.Click += (_, _) =>
        {
            OpenRegistriesRequested = true;
            Hide();
        };

        _nameBox = new TextBox
        {
            Header = UiText.Get("Workload_Text_Name_in_the_registry_c5e56f", "Name in the registry"),
            PlaceholderText = UiText.Get("Workload_Text_e_g_team_myapp_1_0_201728", "e.g. team/myapp:1.0"),
        };
        _nameBox.TextChanged += (_, _) => OnNameChanged();

        _preview = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        _allTagsBox = new CheckBox
        {
            Content = InfoTip.Labeled(
                new TextBlock { Text = UiText.Get("Workload_Text_All_tags_7c39c1", "All tags"), VerticalAlignment = VerticalAlignment.Center },
                InfoTip.Create(FlagHelp.PushAllTags)),
        };
        _allTagsBox.Checked += (_, _) => UpdatePreview();
        _allTagsBox.Unchecked += (_, _) => UpdatePreview();

        _error = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        Content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                _registryBox,
                new StackPanel { Spacing = 2, Children = { _signInStatus, _openRegistries } },
                _nameBox,
                _allTagsBox,
                _preview,
                _error,
            },
        };

        ApplySuggestion(force: true);
        _registryBox.SelectionChanged += (_, _) =>
        {
            ApplySuggestion(force: false);
            _ = CheckSignInAsync();
        };
        PrimaryButtonClick += OnPrimary;
        Opened += (_, _) => _ = CheckSignInAsync();
    }

    private RegistryEntry SelectedRegistry =>
        _registryBox.SelectedIndex >= 0 && _registryBox.SelectedIndex < _registries.Count
            ? _registries[_registryBox.SelectedIndex]
            : _registries[0];

    private int IndexOf(RegistryEntry registry)
    {
        for (var i = 0; i < _registries.Count; i++)
        {
            if (ReferenceEquals(_registries[i], registry))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Fills in a suggested name, unless the user has typed their own.</summary>
    private void ApplySuggestion(bool force)
    {
        var suggestion = PushDestination.SuggestName(_localReference, SelectedRegistry, _dockerHubUser);
        if (force || string.IsNullOrWhiteSpace(_nameBox.Text) ||
            string.Equals(_nameBox.Text.Trim(), _lastSuggestion, StringComparison.Ordinal))
        {
            _suppressNameChange = true;
            _nameBox.Text = suggestion;
            _suppressNameChange = false;
        }

        _lastSuggestion = suggestion;
        UpdatePreview();
    }

    private void OnNameChanged()
    {
        if (!_suppressNameChange && PushDestination.HostIn(_nameBox.Text) is { } host &&
            PushDestination.FindRegistry(_registries, host) is { } registry)
        {
            // A pasted full reference for a listed registry: select it and keep just the name.
            _suppressNameChange = true;
            _nameBox.Text = PushDestination.StripHost(_nameBox.Text);
            _nameBox.SelectionStart = _nameBox.Text.Length;
            _suppressNameChange = false;
            _registryBox.SelectedIndex = IndexOf(registry);
        }

        UpdatePreview();
    }

    private async Task CheckSignInAsync()
    {
        var version = ++_checkVersion;
        var registry = SelectedRegistry;
        IsPrimaryButtonEnabled = false;
        _openRegistries.Visibility = Visibility.Collapsed;
        _signInStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _signInStatus.Text = UiText.Get("Workload_Text_Checking_your_sign_in_to_0_5577aa", "Checking your sign-in to {0}…", registry.Name);

        (RegistryLoginState State, string? User) result;
        try
        {
            result = await _checkSignIn(registry);
        }
        catch (Exception)
        {
            // The push itself reports a real sign-in failure; an inconclusive check must not block it.
            result = (RegistryLoginState.Unknown, null);
        }

        if (version != _checkVersion)
        {
            return;
        }

        switch (result.State)
        {
            case RegistryLoginState.LoggedIn:
                _signInStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                _signInStatus.Text = string.IsNullOrWhiteSpace(result.User)
                    ? UiText.Get("Workload_Text_Signed_in_to_0_dc397c", "✓ Signed in to {0}.", registry.Name)
                    : UiText.Get("Workload_Text_Signed_in_to_0_as_1_43bf73", "✓ Signed in to {0} as {1}.", registry.Name, result.User);
                IsPrimaryButtonEnabled = true;
                if (registry.IsDefault && !string.IsNullOrWhiteSpace(result.User) && _dockerHubUser != result.User)
                {
                    _dockerHubUser = result.User;
                    ApplySuggestion(force: false);
                }

                break;

            case RegistryLoginState.LoggedOut:
            case RegistryLoginState.Anonymous:
                _signInStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
                _signInStatus.Text = UiText.Get("Workload_Text_You_re_not_signed_in_to_8756be", "You're not signed in to {0}. Pushing needs an account there: sign in on the Registries page, then push again.", registry.Name);
                _openRegistries.Visibility = Visibility.Visible;
                break;

            case RegistryLoginState.Unreachable:
                _signInStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
                _signInStatus.Text = UiText.Get("Workload_Text_Couldn_t_reach_0_to_check_a88a1d", "Couldn't reach {0} to check your sign-in. You can still try the push.", registry.Name);
                IsPrimaryButtonEnabled = true;
                break;

            default:
                _signInStatus.Text = UiText.Get("Workload_Text_Couldn_t_confirm_your_sign_in_1c8432", "Couldn't confirm your sign-in to {0}. You can still try the push.", registry.Name);
                IsPrimaryButtonEnabled = true;
                break;
        }
    }

    private void UpdatePreview()
    {
        var name = (_nameBox?.Text ?? string.Empty).Trim();
        if (name.Length == 0 || PushDestination.HostIn(name) is not null)
        {
            _preview.Text = string.Empty;
        }
        else if (SelectedRegistry.IsDefault && !name.Contains('/'))
        {
            _preview.Text = UiText.Get("Workload_Text_Docker_Hub_names_start_with_your_18221c", "Docker Hub names start with your Docker Hub username, for example yourname/") + name + ".";
        }
        else
        {
            var target = PushDestination.Display(name, SelectedRegistry);
            _preview.Text = _allTagsBox.IsChecked == true
                ? UiText.Get("Workload_Text_Uploads_every_tag_of_0_Your_1b265e", "Uploads every tag of {0}. Your image on this PC is not changed.", PushDestination.StripTag(target))
                : UiText.Get("Workload_Text_Uploads_it_as_0_Your_image_7c6d7a", "Uploads it as {0}. Your image on this PC is not changed.", target);
        }

        if (_error is not null)
        {
            _error.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var name = _nameBox.Text.Trim();
        var allTags = _allTagsBox.IsChecked == true;
        if (PushDestination.Validate(name, SelectedRegistry, allTags, _dockerHubUser) is { } problem)
        {
            _error.Text = problem;
            _error.Visibility = Visibility.Visible;
            args.Cancel = true;
            _nameBox.Focus(FocusState.Programmatic);
            return;
        }

        Reference = PushDestination.Resolve(name, SelectedRegistry);
        AllTags = allTags;
    }
}
