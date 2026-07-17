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

/// <summary>File-backed known dev container store. Load failures fall back to an empty list.</summary>
public sealed class DevContainerStore : IDevContainerStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WslContainerDesktop");

    private static readonly string DevContainersFile = Path.Combine(SettingsDirectory, "devcontainers.json");
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly ILogger<DevContainerStore> _logger;
    private readonly List<DevContainerConfig> _configs = new();
    private readonly object _gate = new();

    public DevContainerStore(ILogger<DevContainerStore> logger)
    {
        _logger = logger;
        Load();
    }

    public IReadOnlyList<DevContainerConfig> GetAll()
    {
        lock (_gate)
        {
            return _configs.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public DevContainerConfig? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        lock (_gate)
        {
            return _configs.FirstOrDefault(c => string.Equals(c.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Save(DevContainerConfig config)
    {
        if (config is null || string.IsNullOrWhiteSpace(config.Id))
        {
            return;
        }

        lock (_gate)
        {
            _configs.RemoveAll(c => string.Equals(c.Id, config.Id, StringComparison.OrdinalIgnoreCase));
            _configs.Add(config);
            Persist();
        }
    }

    public void Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        lock (_gate)
        {
            if (_configs.RemoveAll(c => string.Equals(c.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Persist();
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(DevContainersFile))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<DevContainerConfig>>(File.ReadAllText(DevContainersFile));
            if (loaded is null)
            {
                return;
            }

            _configs.Clear();
            foreach (var config in loaded.Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Id)))
            {
                config.ContainerEnv ??= new Dictionary<string, string>(StringComparer.Ordinal);
                config.ForwardPorts ??= new List<int>();
                config.Warnings ??= new List<string>();
                config.RunOptions ??= new RunContainerOptions();
                _configs.RemoveAll(c => string.Equals(c.Id, config.Id, StringComparison.OrdinalIgnoreCase));
                _configs.Add(config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load dev containers from {Path}; starting empty.", DevContainersFile);
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(
                DevContainersFile,
                JsonSerializer.Serialize(_configs.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(), SerializerOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save dev containers to {Path}.", DevContainersFile);
        }
    }
}
