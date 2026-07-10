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

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tray;

namespace WslContainerDesktop.ViewModels;

/// <summary>Backs the shell header/status strip: live engine health indicator.</summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly StatusMonitor _monitor;

    [ObservableProperty]
    private string _engineStatusText = "Connecting…";

    [ObservableProperty]
    private Brush _engineStatusBrush = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));

    [ObservableProperty]
    private string _kubernetesStatusText = "Kubernetes: …";

    [ObservableProperty]
    private Brush _kubernetesStatusBrush = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));

    /// <summary>The footer Kubernetes indicator is shown only when the cluster is installed.</summary>
    [ObservableProperty]
    private Visibility _kubernetesVisibility = Visibility.Collapsed;

    public ShellViewModel(StatusMonitor monitor)
    {
        _monitor = monitor;
        _monitor.StatusChanged += OnStatusChanged;
        _monitor.K8sStatusChanged += OnK8sStatusChanged;

        if (_monitor.Latest is not null)
        {
            Apply(_monitor.Latest);
        }

        if (_monitor.LatestK8s is not null)
        {
            ApplyK8s(_monitor.LatestK8s);
        }
    }

    private void OnStatusChanged(object? sender, EngineStatusSnapshot e) => Apply(e);

    private void OnK8sStatusChanged(object? sender, K8sStatusSnapshot e) => ApplyK8s(e);

    private void ApplyK8s(K8sStatusSnapshot snapshot)
    {
        KubernetesVisibility = snapshot.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        KubernetesStatusText = snapshot.Summary;

        var color = snapshot.State switch
        {
            Models.ClusterState.Running => Color.FromArgb(255, 45, 200, 95),
            Models.ClusterState.Stopped => Color.FromArgb(255, 240, 180, 40),
            _ => Color.FromArgb(255, 150, 150, 150),
        };

        KubernetesStatusBrush = new SolidColorBrush(color);
    }

    private void Apply(EngineStatusSnapshot snapshot)
    {
        EngineStatusText = snapshot.Summary;
        var color = snapshot.Health switch
        {
            EngineHealth.Healthy => Color.FromArgb(255, 45, 200, 95),
            EngineHealth.Degraded => Color.FromArgb(255, 240, 180, 40),
            EngineHealth.Down => Color.FromArgb(255, 230, 70, 70),
            _ => Color.FromArgb(255, 150, 150, 150),
        };

        EngineStatusBrush = new SolidColorBrush(color);
    }
}
