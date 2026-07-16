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
using Microsoft.UI.Xaml.Data;
using WslContainerDesktop.Models;
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

    private void ConfigureButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StackTemplate template)
        {
            ViewModel.ConfigureCommand.Execute(template);
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StackTemplate template)
        {
            ViewModel.RemoveCommand.Execute(template);
        }
    }
}
