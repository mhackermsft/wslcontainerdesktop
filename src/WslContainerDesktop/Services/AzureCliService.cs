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

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Invokes the Azure CLI (`az`). All calls fail soft: if `az` is not installed the methods
/// report unavailability rather than throwing, so the UI can guide the user to install it.
/// </summary>
public sealed class AzureCliService : IAzureCliService
{
    private const int DefaultTimeoutSeconds = 90;
    private const int InteractiveLoginTimeoutSeconds = 300;

    private readonly ILogger<AzureCliService> _logger;
    private string? _resolvedPath;
    private bool _resolved;

    public AzureCliService(ILogger<AzureCliService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        await ResolveAzPathAsync(ct).ConfigureAwait(false) is not null;

    public async Task<string?> GetSignedInUserAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(new[] { "account", "show", "--query", "user.name", "-o", "tsv" }, ct)
            .ConfigureAwait(false);
        if (result is null || !result.Success)
        {
            return null;
        }

        var name = result.StandardOutput.Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public async Task<CommandResult> LoginAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(new[] { "login", "--only-show-errors", "-o", "none" }, ct, timeoutSeconds: InteractiveLoginTimeoutSeconds)
            .ConfigureAwait(false);
        return result ?? new CommandResult { ExitCode = -1, StandardError = "Azure CLI is not installed." };
    }

    public async Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(
            new[] { "account", "list", "--all", "--query", "[].{Id:id,Name:name,IsDefault:isDefault}", "-o", "json" }, ct)
            .ConfigureAwait(false);
        if (result is null || !result.Success)
        {
            return Array.Empty<AzureSubscription>();
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            var list = new List<AzureSubscription>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                list.Add(new AzureSubscription
                {
                    Id = el.TryGetProperty("Id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                    Name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                    IsDefault = el.TryGetProperty("IsDefault", out var d) && d.ValueKind == JsonValueKind.True,
                });
            }

            return list.OrderByDescending(s => s.IsDefault).ThenBy(s => s.Name).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse `az account list` output.");
            return Array.Empty<AzureSubscription>();
        }
    }

    public async Task<IReadOnlyList<AzureRegistry>> ListRegistriesAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = await RunAsync(
            new[]
            {
                "acr", "list", "--subscription", subscriptionId,
                "--query", "[].{Name:name,LoginServer:loginServer,ResourceGroup:resourceGroup,AdminEnabled:adminUserEnabled}",
                "-o", "json",
            }, ct)
            .ConfigureAwait(false);
        if (result is null || !result.Success)
        {
            return Array.Empty<AzureRegistry>();
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            var list = new List<AzureRegistry>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                list.Add(new AzureRegistry
                {
                    Name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                    LoginServer = el.TryGetProperty("LoginServer", out var ls) ? ls.GetString() ?? string.Empty : string.Empty,
                    ResourceGroup = el.TryGetProperty("ResourceGroup", out var rg) ? rg.GetString() ?? string.Empty : string.Empty,
                    AdminEnabled = el.TryGetProperty("AdminEnabled", out var a) && a.ValueKind == JsonValueKind.True,
                });
            }

            return list.OrderBy(r => r.Name).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse `az acr list` output for subscription {SubscriptionId}.", subscriptionId);
            return Array.Empty<AzureRegistry>();
        }
    }

    public async Task<(string LoginServer, string Token)?> GetAcrTokenAsync(string acrName, string subscriptionId, CancellationToken ct = default)
    {
        var result = await RunAsync(
            new[] { "acr", "login", "--name", acrName, "--subscription", subscriptionId, "--expose-token", "-o", "json" }, ct)
            .ConfigureAwait(false);
        if (result is null || !result.Success)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            var server = doc.RootElement.TryGetProperty("loginServer", out var ls) ? ls.GetString() : null;
            var token = doc.RootElement.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            return (server!, token!);
        }
        catch (Exception ex)
        {
            // Note: never log the response body here — it contains the ACR access token.
            _logger.LogWarning(ex, "Failed to parse `az acr login --expose-token` output for registry {AcrName}.", acrName);
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ListAcrRepositoriesAsync(string acrName, string subscriptionId, CancellationToken ct = default)
    {
        var result = await RunAsync(
            new[] { "acr", "repository", "list", "--name", acrName, "--subscription", subscriptionId, "-o", "json" }, ct)
            .ConfigureAwait(false);
        return ParseStringArray(result, $"`az acr repository list` for registry {acrName}");
    }

    public async Task<IReadOnlyList<string>> ListAcrTagsAsync(string acrName, string repository, string subscriptionId, CancellationToken ct = default)
    {
        var result = await RunAsync(
            new[]
            {
                "acr", "repository", "show-tags", "--name", acrName, "--repository", repository,
                "--subscription", subscriptionId, "--orderby", "time_desc", "-o", "json",
            }, ct)
            .ConfigureAwait(false);
        return ParseStringArray(result, $"`az acr repository show-tags` for {acrName}/{repository}");
    }

    /// <summary>Parses an `az ... -o json` response that is a flat array of strings.</summary>
    private IReadOnlyList<string> ParseStringArray(CommandResult? result, string context)
    {
        if (result is null || !result.Success)
        {
            return Array.Empty<string>();
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var list = new List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        list.Add(s);
                    }
                }
            }

            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse {Context} output.", context);
            return Array.Empty<string>();
        }
    }

    // ---- az resolution + process plumbing -------------------------------

    private async Task<string?> ResolveAzPathAsync(CancellationToken ct)
    {
        if (_resolved)
        {
            return _resolvedPath;
        }

        // 1) Ask the OS where az lives (handles PATH-only installs). `where` may return
        //    several lines (az, az.cmd, az.bat); prefer a .cmd since that's what runs.
        var located = await WhereAsync(ct).ConfigureAwait(false);
        foreach (var path in located)
        {
            if (await CanRunAsync(path, ct).ConfigureAwait(false))
            {
                _resolvedPath = path;
                _resolved = true;
                return _resolvedPath;
            }
        }

        // 2) Fall back to well-known install locations.
        var candidates = new[]
        {
            @"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd",
            @"C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\wbin\az.cmd",
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && await CanRunAsync(candidate, ct).ConfigureAwait(false))
            {
                _resolvedPath = candidate;
                _resolved = true;
                return _resolvedPath;
            }
        }

        _resolved = true;
        _resolvedPath = null;
        return null;
    }

    /// <summary>Uses `where.exe` to locate az on PATH; returns candidate full paths (.cmd first).</summary>
    private static async Task<IReadOnlyList<string>> WhereAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("az");

            using var p = new Process { StartInfo = psi };
            if (!p.Start())
            {
                return Array.Empty<string>();
            }

            var outputTask = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);

            var paths = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(File.Exists)
                .ToList();

            // Prefer a .cmd (the runnable wrapper) over the extensionless python entry point.
            return paths
                .OrderByDescending(p2 => p2.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static async Task<bool> CanRunAsync(string path, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("version");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("none");

            using var p = new Process { StartInfo = psi };
            if (!p.Start())
            {
                return false;
            }

            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<CommandResult?> RunAsync(IEnumerable<string> args, CancellationToken ct, int timeoutSeconds = DefaultTimeoutSeconds)
    {
        var az = await ResolveAzPathAsync(ct).ConfigureAwait(false);
        if (az is null)
        {
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = az,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return await ProcessExecutor.RunAsync(
            psi,
            timeout: TimeSpan.FromSeconds(timeoutSeconds),
            launchErrorContext: "Could not launch az.",
            ct: ct).ConfigureAwait(false);
    }
}
