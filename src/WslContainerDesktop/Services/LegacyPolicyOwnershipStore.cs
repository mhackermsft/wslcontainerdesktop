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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Persists explicit ownership for unchanged legacy policies without rewriting settings.</summary>
public sealed class LegacyPolicyOwnershipStore
{
    private const int CurrentVersion = 1;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<LegacyPolicyOwnershipStore> _logger;
    private readonly Dictionary<string, AutomationSessionOwner> _assignments = new(StringComparer.Ordinal);
    private Exception? _loadError;

    /// <summary>Raised after an assignment is safely saved.</summary>
    public event EventHandler? Changed;

    /// <summary>Creates the store in the application's local data directory.</summary>
    public LegacyPolicyOwnershipStore(ILogger<LegacyPolicyOwnershipStore> logger)
        : this(logger, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WslContainerDesktop", "automation-policy-owners.json"))
    {
    }

    internal LegacyPolicyOwnershipStore(ILogger<LegacyPolicyOwnershipStore> logger, string path)
    {
        _logger = logger;
        _path = path;
        Load();
    }

    /// <summary>True when the saved assignments could not be read and must not be overwritten.</summary>
    public bool HasLoadError => _loadError is not null;

    /// <summary>Finds explicit or previously assigned ownership for a health policy.</summary>
    public AutomationSessionOwner? OwnerFor(HealthCheckConfig policy) =>
        policy.SessionOwner?.IsValid == true ? policy.SessionOwner : Find(HealthKey(policy));

    /// <summary>Finds explicit or previously assigned ownership for a restart policy.</summary>
    public AutomationSessionOwner? OwnerFor(RestartPolicyConfig policy) =>
        policy.SessionOwner?.IsValid == true ? policy.SessionOwner : Find(RestartKey(policy));

    /// <summary>Saves ownership for one unchanged legacy health policy.</summary>
    public void Assign(HealthCheckConfig policy, AutomationSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.SessionOwner?.IsValid == true)
            throw new InvalidOperationException(UiText.Get("Automation_AssignmentAlreadyOwned",
                "This setting already has a session. Refresh before changing it."));
        AssignKey(HealthKey(policy), owner);
    }

    /// <summary>Saves ownership for one unchanged legacy restart policy.</summary>
    public void Assign(RestartPolicyConfig policy, AutomationSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.SessionOwner?.IsValid == true)
            throw new InvalidOperationException(UiText.Get("Automation_AssignmentAlreadyOwned",
                "This setting already has a session. Refresh before changing it."));
        AssignKey(RestartKey(policy), owner);
    }

    private AutomationSessionOwner? Find(string key)
    {
        lock (_gate)
            return _assignments.GetValueOrDefault(key);
    }

    private void AssignKey(string key, AutomationSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.IsValid)
            throw new ArgumentException(UiText.Get("Automation_InvalidOwner",
                "Choose a valid engine and session before continuing."), nameof(owner));

        lock (_gate)
        {
            if (_loadError is not null)
                throw new InvalidOperationException(UiText.Get("Automation_OwnershipReadFailed",
                    "Saved session assignments could not be read. They were not changed."), _loadError);
            if (_assignments.ContainsKey(key))
                throw new InvalidOperationException(UiText.Get("Automation_AssignmentAlreadyOwned",
                    "This setting already has a session. Refresh before changing it."));
            var next = new Dictionary<string, AutomationSessionOwner>(_assignments, StringComparer.Ordinal)
            {
                [key] = owner,
            };
            Save(next);
            _assignments.Clear();
            foreach (var pair in next)
                _assignments.Add(pair.Key, pair.Value);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;
        try
        {
            var document = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(_path));
            if (document is null || document.Version != CurrentVersion || document.Assignments is null ||
                document.Assignments.Any(entry => entry is null || !IsKey(entry.Key) || entry.Owner?.IsValid != true) ||
                document.Assignments.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count() != document.Assignments.Count)
                throw new InvalidDataException("Invalid automation policy ownership data.");
            foreach (var entry in document.Assignments)
                _assignments.Add(entry.Key, entry.Owner!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _loadError = ex;
            _logger.LogWarning(ex, "Could not read automation policy ownership data from {Path}.", _path);
        }
    }

    private void Save(IReadOnlyDictionary<string, AutomationSessionOwner> assignments)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".automation-policy-owners-{Guid.NewGuid():N}.tmp");
        try
        {
            var data = new StoreData
            {
                Version = CurrentVersion,
                Assignments = assignments.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new StoreEntry { Key = pair.Key, Owner = pair.Value }).ToList(),
            };
            File.WriteAllText(temporary, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not remove temporary automation ownership data.");
                }
            }
        }
    }

    private static bool IsKey(string? key) =>
        key is { Length: 64 } && key.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal static string HealthKey(HealthCheckConfig policy) => Hash(JsonSerializer.Serialize(new
    {
        RecordType = "health", policy.ContainerName, policy.Kind, policy.Command, policy.TcpPort,
        policy.IntervalSeconds, policy.MaxRestarts, policy.DesiredHealth,
    }));

    internal static string RestartKey(RestartPolicyConfig policy) => Hash(JsonSerializer.Serialize(new
    {
        RecordType = "restart", policy.ContainerName, policy.Policy, policy.MaxRestarts,
    }));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class StoreData
    {
        public int Version { get; set; }
        public List<StoreEntry>? Assignments { get; set; }
    }

    private sealed class StoreEntry
    {
        public string Key { get; set; } = string.Empty;
        public AutomationSessionOwner? Owner { get; set; }
    }
}
