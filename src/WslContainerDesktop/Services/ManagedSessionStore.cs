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

/// <summary>Persists app-created session identities without exposing arbitrary deletion paths.</summary>
public sealed class ManagedSessionStore
{
    private readonly object _gate = new();
    private readonly string _file;
    private readonly ILogger _logger;
    private List<ManagedSessionDefinition> _sessions = [];
    private Exception? _loadError;

    /// <summary>Creates a store rooted in the package's externally accessible local cache.</summary>
#if !WSLCD_TEST_NO_DISPATCHER
    public ManagedSessionStore(ILogger<ManagedSessionStore> logger)
        : this(logger, Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path,
            "WslContainerDesktop", "managed-sessions"))
    {
    }
#endif

    internal ManagedSessionStore(ILogger logger, string root)
    {
        _logger = logger;
        Root = Path.GetFullPath(root);
        _file = Path.Combine(Root, "sessions.json");
        Load();
    }

    /// <summary>Root containing definitions and app-owned session directories.</summary>
    public string Root { get; }
    /// <summary>Whether saved definitions could not safely be read.</summary>
    public bool HasLoadError => _loadError is not null;

    /// <summary>Returns the fixed app-owned storage directory for a definition.</summary>
    public string StoragePath(ManagedSessionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Id == Guid.Empty)
            throw new ArgumentException("Session storage identity is missing.", nameof(definition));
        return Path.Combine(Root, "storage", definition.Id.ToString("N"));
    }

    /// <summary>Returns a copy of all stored definitions.</summary>
    public IReadOnlyList<ManagedSessionDefinition> Snapshot()
    {
        lock (_gate)
            return _sessions.ToArray();
    }

    /// <summary>Persists a new uniquely named definition.</summary>
    public void Add(ManagedSessionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Validate(definition);
        Mutate(current =>
        {
            if (current.Any(existing => existing.Id == definition.Id || existing.Name == definition.Name))
                throw new InvalidOperationException(UiText.Get("ManagedSession_Duplicate",
                    "A session with this name already exists."));
            return [.. current, definition];
        });
    }

    /// <summary>Removes a definition only if it still matches the captured identity.</summary>
    public void Remove(ManagedSessionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Mutate(current =>
        {
            if (current.Count(item => item == definition) != 1)
                throw new InvalidOperationException(UiText.Get("ManagedSession_Changed",
                    "The session changed. Refresh and try again."));
            return current.Where(item => item != definition).ToList();
        });
    }

    /// <summary>Persists deletion intent so an interrupted cleanup is not restarted on launch.</summary>
    public ManagedSessionDefinition MarkForDeletion(ManagedSessionDefinition definition, AutomationSessionOwner? owner = null)
    {
        var pending = definition with { PendingDeletion = true, PendingRemovalOwner = owner ?? definition.PendingRemovalOwner };
        Mutate(current =>
        {
            if (current.Count(item => item == definition) != 1)
                throw new InvalidOperationException(UiText.Get("ManagedSession_Changed",
                    "The session changed. Refresh and try again."));
            return current.Select(item => item == definition ? pending : item).ToList();
        });
        return pending;
    }

    /// <summary>Persists the engine that owns a successfully started session.</summary>
    public ManagedSessionDefinition MarkEngine(ManagedSessionDefinition definition, string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var updated = definition with { LastExecutablePath = executable };
        Mutate(current =>
        {
            if (current.Count(item => item == definition) != 1)
                throw new InvalidOperationException(UiText.Get("ManagedSession_Changed",
                    "The session changed. Refresh and try again."));
            return current.Select(item => item == definition ? updated : item).ToList();
        });
        return updated;
    }

    /// <summary>Records that the original storage directory was created before starting the SDK session.</summary>
    public ManagedSessionDefinition MarkStorageCreated(ManagedSessionDefinition definition)
    {
        var initialized = definition with { StorageCreated = true };
        Mutate(current =>
        {
            if (current.Count(item => item == definition) != 1)
                throw new InvalidOperationException(UiText.Get("ManagedSession_Changed",
                    "The session changed. Refresh and try again."));
            return current.Select(item => item == definition ? initialized : item).ToList();
        });
        return initialized;
    }

    private void Mutate(Func<IReadOnlyList<ManagedSessionDefinition>, List<ManagedSessionDefinition>> change)
    {
        lock (_gate)
        {
            if (_loadError is not null)
                throw new InvalidOperationException(UiText.Get("ManagedSession_ReadFailed",
                    "Saved sessions could not be read. They were not changed."), _loadError);
            var next = change(_sessions);
            Save(next);
            _sessions = next;
        }
    }

    private void Load()
    {
        if (!File.Exists(_file))
            return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<ManagedSessionDefinition>>(File.ReadAllText(_file))
                ?? throw new InvalidDataException("Session list is empty.");
            foreach (var item in loaded)
                Validate(item);
            if (loaded.Select(item => item.Id).Distinct().Count() != loaded.Count ||
                loaded.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != loaded.Count)
                throw new InvalidDataException("Session identities are duplicated.");
            _sessions = loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   InvalidDataException or ArgumentException)
        {
            _loadError = ex;
            _logger.LogWarning(ex, "Could not read the app-created session list.");
        }
    }

    private void Save(IReadOnlyList<ManagedSessionDefinition> sessions)
    {
        Directory.CreateDirectory(Root);
        var temporary = Path.Combine(Root, $".sessions-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not remove a temporary session list.");
                }
            }
        }
    }

    private static void Validate(ManagedSessionDefinition definition)
    {
        if (definition is null || definition.Id == Guid.Empty || string.IsNullOrWhiteSpace(definition.Name) ||
            definition.Name != definition.Name.Trim() || definition.Name.Any(char.IsControl) ||
            definition.PendingRemovalOwner is { } owner &&
                (!definition.PendingDeletion || !owner.IsValid ||
                 !string.Equals(owner.SessionName, definition.Name, StringComparison.Ordinal)))
            throw new InvalidDataException("Invalid saved session identity.");
    }
}
