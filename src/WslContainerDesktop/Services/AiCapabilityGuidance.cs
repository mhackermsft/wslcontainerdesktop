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

using System.Text;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Builds the safety and capability instructions injected into AI prompts before tool use.
/// The text tells providers which <c>wslc.exe</c> features are known, unsupported or unknown so the assistant does not invent commands.
/// </summary>
internal static class AiCapabilityGuidance
{
    /// <summary>
    /// Reads the shared WSLC capability snapshot and returns prompt guidance, or conservative fallback text when evidence is unavailable.
    /// </summary>
    /// <param name="service">Optional capability service; null means no evidence is available.</param>
    /// <param name="ct">Cancels the capability read.</param>
    /// <returns>Plain-text guidance safe to include in an AI system prompt.</returns>
    internal static async Task<string> GetAsync(IWslcCapabilitiesService? service, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (service is null)
            return Unavailable;
        try
        {
            var snapshot = await service.GetAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return Build(snapshot);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or
            System.ComponentModel.Win32Exception or TimeoutException)
        {
            // Raw probe errors can include local paths, arguments or credentials.
            return Unavailable;
        }
    }

    private const string Unavailable = """
        Configured WSLC capability evidence is unavailable. Optional support is Unknown, not Unsupported.
        WSL/wslc 3.0.1 or later is required before wslc-backed features are used.
        Do not invent commands/flags or infer optional support from versions.
        In interactive chat, query engine_capabilities again for current optional evidence. No speculative mutation or backend retry.
        Evidence never grants permission. Compose and app-owned restart/auto-heal are not native CLI features.
        """;

    /// <summary>
    /// Formats a capability snapshot into the exact rules the assistant should follow when suggesting or invoking WSLC commands.
    /// </summary>
    /// <param name="capabilities">Current point-in-time WSLC feature evidence.</param>
    /// <returns>Prompt text containing only feature names and support states.</returns>
    internal static string Build(WslcCapabilities capabilities)
    {
        var text = new StringBuilder("""
            Optional CLI availability beyond the gated WSL/wslc 3.0.1 baseline:
            Only suggest optional commands/flags explicitly marked Supported below.
            Unsupported means absent from recognized help; Unknown means availability could not be established.
            Never treat Unknown as Supported or infer support from a version number.
            Create-prefixed entries apply only to `wslc create`; other entries apply to `wslc run`.
            `wslc container cp`, `network connect/disconnect`, create/run health flags except --health-start-interval,
            create --gpus/--pull, remove --volumes and prune --force are baseline 3.0.1 features, not optional evidence.
            Health flags apply at creation, not to an existing container. Health monitoring is distinct from restart/auto-heal.
            App-owned probes, TCP checks, restart policies and auto-heal require the app to keep running.
            Advertised CLI support does not include --restart or --add-host; do not suggest these flags.
            Unsupported/Unknown optional evidence must surface a diagnostic or use a documented app-owned substitute.
            Never retry failed native mutations through another backend.
            Compose is app-owned orchestration, not an advertised native Compose command. Use project tools for saved projects.
            This is point-in-time help evidence obtained through the shared capability service, not a live guarantee.
            In interactive chat, query engine_capabilities after engine/configuration changes. Evidence never grants permission.
            The shared cache can retain complete help evidence for five minutes, or failed/partial evidence for 15 seconds;
            executable/configuration changes invalidate it. A query is not necessarily a new probe.
            """);
        text.AppendLine();
        text.Append("Evidence completeness: ").Append(capabilities.HasProbeFailures ? "partial/unknown" : "complete");
        foreach (var feature in Enum.GetValues<WslcFeature>())
        {
            text.AppendLine();
            text.Append(feature).Append(": ").Append(capabilities[feature].Support);
        }

        // Only enum names/states enter the prompt: paths, raw help and probe errors may contain local data.
        return text.ToString();
    }
}
