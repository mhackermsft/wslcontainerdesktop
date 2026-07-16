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

using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.Storage;
using Windows.Storage.Pickers;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop.Views;

public sealed partial class TemplatesPage : Page
{
    private readonly CollectionViewSource _grouped = new() { IsSourceGrouped = true };

    public TemplatesPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<TemplatesViewModel>();
        InitializeComponent();

        DataContext = ViewModel;
        _grouped.Source = ViewModel.Groups;
        TemplatesView.ItemsSource = _grouped.View;
    }

    public TemplatesViewModel ViewModel { get; }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StackTemplate template)
        {
            ViewModel.LaunchCommand.Execute(template);
        }
    }

    private void ConfigureMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is StackTemplate template)
        {
            ViewModel.ConfigureCommand.Execute(template);
        }
    }

    private void RemoveDeploymentMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is StackTemplate template)
        {
            ViewModel.RemoveCommand.Execute(template);
        }
    }

    private void HideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is StackTemplate template)
        {
            ViewModel.ToggleHiddenCommand.Execute(template);
        }
    }

    private void DeleteTemplateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is StackTemplate template)
        {
            ViewModel.DeleteTemplateCommand.Execute(template);
        }
    }

    private void NewTemplateButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.CreateTemplateCommand.Execute(null);

    private void EditMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is StackTemplate template)
        {
            ViewModel.EditTemplateCommand.Execute(template);
        }
    }

    private void DuplicateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is StackTemplate template)
        {
            ViewModel.DuplicateTemplateCommand.Execute(template);
        }
    }

    private void ExportMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StackTemplate template)
        {
            return;
        }

        UiSafe.Run(async () =>
        {
            var file = await PickSaveFileAsync(SanitizeFileName(template.Name));
            if (file is not null)
            {
                await FileIO.WriteTextAsync(file, ViewModel.ExportToJson(template));
                ViewModel.StatusMessage = $"Exported \"{template.Name}\" to {file.Name}.";
            }
        });
    }

    private void ExportAllButton_Click(object sender, RoutedEventArgs e)
    {
        UiSafe.Run(async () =>
        {
            if (!await ViewModel.EnsureHasUserTemplatesForExportAsync())
            {
                return;
            }

            var file = await PickSaveFileAsync("my-templates");
            if (file is not null)
            {
                await FileIO.WriteTextAsync(file, ViewModel.ExportAllUserToJson());
                ViewModel.StatusMessage = $"Exported your custom templates to {file.Name}.";
            }
        });
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        UiSafe.Run(async () =>
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(TemplatePortability.FileExtension);
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, GetMainWindowHandle());

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var json = await FileIO.ReadTextAsync(file);
            await ViewModel.ImportFromJsonAsync(json);
        });
    }

    private async System.Threading.Tasks.Task<StorageFile?> PickSaveFileAsync(string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
        };
        picker.FileTypeChoices.Add(
            "WSL template", new List<string> { TemplatePortability.FileExtension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetMainWindowHandle());
        return await picker.PickSaveFileAsync();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "template" : cleaned;
    }

    private static nint GetMainWindowHandle() =>
        Microsoft.UI.Win32Interop.GetWindowFromWindowId(App.Current.MainWindow!.AppWindow.Id);
}
