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

namespace WslContainerDesktop.Models;

/// <summary>Whether a template launches a single container or imports a multi-service compose stack.</summary>
public enum StackTemplateKind
{
    SingleContainer,
    Compose,
}

/// <summary>
/// A curated, one-click template from the gallery. Single-container templates prefill the Run
/// dialog with <see cref="RunOptions"/>; compose templates import <see cref="ComposeYaml"/> as a
/// new Compose project. Templates are static, bundled data (see <c>TemplateCatalog</c>).
/// </summary>
public sealed partial class StackTemplate : ObservableObject
{
    public required string Id { get; init; }

    /// <summary>Display name, e.g. "PostgreSQL".</summary>
    public required string Name { get; init; }

    /// <summary>Grouping shown as a section header, e.g. "Databases".</summary>
    public required string Category { get; init; }

    /// <summary>Short one-line description shown on the card.</summary>
    public required string Description { get; init; }

    /// <summary>Segoe MDL2 glyph for the card icon.</summary>
    public string Glyph { get; init; } = "\uE7B8"; // generic package

    public StackTemplateKind Kind { get; init; } = StackTemplateKind.SingleContainer;

    /// <summary>Prefilled run options for <see cref="StackTemplateKind.SingleContainer"/> templates.</summary>
    public RunContainerOptions? RunOptions { get; init; }

    /// <summary>Raw docker-compose YAML for <see cref="StackTemplateKind.Compose"/> templates.</summary>
    public string? ComposeYaml { get; init; }

    /// <summary>Default project name suggested when importing a compose template.</summary>
    public string? ComposeProjectName { get; init; }

    /// <summary>Optional note surfaced to the user (e.g. default credentials) before launching.</summary>
    public string? Note { get; init; }

    /// <summary>Human-readable badge summarizing the template kind, shown on the card.</summary>
    public string KindLabel => Kind == StackTemplateKind.Compose ? "Compose stack" : "Container";

    /// <summary>
    /// True while this template is being launched, so the card can show a spinner and disable its
    /// buttons. Not part of the static catalog data — set transiently by the Templates view model.
    /// </summary>
    [ObservableProperty]
    private bool _isLaunching;
}
