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
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop.Views.Controls;

/// <summary>
/// The Kubernetes "Dashboard" overview: a grid of clickable metric cards (one per resource kind)
/// that jump to their section. Extracted from KubernetesPage so the page markup stays reviewable.
/// The hosting page passes its <see cref="KubernetesViewModel"/> via <see cref="ViewModel"/>.
/// </summary>
public sealed partial class K8sDashboardSection : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(KubernetesViewModel),
            typeof(K8sDashboardSection),
            new PropertyMetadata(null, OnViewModelChanged));

    public K8sDashboardSection()
    {
        InitializeComponent();
    }

    public KubernetesViewModel? ViewModel
    {
        get => (KubernetesViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Re-evaluate the compiled x:Bind expressions now that the view model is available.
        ((K8sDashboardSection)d).Bindings.Update();
    }

    private void DashboardCard_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && (sender as FrameworkElement)?.Tag is string section)
        {
            ViewModel.SelectedSection = section;
        }
    }
}
