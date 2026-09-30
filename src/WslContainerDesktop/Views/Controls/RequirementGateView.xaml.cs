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
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop.Views.Controls;

/// <summary>Banner-style control shown when required WSL or <c>wslc</c> prerequisites are missing.</summary>
public sealed partial class RequirementGateView : UserControl
{
    /// <summary>Initializes the page/control and resolves its view model from the app service provider.</summary>
    public RequirementGateView()
    {
        ViewModel = App.Current.Services.GetRequiredService<RequirementGateViewModel>();
        InitializeComponent();
    }

    /// <summary>Requirement status view model bound by the gate control.</summary>
    public RequirementGateViewModel ViewModel { get; }
}
