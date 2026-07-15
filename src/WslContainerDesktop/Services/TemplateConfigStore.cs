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
/// File-backed <see cref="ITemplateConfigStore"/>. Configs live in
/// <c>%LOCALAPPDATA%\WslContainerDesktop\template-configs.json</c>, next to <c>settings.json</c>, so
/// a template's most recent Settings survive app restarts. Load failures never crash the app: a
/// corrupt file simply yields an empty set.
/// </summary>
public sealed class TemplateConfigStore : ITemplateConfigStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WslContainerDesktop");

    private static readonly string ConfigsFile = Path.Combine(SettingsDirectory, "template-configs.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly ILogger<TemplateConfigStore> _logger;
    private readonly List<TemplateConfig> _configs = new();

    public TemplateConfigStore(ILogger<TemplateConfigStore> logger)
    {
        _logger = logger;
        Load();
    }

    public TemplateConfig? Get(string templateId) =>
        string.IsNullOrWhiteSpace(templateId)
            ? null
            : _configs.FirstOrDefault(c => string.Equals(c.TemplateId, templateId.Trim(), StringComparison.OrdinalIgnoreCase));

    public void Save(TemplateConfig config)
    {
        if (config is null || string.IsNullOrWhiteSpace(config.TemplateId))
        {
            return;
        }

        config.TemplateId = config.TemplateId.Trim();
        _configs.RemoveAll(c => string.Equals(c.TemplateId, config.TemplateId, StringComparison.OrdinalIgnoreCase));
        _configs.Add(config);
        Persist();
    }

    public void Delete(string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return;
        }

        var removed = _configs.RemoveAll(c => string.Equals(c.TemplateId, templateId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            Persist();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(ConfigsFile))
            {
                return;
            }

            var json = File.ReadAllText(ConfigsFile);
            var loaded = JsonSerializer.Deserialize<List<TemplateConfig>>(json);
            if (loaded is null)
            {
                return;
            }

            _configs.Clear();
            foreach (var config in loaded)
            {
                if (config is null || string.IsNullOrWhiteSpace(config.TemplateId))
                {
                    continue;
                }

                config.TemplateId = config.TemplateId.Trim();
                // Guard against duplicate ids from a hand-edited file.
                _configs.RemoveAll(c => string.Equals(c.TemplateId, config.TemplateId, StringComparison.OrdinalIgnoreCase));
                _configs.Add(config);
            }
        }
        catch (Exception ex)
        {
            // A corrupt configs file should never crash the app; start with none.
            _logger.LogWarning(ex, "Failed to load template configs from {Path}; starting empty.", ConfigsFile);
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(
                _configs.OrderBy(c => c.TemplateId, StringComparer.OrdinalIgnoreCase).ToList(),
                SerializerOptions);
            File.WriteAllText(ConfigsFile, json);
        }
        catch (Exception ex)
        {
            // Best effort; ignore persistence failures.
            _logger.LogWarning(ex, "Failed to save template configs to {Path}.", ConfigsFile);
        }
    }
}
