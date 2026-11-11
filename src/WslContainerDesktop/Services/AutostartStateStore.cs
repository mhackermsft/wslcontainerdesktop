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

/// <summary>Preserves unassigned history and session-qualified automatic-start state.</summary>
public sealed class AutostartStateStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger _logger;
    private List<AutostartEntry> _entries = [];
    private Exception? _loadError;

    /// <summary>Raised after a successful durable state change.</summary>
    public event EventHandler? Changed;

    /// <summary>Creates the store in the application's local data directory.</summary>
    public AutostartStateStore(ILogger<AutostartStateStore> logger)
        : this(logger, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WslContainerDesktop", "container-autostart.json"))
    {
    }

    internal AutostartStateStore(ILogger logger, string path)
    {
        _logger = logger;
        _path = path;
        FileWasPresent = File.Exists(path);
        Load();
    }

    /// <summary>Whether a state file already existed when the app started.</summary>
    public bool FileWasPresent { get; }
    /// <summary>True if saved history cannot safely be read or overwritten.</summary>
    public bool HasLoadError => _loadError is not null;

    /// <summary>Returns a stable snapshot without exposing the store's mutable collection.</summary>
    public IReadOnlyList<AutostartEntry> Snapshot()
    {
        lock (_gate)
            return _entries.Select(Clone).ToArray();
    }

    /// <summary>Whether the user has enabled tracking for this session.</summary>
    public bool Tracks(AutomationSessionOwner owner)
    {
        lock (_gate)
            return _entries.Any(entry => entry.TracksSession && SameOwner(entry.SessionOwner, owner));
    }

    /// <summary>Explicitly enables automatic-start tracking for one session.</summary>
    public void EnableTracking(AutomationSessionOwner owner)
    {
        ValidateOwner(owner);
        Mutate(current => current.Any(entry => entry.TracksSession && SameOwner(entry.SessionOwner, owner))
            ? null : [.. current, new AutostartEntry { SessionOwner = owner, TracksSession = true }]);
    }

    /// <summary>Assigns matching legacy history to one chosen session and enables its tracking.</summary>
    public void AssignLegacy(AutostartEntry legacy, AutomationSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ValidateOwner(owner);
        if (legacy.TracksSession || legacy.SessionOwner?.IsValid == true || !HasIdentity(legacy))
            throw ChangedEntry();
        Mutate(current =>
        {
            var matches = current.Where(entry => !entry.TracksSession && entry.SessionOwner?.IsValid != true &&
                entry.Id == legacy.Id && entry.Name == legacy.Name).ToArray();
            if (matches.Length == 0)
                throw ChangedEntry();
            var result = current.Select(entry => matches.Contains(entry) ? entry with { SessionOwner = owner } : entry).ToList();
            if (!result.Any(entry => entry.TracksSession && SameOwner(entry.SessionOwner, owner)))
                result.Add(new AutostartEntry { SessionOwner = owner, TracksSession = true });
            return result;
        });
    }

    /// <summary>Replaces only one enrolled session's remembered containers.</summary>
    public void ReplaceOwned(AutomationSessionOwner owner, IReadOnlyList<AutostartEntry> entries)
    {
        ValidateOwner(owner);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Any(entry => entry.TracksSession || !HasIdentity(entry) || !SameOwner(entry.SessionOwner, owner)))
            throw new ArgumentException("Invalid automatic-start entries.", nameof(entries));
        Mutate(current =>
        {
            if (!current.Any(entry => entry.TracksSession && SameOwner(entry.SessionOwner, owner)))
                throw ChangedEntry();
            var previous = current.Where(entry => !entry.TracksSession && SameOwner(entry.SessionOwner, owner)).ToArray();
            var updated = entries.Select(entry =>
            {
                var match = previous.FirstOrDefault(old =>
                    !string.IsNullOrWhiteSpace(entry.Id) && old.Id == entry.Id ||
                    !string.IsNullOrWhiteSpace(entry.Name) && old.Name == entry.Name);
                return match is null ? entry : entry with { AdditionalData = match.AdditionalData };
            }).ToArray();
            if (previous.SequenceEqual(updated))
                return null;
            return [.. current.Where(entry => entry.TracksSession || !SameOwner(entry.SessionOwner, owner)), .. updated];
        });
    }

    internal void ForgetContainer(AutomationSessionOwner owner, string name, string? id = null)
    {
        ValidateOwner(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Mutate(current => current.Where(entry => entry.TracksSession || !SameOwner(entry.SessionOwner, owner) ||
            !(string.Equals(entry.Name.TrimStart('/'), name.TrimStart('/'), StringComparison.Ordinal) ||
              !string.IsNullOrWhiteSpace(id) && string.Equals(entry.Id, id, StringComparison.Ordinal))).ToList());
    }

    /// <summary>Durably removes one owner's tracking marker and automatic-start history.</summary>
    internal void ForgetOwner(AutomationSessionOwner owner)
    {
        ValidateOwner(owner);
        Mutate(current => current.Where(entry => !SameOwner(entry.SessionOwner, owner)).ToList());
    }

    internal static bool SameOwner(AutomationSessionOwner? left, AutomationSessionOwner? right) =>
        left?.IsValid == true && right?.IsValid == true &&
        string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.SessionName, right.SessionName, StringComparison.Ordinal);

    private void Mutate(Func<IReadOnlyList<AutostartEntry>, List<AutostartEntry>?> change)
    {
        bool changed;
        lock (_gate)
        {
            if (_loadError is not null)
                throw new InvalidOperationException(UiText.Get("Automation_AutostartReadFailed",
                    "Saved automatic-start history could not be read. It was not changed."), _loadError);
            var next = change(_entries);
            changed = next is not null && !_entries.SequenceEqual(next);
            if (!changed)
                return;
            Save(next!);
            _entries = next!;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Load()
    {
        if (!FileWasPresent)
            return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<AutostartEntry>>(File.ReadAllText(_path));
            if (loaded is null || loaded.Any(entry => entry is null ||
                entry.Id is null || entry.Name is null ||
                entry.TracksSession && (entry.SessionOwner?.IsValid != true || HasIdentity(entry)) ||
                !entry.TracksSession && !HasIdentity(entry)))
                throw new InvalidDataException("Invalid automatic-start history.");
            _entries = loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _loadError = ex;
            _logger.LogWarning(ex, "Could not read automatic-start history from {Path}.", _path);
        }
    }

    private void Save(IReadOnlyList<AutostartEntry> entries)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".container-autostart-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not remove temporary automatic-start history.");
                }
            }
        }
    }

    private static bool HasIdentity(AutostartEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.Id) || !string.IsNullOrWhiteSpace(entry.Name);

    private static AutostartEntry Clone(AutostartEntry entry) => entry with
    {
        AdditionalData = entry.AdditionalData is null
            ? null : new Dictionary<string, JsonElement>(entry.AdditionalData, StringComparer.Ordinal),
    };

    private static void ValidateOwner(AutomationSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.IsValid)
            throw new ArgumentException(UiText.Get("Automation_InvalidOwner",
                "Choose a valid engine and session before continuing."), nameof(owner));
    }

    private static InvalidOperationException ChangedEntry() =>
        new(UiText.Get("Automation_EntryChanged", "This setting changed. Refresh before assigning a session."));
}
