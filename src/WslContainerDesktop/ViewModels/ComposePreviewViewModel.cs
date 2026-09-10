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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.ViewModels;

/// <summary>Immutable review VM: no commands can edit or override blocked planner evidence.</summary>
public sealed class ComposePreviewViewModel(ComposeCompatibilityPreview preview)
{
    public string Title => $"Review Compose {preview.Operation}: {preview.Project}";
    public string Summary => preview.Summary;
    public bool CanApply => preview.CanApply;
    public bool HasWarnings => preview.HasWarnings;
    public IReadOnlyList<ComposeCompatibilitySetting> Settings => preview.Settings;
    public string ApplyLabel => preview.Operation == ComposeLifecycleOperation.Up ? "Apply reviewed plan" : "Confirm operation";
}
