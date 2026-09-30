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

using System.Text;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Reads and edits the WSLC settings YAML file through the WSLC service so the UI can show and change engine storage settings.
/// </summary>
public sealed class WslcSettingsFileService(IWslcService wslc, ILogger<WslcSettingsFileService> logger) : IWslcSettingsFileService
{
    private static readonly string RealLocalAppData =
        Environment.GetEnvironmentVariable("LOCALAPPDATA") ??
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly string DefaultSettingsPath = Path.Combine(RealLocalAppData, "wslc", "settings.yaml");

    private static readonly string DefaultStoragePath = Path.Combine(RealLocalAppData, "wslc", "sessions");

    /// <summary>Reads the current settings file and parses the storage path.</summary>
    public async Task<WslcSettingsFile> ReadAsync(CancellationToken ct = default)
    {
        string path;
        try
        {
            var info = await wslc.GetSystemInfoAsync(ct).ConfigureAwait(false);
            path = string.IsNullOrWhiteSpace(info.Client.SettingsFile) ? DefaultSettingsPath : info.Client.SettingsFile;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read wslc system info; falling back to default settings path.");
            path = DefaultSettingsPath;
        }

        if (!File.Exists(path))
        {
            return new WslcSettingsFile
            {
                SettingsFilePath = path,
                DefaultStoragePath = DefaultStoragePath,
                SettingsFileExists = false,
                // Creating the file from a packaged app would land in the package's private AppData.
                DirectEditAvailable = false,
            };
        }

        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var parsed = Parse(text);
        return new WslcSettingsFile
        {
            SettingsFilePath = path,
            StoragePath = parsed.StoragePath,
            CpuCount = parsed.CpuCount,
            MemorySize = parsed.MemorySize,
            MaxStorageSize = parsed.MaxStorageSize,
            DefaultBindingAddress = parsed.DefaultBindingAddress,
            CredentialStore = parsed.CredentialStore,
            DefaultStoragePath = DefaultStoragePath,
            SettingsFileExists = true,
            DirectEditAvailable = InPlaceAppDataEditSupported,
        };
    }

    /// <summary>Updates the session storage path while preserving unrelated YAML text.</summary>
    public async Task SetStoragePathAsync(string? path, CancellationToken ct = default)
    {
        var settings = await ReadAsync(ct).ConfigureAwait(false);
        if (!settings.DirectEditAvailable)
        {
            throw new InvalidOperationException(
                "The wslc settings file can't be edited from the app. Use Edit settings file and set session.storagePath there.");
        }

        var value = string.IsNullOrWhiteSpace(path) ? "default" : path.Trim();
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("The storage path contains unsupported characters.", nameof(path));
        }

        // Packaged apps may modify an existing file under the real AppData in place, but newly created
        // files (including temp files or replacements) are redirected to the package's private store
        // where wslc would never see them. So rewrite the existing file through its own handle only.
        await using var stream = new FileStream(settings.SettingsFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var original = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        var encoding = reader.CurrentEncoding is UTF8Encoding utf8 && utf8.GetPreamble().Length > 0
            ? reader.CurrentEncoding
            : new UTF8Encoding(false);
        var updated = SetSessionStoragePath(original, value);
        logger.LogInformation("Updating wslc session.storagePath (previous value: {Previous}).", Parse(original).StoragePath ?? "unset");

        stream.SetLength(0);
        stream.Position = 0;
        await using var writer = new StreamWriter(stream, encoding, leaveOpen: true);
        await writer.WriteAsync(updated.AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>In-place edits of existing AppData files escape MSIX redirection only on Windows 10 1903+.</summary>
    private static bool InPlaceAppDataEditSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362);

    /// <summary>Validates a user-entered storage path before writing it to settings.</summary>
    public WslcStoragePathValidation ValidateStoragePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new(false, "Choose an empty folder for new wslc session storage.");
        }

        if (!Directory.Exists(path))
        {
            return new(false, "The selected path does not exist.");
        }

        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            return new(false, "The selected path is not a directory.");
        }

        try
        {
            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                return new(false, "The selected folder is not empty. wslc requires an empty storage folder.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, "The selected folder could not be read.");
        }

        return WslcStoragePathValidation.Valid;
    }

    /// <summary>Reads disk capacity information for the selected storage path when available.</summary>
    public Task<long?> GetStorageDiskBytesAsync(string storagePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(storagePath) || !Directory.Exists(storagePath))
        {
            return Task.FromResult<long?>(null);
        }

        return Task.Run<long?>(() =>
        {
            try
            {
                long total = 0;
                foreach (var pattern in new[] { "*.vhdx", "*.vhd" })
                {
                    foreach (var file in Directory.EnumerateFiles(storagePath, pattern, SearchOption.AllDirectories))
                    {
                        ct.ThrowIfCancellationRequested();
                        total += new FileInfo(file).Length;
                    }
                }

                return total;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not calculate wslc storage disk size for {Path}.", storagePath);
                return null;
            }
        }, ct);
    }

    /// <summary>Parses the subset of the WSLC YAML settings file the app understands.</summary>
    internal static WslcSettingsFile Parse(string text)
    {
        var values = ParseValues(text);
        values.TryGetValue("session.storagePath", out var storagePath);
        values.TryGetValue("session.cpuCount", out var cpuCount);
        values.TryGetValue("session.memorySize", out var memorySize);
        values.TryGetValue("session.maxStorageSize", out var maxStorageSize);
        values.TryGetValue("session.defaultBindingAddress", out var defaultBindingAddress);
        values.TryGetValue("credentialStore", out var credentialStore);
        return new WslcSettingsFile
        {
            StoragePath = storagePath,
            CpuCount = cpuCount,
            MemorySize = memorySize,
            MaxStorageSize = maxStorageSize,
            DefaultBindingAddress = defaultBindingAddress,
            CredentialStore = credentialStore,
            DefaultStoragePath = DefaultStoragePath,
        };
    }

    /// <summary>Rewrites or inserts the <c>sessionStoragePath</c> scalar in existing YAML text.</summary>
    internal static string SetSessionStoragePath(string text, string value)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var hadFinalNewline = text.EndsWith("\r\n", StringComparison.Ordinal) || text.EndsWith('\n');
        var lines = text.Length == 0 ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var sessionIndex = FindTopLevelKey(lines, "session");
        value = ToYamlScalar(value);
        var storageLine = $"  storagePath: {value}";
        if (sessionIndex < 0)
        {
            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add("session:");
            lines.Add(storageLine);
            return string.Join(newline, lines) + newline;
        }

        var blockEnd = FindBlockEnd(lines, sessionIndex + 1);
        for (var i = sessionIndex + 1; i < blockEnd; i++)
        {
            if (IsSessionKeyLine(lines[i], "storagePath"))
            {
                var indent = LeadingWhitespace(lines[i]);
                lines[i] = indent + "storagePath: " + value;
                return string.Join(newline, lines) + (hadFinalNewline ? newline : string.Empty);
            }
        }

        var insertAt = blockEnd;
        lines.Insert(insertAt, storageLine);
        return string.Join(newline, lines) + (hadFinalNewline ? newline : string.Empty);
    }

    private static Dictionary<string, string> ParseValues(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var inSession = false;
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                inSession = IsTopLevelKey(line, "session");
                TryAddValue(result, line, null);
                continue;
            }

            if (inSession)
            {
                TryAddValue(result, line.TrimStart(), "session");
            }
        }

        return result;
    }

    private static void TryAddValue(Dictionary<string, string> result, string line, string? prefix)
    {
        var uncommented = line.TrimStart();
        if (uncommented.StartsWith('#'))
        {
            return;
        }

        var colon = uncommented.IndexOf(':');
        if (colon <= 0)
        {
            return;
        }

        var key = uncommented[..colon].Trim();
        var value = StripComment(uncommented[(colon + 1)..]).Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        result[string.IsNullOrWhiteSpace(prefix) ? key : prefix + "." + key] = value;
    }

    /// <summary>
    /// Single-quoted YAML keeps Windows backslashes literal and makes '#', ': ' and leading
    /// indicators safe; the built-in "default" keyword stays a plain scalar.
    /// </summary>
    internal static string ToYamlScalar(string value) =>
        value == "default" ? value : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string StripComment(string value)
    {
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] is '"' or '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && value[i] == '#')
            {
                return Unquote(value[..i]);
            }
        }

        return Unquote(value);
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return value.Trim('"', '\'');
    }

    private static int FindTopLevelKey(List<string> lines, string key)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsTopLevelKey(lines[i], key))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsTopLevelKey(string line, string key) =>
        !string.IsNullOrWhiteSpace(line) &&
        !char.IsWhiteSpace(line[0]) &&
        string.Equals(line.Trim(), key + ":", StringComparison.Ordinal);

    private static int FindBlockEnd(List<string> lines, int start)
    {
        for (var i = start; i < lines.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]) && !char.IsWhiteSpace(lines[i][0]))
            {
                return i;
            }
        }

        return lines.Count;
    }

    private static bool IsSessionKeyLine(string line, string key)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..].TrimStart();
        }

        return trimmed.StartsWith(key + ":", StringComparison.Ordinal);
    }

    private static string LeadingWhitespace(string line)
    {
        var count = 0;
        while (count < line.Length && char.IsWhiteSpace(line[count]))
        {
            count++;
        }

        return line[..count];
    }
}
