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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Models;
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop.Dialogs;

public sealed partial class ComposePreviewDialog : ContentDialog
{
    public ComposePreviewViewModel ViewModel { get; }
    public InfoBarSeverity Severity => !ViewModel.CanApply ? InfoBarSeverity.Error :
        ViewModel.HasWarnings ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;

    public ComposePreviewDialog(ComposeCompatibilityPreview preview)
    {
        ViewModel = new(preview);
        InitializeComponent();
        PrimaryButtonClick += (_, args) => args.Cancel = !ViewModel.CanApply;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PrimaryButton") is FrameworkElement apply)
            AutomationProperties.SetAutomationId(apply, "ComposePreviewApply");
        if (GetTemplateChild("CloseButton") is FrameworkElement cancel)
            AutomationProperties.SetAutomationId(cancel, "ComposePreviewCancel");
    }
}
