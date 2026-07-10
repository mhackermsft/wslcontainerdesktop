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
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed class SettingsService : ISettingsService
{
    private const string DefaultWslcPath = @"C:\Program Files\WSL\wslc.exe";

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WslContainerDesktop");

    private static readonly string SettingsFile = Path.Combine(SettingsDirectory, "settings.json");

    public string WslcPath { get; set; } = ResolveDefaultWslcPath();
    public int RefreshIntervalSeconds { get; set; } = 5;
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public string Theme { get; set; } = "Default";
    public string? WslDistro { get; set; }
    public List<RegistryEntry> Registries { get; set; } = new() { RegistryEntry.DockerHub() };

    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return;
            }

            var json = File.ReadAllText(SettingsFile);
            var dto = JsonSerializer.Deserialize<SettingsDto>(json);
            if (dto is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(dto.WslcPath))
            {
                WslcPath = dto.WslcPath;
            }

            RefreshIntervalSeconds = Math.Clamp(dto.RefreshIntervalSeconds, 2, 120);
            CloseToTray = dto.CloseToTray;
            StartMinimized = dto.StartMinimized;
            Theme = string.IsNullOrWhiteSpace(dto.Theme) ? "Default" : dto.Theme;
            WslDistro = string.IsNullOrWhiteSpace(dto.WslDistro) ? null : dto.WslDistro;

            // Load registries, always keeping a single Docker Hub default at the top.
            var registries = new List<RegistryEntry> { RegistryEntry.DockerHub() };
            if (dto.Registries is not null)
            {
                foreach (var r in dto.Registries)
                {
                    if (r is null || r.IsDefault || string.IsNullOrWhiteSpace(r.Host))
                    {
                        continue;
                    }

                    registries.Add(new RegistryEntry
                    {
                        Name = string.IsNullOrWhiteSpace(r.Name) ? r.Host! : r.Name!,
                        Host = r.Host!.Trim(),
                        Username = string.IsNullOrWhiteSpace(r.Username) ? null : r.Username,
                        IsAzure = r.IsAzure,
                        SubscriptionId = string.IsNullOrWhiteSpace(r.SubscriptionId) ? null : r.SubscriptionId,
                        AzureAcrName = string.IsNullOrWhiteSpace(r.AzureAcrName) ? null : r.AzureAcrName,
                    });
                }
            }

            Registries = registries;
        }
        catch
        {
            // Corrupt settings should never crash the app; fall back to defaults.
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var dto = new SettingsDto
            {
                WslcPath = WslcPath,
                RefreshIntervalSeconds = RefreshIntervalSeconds,
                CloseToTray = CloseToTray,
                StartMinimized = StartMinimized,
                Theme = Theme,
                WslDistro = WslDistro,
                Registries = Registries
                    .Where(r => !r.IsDefault && !string.IsNullOrWhiteSpace(r.Host))
                    .Select(r => new RegistryDto
                    {
                        Name = r.Name,
                        Host = r.Host,
                        Username = r.Username,
                        IsAzure = r.IsAzure,
                        SubscriptionId = r.SubscriptionId,
                        AzureAcrName = r.AzureAcrName,
                    })
                    .ToList(),
            };

            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Best effort; ignore persistence failures.
        }
    }

    private static string ResolveDefaultWslcPath()
    {
        if (File.Exists(DefaultWslcPath))
        {
            return DefaultWslcPath;
        }

        // Fall back to whatever is on PATH.
        return "wslc.exe";
    }

    private sealed class SettingsDto
    {
        public string? WslcPath { get; set; }
        public int RefreshIntervalSeconds { get; set; } = 5;
        public bool CloseToTray { get; set; } = true;
        public bool StartMinimized { get; set; }
        public string? Theme { get; set; }
        public string? WslDistro { get; set; }
        public List<RegistryDto>? Registries { get; set; }
    }

    private sealed class RegistryDto
    {
        public string? Name { get; set; }
        public string? Host { get; set; }
        public string? Username { get; set; }
        public bool IsDefault { get; set; }
        public bool IsAzure { get; set; }
        public string? SubscriptionId { get; set; }
        public string? AzureAcrName { get; set; }
    }
}
