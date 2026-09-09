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
            """);
        foreach (var feature in Enum.GetValues<WslcFeature>())
        {
            text.AppendLine();
            text.Append(feature).Append(": ").Append(capabilities[feature].Support);
        }

        // Only enum names/states enter the prompt: paths, raw help and probe errors may contain local data.
        return text.ToString();
    }
}
