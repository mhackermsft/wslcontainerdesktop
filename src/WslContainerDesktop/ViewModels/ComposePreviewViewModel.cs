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

    /// <summary>
    /// Settings the user must actually decide about. Everything that resolved as expected is noise
    /// in a confirmation dialog, so only blocked, approximated and ignored settings surface directly.
    /// </summary>
    public IReadOnlyList<ComposeCompatibilitySetting> Attention =>
        Settings.Where(s => s.Disposition != ComposeSettingDisposition.Supported)
            .OrderBy(s => s.Disposition == ComposeSettingDisposition.Blocked ? 0 : 1)
            .ToList();

    public bool HasAttention => Attention.Count > 0;

    public string AttentionHeader
    {
        get
        {
            var blocked = Attention.Count(s => s.Disposition == ComposeSettingDisposition.Blocked);
            var other = Attention.Count - blocked;
            if (blocked > 0 && other > 0)
                return $"{blocked} blocking {Word(blocked, "issue")} and {other} {Word(other, "setting")} needing attention";
            return blocked > 0
                ? $"{blocked} blocking {Word(blocked, "issue")}"
                : $"{other} {Word(other, "setting")} needing attention";
        }
    }

    public string AllSettingsHeader => $"All resolved settings ({Settings.Count})";

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
                parts.Add($"{services.Count} {Word(services.Count, "service")} ({string.Join(", ", services)})");

            var pulls = Settings
                .Where(s => s.Setting == "image" && s.Explanation.Contains("Pull", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.EffectiveValue.Trim())
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (pulls.Count > 0)
                parts.Add($"downloads {pulls.Count} {Word(pulls.Count, "image")} ({string.Join(", ", pulls)})");

            var ports = Settings
                .Where(s => s.Setting == "ports")
                .SelectMany(s => s.EffectiveValue.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (ports.Count > 0)
                parts.Add($"publishes {string.Join(", ", ports)}");

            if (parts.Count == 0)
                return "No container changes are required.";
            var text = string.Join(" · ", parts);
            return char.ToUpperInvariant(text[0]) + text[1..] + ".";
        }
    }

    private static string Word(int count, string singular) => count == 1 ? singular : singular + "s";
}
