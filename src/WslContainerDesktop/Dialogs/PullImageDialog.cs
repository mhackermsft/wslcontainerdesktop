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
using Microsoft.UI.Xaml.Media;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Dialogs;

/// <summary>
/// Pull an image, choosing which registered registry to pull from. The selected registry's
/// host qualifies a bare reference; a fully-qualified reference is passed through unchanged.
/// For registries that support it (ACR, generic v2 registries) a "Browse" panel lists the
/// available repositories and tags so the user can pick instead of typing.
/// </summary>
public sealed class PullImageDialog : ContentDialog
{
    private readonly ComboBox _registryBox;
    private readonly TextBox _referenceBox;
    private readonly TextBlock _preview;
    private readonly Button _browseButton;
    private readonly Border _browsePanel;
    private readonly TextBox _repoFilterBox;
    private readonly ProgressRing _browseRing;
    private readonly TextBlock _browseStatus;
    private readonly ListView _repoList;
    private readonly ListView _tagList;
    private readonly IReadOnlyList<RegistryEntry> _registries;
    private readonly IRegistryCatalogService _catalog;

    private readonly List<string> _allRepositories = new();
    private CancellationTokenSource? _loadCts;
    private bool _suppressSelection;

    /// <summary>The fully-resolved image reference to pull.</summary>
    public string Reference { get; private set; } = string.Empty;

    public PullImageDialog(IReadOnlyList<RegistryEntry> registries, IRegistryCatalogService catalog)
    {
        _registries = registries;
        _catalog = catalog;

        Title = "Pull image";
        PrimaryButtonText = "Pull";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        _registryBox = new ComboBox
        {
            Header = "Registry",
            MinWidth = 380,
        };
        foreach (var r in registries)
        {
            _registryBox.Items.Add(r.HasHost ? $"{r.Name} ({r.Host})" : r.Name);
        }

        _registryBox.SelectedIndex = 0;
        _registryBox.SelectionChanged += (_, _) => OnRegistryChanged();

        _referenceBox = new TextBox
        {
            Header = "Image reference",
            PlaceholderText = "e.g. ubuntu:latest or myapp:1.0",
        };
        _referenceBox.TextChanged += (_, _) => UpdatePreview();

        _browseButton = new Button
        {
            Content = "Browse…",
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _browseButton.Click += (_, _) => UiSafe.Run(ToggleBrowseAsync);

        var referenceRow = new Grid { ColumnSpacing = 8 };
        referenceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        referenceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_referenceBox, 0);
        Grid.SetColumn(_browseButton, 1);
        referenceRow.Children.Add(_referenceBox);
        referenceRow.Children.Add(_browseButton);

        _preview = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        // ---- Browse panel (collapsed until "Browse…" is clicked) --------------------------
        _repoFilterBox = new TextBox
        {
            PlaceholderText = "Filter repositories…",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _repoFilterBox.TextChanged += (_, _) => ApplyRepositoryFilter();

        _browseRing = new ProgressRing
        {
            Width = 18,
            Height = 18,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var filterRow = new Grid { ColumnSpacing = 10, VerticalAlignment = VerticalAlignment.Center };
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_repoFilterBox, 0);
        Grid.SetColumn(_browseRing, 1);
        filterRow.Children.Add(_repoFilterBox);
        filterRow.Children.Add(_browseRing);

        _browseStatus = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            MinHeight = 18,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _repoList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Height = 190,
        };
        _repoList.SelectionChanged += (_, _) => OnRepositorySelected();

        _tagList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Height = 190,
        };
        _tagList.SelectionChanged += (_, _) => OnTagSelected();

        var listsGrid = new Grid { ColumnSpacing = 12 };
        listsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        listsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var repoPane = BuildPane("Repositories", _repoList);
        var tagPane = BuildPane("Tags", _tagList);
        Grid.SetColumn(repoPane, 0);
        Grid.SetColumn(tagPane, 1);
        listsGrid.Children.Add(repoPane);
        listsGrid.Children.Add(tagPane);

        _browsePanel = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Spacing = 10,
                MinWidth = 480,
                Children = { filterRow, _browseStatus, listsGrid },
            },
        };

        Content = new StackPanel
        {
            Spacing = 12,
            Children = { _registryBox, referenceRow, _preview, _browsePanel },
        };

        UpdateBrowseAvailability();
        UpdatePreview();
        Loaded += (_, _) => _referenceBox.Focus(FocusState.Programmatic);
        PrimaryButtonClick += OnPrimary;
        Closed += (_, _) => CancelLoad();
    }

    /// <summary>Builds a titled, bordered pane wrapping a list so the two columns read as distinct.</summary>
    private static FrameworkElement BuildPane(string title, ListView list)
    {
        var header = new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(2, 0, 0, 4),
        };

        var frame = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = list,
        };

        return new StackPanel { Spacing = 0, Children = { header, frame } };
    }

    private RegistryEntry SelectedRegistry =>
        _registryBox.SelectedIndex >= 0 && _registryBox.SelectedIndex < _registries.Count
            ? _registries[_registryBox.SelectedIndex]
            : _registries[0];

    private void OnRegistryChanged()
    {
        // Switching registries invalidates any listed repositories/tags; collapse and reset.
        CancelLoad();
        _browsePanel.Visibility = Visibility.Collapsed;
        _browseButton.Content = "Browse…";
        _allRepositories.Clear();
        _repoList.Items.Clear();
        _tagList.Items.Clear();
        _repoFilterBox.Text = string.Empty;
        _browseStatus.Text = string.Empty;
        UpdateBrowseAvailability();
        UpdatePreview();
    }

    private void UpdateBrowseAvailability()
    {
        var canBrowse = _catalog.CanBrowse(SelectedRegistry);
        _browseButton.IsEnabled = canBrowse;
        ToolTipService.SetToolTip(_browseButton, canBrowse
            ? "List the images available in this registry"
            : "Browsing isn't available for this registry — enter a reference manually");
    }

    private async Task ToggleBrowseAsync()
    {
        if (_browsePanel.Visibility == Visibility.Visible)
        {
            _browsePanel.Visibility = Visibility.Collapsed;
            _browseButton.Content = "Browse…";
            return;
        }

        _browsePanel.Visibility = Visibility.Visible;
        _browseButton.Content = "Hide";
        await LoadRepositoriesAsync();
    }

    private async Task LoadRepositoriesAsync()
    {
        var registry = SelectedRegistry;
        var ct = BeginLoad();

        _allRepositories.Clear();
        _repoList.Items.Clear();
        _tagList.Items.Clear();
        SetBusy(true, "Loading repositories…");

        var result = await _catalog.ListRepositoriesAsync(registry, ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        SetBusy(false, null);

        if (!result.IsOk)
        {
            _browseStatus.Text = result.Message ?? "Could not list repositories.";
            return;
        }

        if (result.Items.Count == 0)
        {
            _browseStatus.Text = "No repositories were found in this registry.";
            return;
        }

        _allRepositories.AddRange(result.Items.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        _browseStatus.Text = $"{_allRepositories.Count} repositor{(_allRepositories.Count == 1 ? "y" : "ies")} — select one to see its tags.";
        ApplyRepositoryFilter();
    }

    private void ApplyRepositoryFilter()
    {
        var filter = _repoFilterBox.Text?.Trim() ?? string.Empty;
        _suppressSelection = true;
        _repoList.Items.Clear();
        foreach (var repo in _allRepositories)
        {
            if (filter.Length == 0 || repo.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                _repoList.Items.Add(repo);
            }
        }

        _suppressSelection = false;
    }

    private void OnRepositorySelected()
    {
        if (_suppressSelection || _repoList.SelectedItem is not string repository)
        {
            return;
        }

        UiSafe.Run(() => LoadTagsAsync(repository));
    }

    private async Task LoadTagsAsync(string repository)
    {
        var registry = SelectedRegistry;
        var ct = BeginLoad();

        _tagList.Items.Clear();
        SetBusy(true, $"Loading tags for {repository}…");

        var result = await _catalog.ListTagsAsync(registry, repository, ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        SetBusy(false, null);

        if (!result.IsOk)
        {
            _browseStatus.Text = result.Message ?? "Could not list tags.";
            return;
        }

        if (result.Items.Count == 0)
        {
            _browseStatus.Text = $"No tags were found for {repository}.";
            return;
        }

        foreach (var tag in result.Items)
        {
            _tagList.Items.Add(tag);
        }

        _browseStatus.Text = $"{repository}: select a tag to fill the image reference.";
    }

    private void OnTagSelected()
    {
        if (_repoList.SelectedItem is not string repository || _tagList.SelectedItem is not string tag)
        {
            return;
        }

        _referenceBox.Text = $"{repository}:{tag}";
        _browseStatus.Text = $"Selected {repository}:{tag}.";
        UpdatePreview();
    }

    private CancellationToken BeginLoad()
    {
        CancelLoad();
        _loadCts = new CancellationTokenSource();
        return _loadCts.Token;
    }

    private void CancelLoad()
    {
        if (_loadCts is not null)
        {
            _loadCts.Cancel();
            _loadCts.Dispose();
            _loadCts = null;
        }
    }

    private void SetBusy(bool busy, string? message)
    {
        _browseRing.IsActive = busy;
        _browseRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (message is not null)
        {
            _browseStatus.Text = message;
        }
    }

    private void UpdatePreview()
    {
        var input = (_referenceBox?.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(input))
        {
            _preview.Text = "Will pull: —";
            return;
        }

        _preview.Text = $"Will pull: {SelectedRegistry.Qualify(input)}";
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var input = _referenceBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            args.Cancel = true;
            _referenceBox.Focus(FocusState.Programmatic);
            return;
        }

        Reference = SelectedRegistry.Qualify(input);
    }
}
