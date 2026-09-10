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

using System.Collections;
using System.Text.Json;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Tests.Services;

internal static class ComposeConformanceWorker
{
    public static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--compose-fixture")
        {
            Console.Error.WriteLine("This test-only entry point requires --compose-fixture <directory>.");
            return 2;
        }

        var directory = Path.GetFullPath(args[1]);
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);
        var project = ComposeImporter.ParseProject(
            File.ReadAllText(Path.Combine(directory, "compose.yaml")), environment, directory);
        Console.WriteLine(ComposeConformanceProjection.FromApp(project).ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
