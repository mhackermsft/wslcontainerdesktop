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
/// File-backed <see cref="IRunProfileStore"/>. Profiles live in
/// <c>%LOCALAPPDATA%\WslContainerDesktop\run-profiles.json</c>, next to <c>settings.json</c>, so
/// they survive app restarts and can be reused by other features. Load failures never crash the
/// app: a corrupt file simply yields an empty set.
/// </summary>
public sealed class RunProfileStore : IRunProfileStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WslContainerDesktop");

    private static readonly string ProfilesFile = Path.Combine(SettingsDirectory, "run-profiles.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly ILogger<RunProfileStore> _logger;
    private readonly List<RunProfile> _profiles = new();

    public RunProfileStore(ILogger<RunProfileStore> logger)
    {
        _logger = logger;
        Load();
    }

    public IReadOnlyList<RunProfile> GetAll() =>
        _profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<RunProfile> GetForImage(string image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return Array.Empty<RunProfile>();
        }

        var reference = image.Trim();
        return _profiles
            .Where(p => string.Equals(p.Options.Image, reference, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public RunProfile? Get(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : _profiles.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public void Save(RunProfile profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
        {
            return;
        }

        profile.Name = profile.Name.Trim();
        _profiles.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        _profiles.Add(profile);
        Persist();
    }

    public void Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var removed = _profiles.RemoveAll(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            Persist();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(ProfilesFile))
            {
                return;
            }

            var json = File.ReadAllText(ProfilesFile);
            var loaded = JsonSerializer.Deserialize<List<RunProfile>>(json);
            if (loaded is null)
            {
                return;
            }

            _profiles.Clear();
            foreach (var profile in loaded)
            {
                if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
                {
                    continue;
                }

                profile.Name = profile.Name.Trim();
                profile.Options ??= new RunContainerOptions();
                // Guard against duplicate names from a hand-edited file.
                _profiles.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
                _profiles.Add(profile);
            }
        }
        catch (Exception ex)
        {
            // A corrupt profiles file should never crash the app; start with none.
            _logger.LogWarning(ex, "Failed to load run profiles from {Path}; starting empty.", ProfilesFile);
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(
                _profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                SerializerOptions);
            File.WriteAllText(ProfilesFile, json);
        }
        catch (Exception ex)
        {
            // Best effort; ignore persistence failures.
            _logger.LogWarning(ex, "Failed to save run profiles to {Path}.", ProfilesFile);
        }
    }
}
