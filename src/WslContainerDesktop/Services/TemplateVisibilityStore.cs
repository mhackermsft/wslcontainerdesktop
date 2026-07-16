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

namespace WslContainerDesktop.Services;

/// <summary>
/// File-backed <see cref="ITemplateVisibilityStore"/>. The hidden-id set lives in
/// <c>%LOCALAPPDATA%\WslContainerDesktop\template-visibility.json</c>. Load failures never crash the
/// app: a corrupt file simply yields "nothing hidden".
/// </summary>
public sealed class TemplateVisibilityStore : ITemplateVisibilityStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WslContainerDesktop");

    private static readonly string VisibilityFile = Path.Combine(SettingsDirectory, "template-visibility.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly ILogger<TemplateVisibilityStore> _logger;
    private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);

    public TemplateVisibilityStore(ILogger<TemplateVisibilityStore> logger)
    {
        _logger = logger;
        Load();
    }

    public event EventHandler? Changed;

    public bool IsHidden(string id) =>
        !string.IsNullOrWhiteSpace(id) && _hidden.Contains(id.Trim());

    public void SetHidden(string id, bool hidden)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var key = id.Trim();
        var changed = hidden ? _hidden.Add(key) : _hidden.Remove(key);
        if (!changed)
        {
            return;
        }

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(VisibilityFile))
            {
                return;
            }

            var json = File.ReadAllText(VisibilityFile);
            var loaded = JsonSerializer.Deserialize<List<string>>(json);
            if (loaded is null)
            {
                return;
            }

            _hidden.Clear();
            foreach (var id in loaded)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _hidden.Add(id.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt file should never crash the app; treat everything as visible.
            _logger.LogWarning(ex, "Failed to load template visibility from {Path}; nothing hidden.", VisibilityFile);
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(
                _hidden.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
                SerializerOptions);
            File.WriteAllText(VisibilityFile, json);
        }
        catch (Exception ex)
        {
            // Best effort; ignore persistence failures.
            _logger.LogWarning(ex, "Failed to save template visibility to {Path}.", VisibilityFile);
        }
    }
}
