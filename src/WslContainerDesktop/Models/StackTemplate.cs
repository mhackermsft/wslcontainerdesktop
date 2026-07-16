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

/// <summary>Where a template came from, which governs whether it can be edited or deleted.</summary>
public enum TemplateSource
{
    /// <summary>Shipped in the app (code-baked). Can be hidden but never edited or deleted.</summary>
    BuiltIn,

    /// <summary>Authored by the user in-app. Can be edited, duplicated, and deleted.</summary>
    User,

    /// <summary>Brought in via Import. Can be edited, duplicated, and deleted.</summary>
    Imported,
}

/// <summary>
/// A curated, one-click template from the gallery. Single-container templates prefill the Run
/// dialog with <see cref="RunOptions"/>; compose templates import <see cref="ComposeYaml"/> as a
/// new Compose project. Templates are static, bundled data (see <c>TemplateCatalog</c>).
/// </summary>
public sealed partial class StackTemplate : ObservableObject
{
    public required string Id { get; set; }

    /// <summary>Display name, e.g. "PostgreSQL".</summary>
    public required string Name { get; set; }

    /// <summary>Grouping shown as a section header, e.g. "Databases".</summary>
    public required string Category { get; set; }

    /// <summary>Short one-line description shown on the card.</summary>
    public required string Description { get; set; }

    /// <summary>Segoe MDL2 glyph for the card icon.</summary>
    public string Glyph { get; set; } = "\uE7B8"; // generic package

    public StackTemplateKind Kind { get; set; } = StackTemplateKind.SingleContainer;

    /// <summary>Prefilled run options for <see cref="StackTemplateKind.SingleContainer"/> templates.</summary>
    public RunContainerOptions? RunOptions { get; set; }

    /// <summary>Raw docker-compose YAML for <see cref="StackTemplateKind.Compose"/> templates.</summary>
    public string? ComposeYaml { get; set; }

    /// <summary>Default project name suggested when importing a compose template.</summary>
    public string? ComposeProjectName { get; set; }

    /// <summary>Optional note surfaced to the user (e.g. default credentials) before launching.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Where this template came from. Built-ins are code-baked (hide-only); User/Imported templates
    /// live in <c>user-templates.json</c> and can be edited, duplicated, and deleted. Mutable so the
    /// stores can normalize it on load/save.
    /// </summary>
    public TemplateSource Source { get; set; } = TemplateSource.BuiltIn;

    /// <summary>True when the user owns this template (authored or imported) and may edit/delete it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUserManaged => Source is TemplateSource.User or TemplateSource.Imported;

    /// <summary>Short human-readable badge for the template's source, shown on the card.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SourceLabel => Source switch
    {
        TemplateSource.User => "Custom",
        TemplateSource.Imported => "Imported",
        _ => "Built-in",
    };

    /// <summary>Human-readable badge summarizing the template kind, shown on the card.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string KindLabel => Kind == StackTemplateKind.Compose ? "Compose stack" : "Container";

    /// <summary>
    /// True while this template is being launched, so the card can show a spinner and disable its
    /// buttons. Not part of the static catalog data — set transiently by the Templates view model.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusyCard))]
    [NotifyPropertyChangedFor(nameof(CanLaunch))]
    [property: System.Text.Json.Serialization.JsonIgnore]
    private bool _isLaunching;

    /// <summary>
    /// True while this template is being removed (torn down). Set transiently by the Templates view
    /// model so the card can show a spinner and disable its buttons.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusyCard))]
    [NotifyPropertyChangedFor(nameof(CanLaunch))]
    [property: System.Text.Json.Serialization.JsonIgnore]
    private bool _isRemoving;

    /// <summary>
    /// True when this template currently has running/created resources (its container or compose
    /// project exists). Recomputed from the live engine snapshot; drives the "Deployed" badge and the
    /// Remove button. Not part of the static catalog data.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLaunch))]
    [property: System.Text.Json.Serialization.JsonIgnore]
    private bool _isDeployed;

    /// <summary>
    /// True when the user has hidden this template from the gallery. Recomputed from the visibility
    /// store; drives filtering and the "Show hidden" dimmed presentation. Not persisted on the
    /// template itself (the hidden-Id set lives in <c>template-visibility.json</c>).
    /// </summary>
    [ObservableProperty]
    [property: System.Text.Json.Serialization.JsonIgnore]
    [NotifyPropertyChangedFor(nameof(CardOpacity))]
    private bool _isHidden;

    /// <summary>Dims the card while it is hidden (only visible when "Show hidden" is on).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double CardOpacity => IsHidden ? 0.5 : 1.0;

    /// <summary>True while the card is launching or removing — used to disable all card actions.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBusyCard => IsLaunching || IsRemoving;

    /// <summary>
    /// True when the Launch button should be enabled: not busy and not already deployed. A deployed
    /// template must be removed before it can be launched again, so Launch is visibly disabled.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanLaunch => !IsBusyCard && !IsDeployed;
}
