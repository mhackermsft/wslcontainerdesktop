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

using System.Text.Json;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// File-backed <see cref="IComposeProjectStore"/>. Projects live in
/// <c>%LOCALAPPDATA%\WslContainerDesktop\compose-projects.json</c>, next to <c>settings.json</c>
/// and <c>run-profiles.json</c>. Load failures never crash the app: a corrupt file yields an
/// empty set.
/// </summary>
public sealed class ComposeProjectStore : IComposeProjectStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WslContainerDesktop");

    private static readonly string ProjectsFile = Path.Combine(SettingsDirectory, "compose-projects.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly ILogger<ComposeProjectStore> _logger;
    private readonly List<ComposeProject> _projects = new();
    private readonly object _gate = new();

    public ComposeProjectStore(ILogger<ComposeProjectStore> logger)
    {
        _logger = logger;
        Load();
    }

    public IReadOnlyList<ComposeProject> GetAll()
    {
        lock (_gate)
        {
            return _projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public ComposeProject? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        lock (_gate)
        {
            return _projects.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Save(ComposeProject project)
    {
        if (project is null || string.IsNullOrWhiteSpace(project.Name))
        {
            return;
        }

        lock (_gate)
        {
            project.Name = project.Name.Trim();
            _projects.RemoveAll(p => string.Equals(p.Name, project.Name, StringComparison.OrdinalIgnoreCase));
            _projects.Add(project);
            Persist();
        }
    }

    public void Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        lock (_gate)
        {
            var removed = _projects.RemoveAll(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                Persist();
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(ProjectsFile))
            {
                return;
            }

            var json = File.ReadAllText(ProjectsFile);
            var loaded = JsonSerializer.Deserialize<List<ComposeProject>>(json);
            if (loaded is null)
            {
                return;
            }

            _projects.Clear();
            foreach (var project in loaded)
            {
                if (project is null || string.IsNullOrWhiteSpace(project.Name))
                {
                    continue;
                }

                project.Name = project.Name.Trim();
                project.Services ??= new List<ComposeService>();
                _projects.RemoveAll(p => string.Equals(p.Name, project.Name, StringComparison.OrdinalIgnoreCase));
                _projects.Add(project);
            }
        }
        catch (Exception ex)
        {
            // A corrupt projects file should never crash the app; start with none.
            _logger.LogWarning(ex, "Failed to load compose projects from {Path}; starting empty.", ProjectsFile);
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(
                _projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                SerializerOptions);
            File.WriteAllText(ProjectsFile, json);
        }
        catch (Exception ex)
        {
            // Best effort; ignore persistence failures.
            _logger.LogWarning(ex, "Failed to save compose projects to {Path}.", ProjectsFile);
        }
    }
}
