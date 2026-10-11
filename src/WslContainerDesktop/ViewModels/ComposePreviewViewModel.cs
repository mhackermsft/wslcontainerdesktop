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

using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>
/// Immutable view model for the Compose compatibility review dialog. It summarizes the planner's
/// evidence so users can decide whether to apply a Compose operation before the supervisor touches containers.
/// </summary>
/// <remarks>No commands here can edit or override blocked planner evidence.</remarks>
public sealed class ComposePreviewViewModel(ComposeCompatibilityPreview preview)
{
    /// <summary>Dialog title naming the Compose operation and project.</summary>
    public string Title => UiText.Get("Workload_Text_Review_Compose_0_1_857679", "Review Compose {0}: {1}", UiText.Translate(preview.Operation.ToString()), preview.Project);
    /// <summary>Short planner summary displayed near the top of the dialog.</summary>
    public string Summary => UiText.Translate(preview.Summary);
    /// <summary>True when no blocking compatibility issue prevents applying the operation.</summary>
    public bool CanApply => preview.CanApply;
    /// <summary>True when the planner found non-blocking warnings.</summary>
    public bool HasWarnings => preview.HasWarnings;
    /// <summary>All resolved Compose settings, including supported ones hidden from the attention list.</summary>
    public IReadOnlyList<ComposeCompatibilitySetting> Settings => preview.Settings;
    /// <summary>Primary button label chosen for the current lifecycle operation.</summary>
    public string ApplyLabel => preview.Operation == ComposeLifecycleOperation.Up ? UiText.Get("Workload_Text_Apply_reviewed_plan_844b63", "Apply reviewed plan") : UiText.Get("Workload_Text_Confirm_operation_382ccb", "Confirm operation");

    /// <summary>
    /// Settings the user must actually decide about. Everything that resolved as expected is noise
    /// in a confirmation dialog, so only blocked, approximated and ignored settings surface directly.
    /// </summary>
    public IReadOnlyList<ComposeCompatibilitySetting> Attention =>
        Settings.Where(s => s.Disposition != ComposeSettingDisposition.Supported)
            .OrderBy(s => s.Disposition == ComposeSettingDisposition.Blocked ? 0 : 1)
            .ToList();

    /// <summary>True when at least one setting needs the user to read the attention section.</summary>
    public bool HasAttention => Attention.Count > 0;

    /// <summary>Header text that counts blocking issues separately from softer warnings.</summary>
    public string AttentionHeader
    {
        get
        {
            var blocked = Attention.Count(s => s.Disposition == ComposeSettingDisposition.Blocked);
            var other = Attention.Count - blocked;
            if (blocked > 0 && other > 0)
                return UiText.Get("Workload_Text_0_blocking_1_and_2_3_e38847", "{0} blocking {1} and {2} {3} needing attention", blocked, Word(blocked, "issue"), other, Word(other, "setting"));
            return blocked > 0
                ? UiText.Get("Workload_Text_0_blocking_1_8133a5", "{0} blocking {1}", blocked, Word(blocked, "issue"))
                : UiText.Get("Workload_Text_0_1_needing_attention_0540b9", "{0} {1} needing attention", other, Word(other, "setting"));
        }
    }

    /// <summary>Header for the full resolved-setting list.</summary>
    public string AllSettingsHeader => UiText.Get("Workload_Text_All_resolved_settings_0_442b43", "All resolved settings ({0})", Settings.Count);

    /// <summary>Plain-language description of the actual outcome, so the decision does not depend on
    /// reading every per-setting row.</summary>
    public string Outcome
    {
        get
        {
            var parts = new List<string>();
            var services = Settings
                .Where(s => s.Setting == "instances" && !string.IsNullOrWhiteSpace(s.EffectiveValue))
                .Select(s => s.Service)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (services.Count > 0)
                parts.Add(UiText.Get("Workload_Additional_284fe05548", "{0} {1} ({2})", services.Count, Word(services.Count, "service"), string.Join(", ", services)));

            var pulls = Settings
                .Where(s => s.Setting == "image" && s.Explanation.Contains("Pull", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.EffectiveValue.Trim())
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (pulls.Count > 0)
                parts.Add(UiText.Get("Workload_Text_downloads_0_1_2_6a06ac", "downloads {0} {1} ({2})", pulls.Count, Word(pulls.Count, "image"), string.Join(", ", pulls)));

            var ports = Settings
                .Where(s => s.Setting == "ports")
                .SelectMany(s => s.EffectiveValue.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (ports.Count > 0)
                parts.Add(UiText.Get("Workload_Text_publishes_0_e65451", "publishes {0}", string.Join(", ", ports)));

            if (parts.Count == 0)
                return UiText.Get("Workload_Text_No_container_changes_are_required_979b3a", "No container changes are required.");
            var text = string.Join(" · ", parts);
            return char.ToUpperInvariant(text[0]) + text[1..] + ".";
        }
    }

    private static string Word(int count, string singular) => UiText.Translate(count == 1 ? singular : singular + "s");
}
