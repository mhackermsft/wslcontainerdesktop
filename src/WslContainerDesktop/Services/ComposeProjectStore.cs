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
    private readonly string _settingsDirectory;
    private readonly string _projectsFile;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly ILogger<ComposeProjectStore> _logger;
    private readonly List<ComposeProject> _projects = new();
    private readonly object _gate = new();

    public ComposeProjectStore(ILogger<ComposeProjectStore> logger)
        : this(logger, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WslContainerDesktop"))
    {
    }

    internal ComposeProjectStore(ILogger<ComposeProjectStore> logger, string settingsDirectory)
    {
        _logger = logger;
        _settingsDirectory = settingsDirectory;
        _projectsFile = Path.Combine(settingsDirectory, "compose-projects.json");
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
            var previous = _projects.FirstOrDefault(p => string.Equals(p.Name, project.Name, StringComparison.OrdinalIgnoreCase));
            if (previous is not null && !ReferenceEquals(previous, project) && !project.AppliedStateKnown)
            {
                foreach (var applied in previous.AppliedServices)
                    project.AppliedServices.TryAdd(applied.Key, applied.Value);
                foreach (var replicaOverride in previous.ReplicaOverrides.Where(entry =>
                    project.Services.Any(service => service.Name == entry.Key)))
                    project.ReplicaOverrides.TryAdd(replicaOverride.Key, replicaOverride.Value);
            }
            project.AppliedStateKnown = true;
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
            if (!File.Exists(_projectsFile))
            {
                return;
            }

            var json = File.ReadAllText(_projectsFile);
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
                project.AppliedServices ??= new();
                project.ReplicaOverrides ??= new(StringComparer.Ordinal);
                _projects.RemoveAll(p => string.Equals(p.Name, project.Name, StringComparison.OrdinalIgnoreCase));
                _projects.Add(project);
            }
        }
        catch (Exception ex)
        {
            // A corrupt projects file should never crash the app; start with none.
            _logger.LogWarning(ex, "Failed to load compose projects from {Path}; starting empty.", _projectsFile);
        }
    }

    private void Persist()
    {
        var temporary = _projectsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            var json = JsonSerializer.Serialize(
                _projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                SerializerOptions);
            File.WriteAllText(temporary, json);
            File.Move(temporary, _projectsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save compose projects to {Path}.", _projectsFile);
            throw new InvalidOperationException("Compose project state could not be saved.", ex);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not remove temporary Compose state file."); }
        }
    }
}
