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
/// File-backed <see cref="IUserTemplateStore"/>. Templates live in
/// <c>%LOCALAPPDATA%\WslContainerDesktop\user-templates.json</c>, next to <c>settings.json</c>. Load
/// failures never crash the app: a corrupt file simply yields an empty set. A template stored here is
/// always <see cref="TemplateSource.User"/> or <see cref="TemplateSource.Imported"/> — a
/// <see cref="TemplateSource.BuiltIn"/> value read from the file is coerced to User.
/// </summary>
public sealed class UserTemplateStore : IUserTemplateStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WslContainerDesktop");

    private static readonly string TemplatesFile = Path.Combine(SettingsDirectory, "user-templates.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly ILogger<UserTemplateStore> _logger;
    private readonly List<StackTemplate> _templates = new();

    public UserTemplateStore(ILogger<UserTemplateStore> logger)
    {
        _logger = logger;
        Load();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<StackTemplate> Templates => _templates;

    public bool Contains(string id) =>
        !string.IsNullOrWhiteSpace(id)
        && _templates.Any(t => string.Equals(t.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    public StackTemplate? Get(string id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : _templates.FirstOrDefault(t => string.Equals(t.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    public void Save(StackTemplate template)
    {
        if (!Upsert(template))
        {
            return;
        }

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveRange(IEnumerable<StackTemplate> templates)
    {
        var any = false;
        foreach (var template in templates)
        {
            any |= Upsert(template);
        }

        if (!any)
        {
            return;
        }

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var removed = _templates.RemoveAll(t =>
            string.Equals(t.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return false;
        }

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Inserts or replaces a template in memory (no persist). Returns false if invalid.</summary>
    private bool Upsert(StackTemplate? template)
    {
        if (template is null || string.IsNullOrWhiteSpace(template.Id))
        {
            return false;
        }

        // A template in this store is user-owned by definition; never let a BuiltIn source slip in.
        if (template.Source == TemplateSource.BuiltIn)
        {
            template.Source = TemplateSource.User;
        }

        _templates.RemoveAll(t => string.Equals(t.Id, template.Id, StringComparison.OrdinalIgnoreCase));
        _templates.Add(template);
        return true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(TemplatesFile))
            {
                return;
            }

            var json = File.ReadAllText(TemplatesFile);
            var loaded = JsonSerializer.Deserialize<List<StackTemplate>>(json, SerializerOptions);
            if (loaded is null)
            {
                return;
            }

            _templates.Clear();
            foreach (var template in loaded)
            {
                Upsert(template);
            }
        }
        catch (Exception ex)
        {
            // A corrupt file should never crash the app; start with none.
            _logger.LogWarning(ex, "Failed to load user templates from {Path}; starting empty.", TemplatesFile);
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(
                _templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                SerializerOptions);
            File.WriteAllText(TemplatesFile, json);
        }
        catch (Exception ex)
        {
            // Best effort; ignore persistence failures.
            _logger.LogWarning(ex, "Failed to save user templates to {Path}.", TemplatesFile);
        }
    }
}
