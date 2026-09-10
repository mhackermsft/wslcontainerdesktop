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

internal static class AiCapabilityGuidance
{
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
        Do not invent commands/flags or infer support from versions. Retain WSLC 2.9.9.0 compatibility.
        In interactive chat, query engine_capabilities again for current evidence. No speculative mutation or native-to-legacy retry.
        Evidence never grants permission. Compose and app-owned restart/auto-heal are not native CLI features.
        """;

    internal static string Build(WslcCapabilities capabilities)
    {
        var text = new StringBuilder("""
            Optional CLI availability for the configured engine (not proof of Docker parity or app integration):
            Only suggest optional commands/flags explicitly marked Supported below.
            Unsupported means absent from recognized help; Unknown means availability could not be established.
            Never treat Unknown as Supported or infer support from a version number.
            ContainerCp means `wslc container cp`, not a documented top-level `wslc cp` alias.
            NetworkConnect/NetworkDisconnect mean `wslc network connect`/`wslc network disconnect`.
            Create-prefixed health entries apply only to `wslc create`; other health entries apply to `wslc run`.
            Health flags apply at creation, not to an existing container. Health monitoring is distinct from restart/auto-heal.
            App-owned probes, TCP checks, restart policies and auto-heal require the app to keep running.
            Advertised CLI support does not include --restart or --add-host; do not suggest these flags.
            Retain WSLC 2.9.9.0 compatibility. Unsupported permits only documented app legacy fallbacks;
            Unknown blocks optional backend selection. Never retry failed native mutations via a legacy backend.
            Compose is app-owned orchestration, not a native Compose command. Use project tools for saved projects.
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
