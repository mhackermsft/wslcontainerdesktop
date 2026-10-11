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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.ViewModels;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Views;

/// <summary>Page that lists container images and exposes run, tag, push, save, load, and remove actions.</summary>
public sealed partial class ImagesPage : Page
{
    /// <summary>Initializes the page/control and resolves its view model from the app service provider.</summary>
    public ImagesPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<ImagesViewModel>();
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;
    }

    /// <summary>Image inventory view model bound by the page.</summary>
    public ImagesViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        UiText.LanguageChanged += OnUiLanguageChanged;
        ViewModel.RefreshLocalizedText();
        await ViewModel.RefreshAsync();
    }

    private static ImageInfo? ImageOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ImageInfo;

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImageOf(sender) is { } img)
        {
            ViewModel.RunCommand.Execute(img);
        }
    }

    private void TagMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ImageOf(sender) is { } img)
        {
            ViewModel.TagCommand.Execute(img);
        }
    }

    private void PullUpdateMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ImageOf(sender) is { } img)
        {
            ViewModel.PullUpdateCommand.Execute(img);
        }
    }

    private void PushMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ImageOf(sender) is { } img)
        {
            ViewModel.PushCommand.Execute(img);
        }
    }

    private void SaveImageMenu_Click(object sender, RoutedEventArgs e) => UiSafe.Run(async () =>
    {
        if (ImageOf(sender) is { } img && await PickImageArchiveSavePathAsync([img]) is { } path)
        {
            await ViewModel.SaveImagesAsync([img], path);
        }
    });

    private void CopyDigestMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ImageOf(sender) is { } img && img.HasDigest)
        {
            var package = new DataPackage();
            package.SetText(img.DigestDisplay);
            Clipboard.SetContent(package);
        }
    }

    private void InspectMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ImageOf(sender) is { } img)
        {
            ViewModel.InspectCommand.Execute(img);
        }
    }

    private void RemoveMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ImageOf(sender) is { } img)
        {
            ViewModel.RemoveCommand.Execute(img);
        }
    }

    // Populates the "Run profile" submenu with the saved profiles for the row's image when the
    // "More" flyout opens, so one click launches a container with the remembered configuration.
    private void MoreFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout)
        {
            return;
        }

        var image = (flyout.Target as FrameworkElement)?.DataContext as ImageInfo;
        var submenu = flyout.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault();
        if (submenu is null)
        {
            return;
        }

        submenu.Items.Clear();

        var profiles = image is null
            ? System.Array.Empty<RunProfile>()
            : ViewModel.ProfilesForImage(image.Reference);

        if (profiles.Count == 0)
        {
            submenu.Items.Add(new MenuFlyoutItem { Text = UiText.Get("Resource_Text_5e8760f506fb", "No saved profiles"), IsEnabled = false });
            return;
        }

        foreach (var profile in profiles)
        {
            var item = new MenuFlyoutItem { Text = profile.Name, Tag = profile };
            item.Click += RunProfileMenu_Click;
            submenu.Items.Add(item);
        }
    }

    private void RunProfileMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is RunProfile profile)
        {
            ViewModel.RunProfileCommand.Execute(profile);
        }
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.IsSelectionMode)
        {
            ViewModel.SelectedCount = ImagesList.SelectedItems.Count;
        }
    }

    private async void BulkRemove_Click(object sender, RoutedEventArgs e)
    {
        var selected = ImagesList.SelectedItems.OfType<ImageInfo>().ToList();
        await ViewModel.BulkRemoveAsync(selected);
    }

    private void BulkSave_Click(object sender, RoutedEventArgs e) => UiSafe.Run(async () =>
    {
        var selected = ImagesList.SelectedItems.OfType<ImageInfo>().ToList();
        if (await PickImageArchiveSavePathAsync(selected) is { } path)
        {
            await ViewModel.SaveImagesAsync(selected, path);
        }
    });

    private void LoadImage_Click(object sender, RoutedEventArgs e) => UiSafe.Run(async () =>
    {
        ImportFlyout.Hide();
        if (await PickTarOpenPathAsync() is { } path)
        {
            await ViewModel.LoadImageAsync(path);
        }
    });

    private void ImportImage_Click(object sender, RoutedEventArgs e) => UiSafe.Run(async () =>
    {
        ImportFlyout.Hide();
        if (await PickTarOpenPathAsync() is { } path)
        {
            await ViewModel.ImportImageAsync(path);
        }
    });

    private void BulkCancel_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsSelectionMode = false;
    }

    private async Task<string?> PickImageArchiveSavePathAsync(IReadOnlyList<ImageInfo> images)
    {
        if (images.Count == 0)
        {
            return null;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = images.Count == 1 ? SafeFileName(images[0].Reference) : UiText.Get("Resource_Text_19f49d852660", "images"),
        };
        picker.FileTypeChoices.Add(UiText.Get("Resource_Text_517cef447ed8", "Tar archive"), [".tar"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetMainWindowHandle());
        return (await picker.PickSaveFileAsync())?.Path;
    }

    private async Task<string?> PickTarOpenPathAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        // The picker accepts only single-dot extensions (".tar.gz" throws); such files match ".gz".
        picker.FileTypeFilter.Add(".tar");
        picker.FileTypeFilter.Add(".gz");
        picker.FileTypeFilter.Add(".tgz");
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetMainWindowHandle());
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private static string SafeFileName(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) || ch is ':' or '/' or '\\' ? '-' : ch).ToArray();
        var name = new string(chars).Trim('-', ' ');
        return string.IsNullOrWhiteSpace(name) ? UiText.Get("Resource_Text_0e7629279488", "image") : name;
    }

    private static nint GetMainWindowHandle() =>
        Microsoft.UI.Win32Interop.GetWindowFromWindowId(App.Current.MainWindow!.AppWindow.Id);
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        UiText.LanguageChanged -= OnUiLanguageChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnUiLanguageChanged(object? sender, EventArgs e)
    {
        ViewModel.RefreshLocalizedText();
        Bindings.Update();
    }
}
