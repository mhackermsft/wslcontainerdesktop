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

using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Whether registry logout completed or still requires attention.</summary>
public enum RegistryLogoutStatus
{
    /// <summary>The engine removed the login and no checked credentials remain.</summary>
    Completed,
    /// <summary>The engine reported a failure other than an absent login.</summary>
    Failed,
    /// <summary>The engine finished, but matching Windows credentials remain.</summary>
    CredentialsRemain,
    /// <summary>The app could not confirm the credential backend or its remaining login metadata.</summary>
    CredentialStateUnknown,
}

/// <summary>Logout outcome, retaining a failed engine result when available.</summary>
public sealed record RegistryLogoutResult(RegistryLogoutStatus Status, CommandResult? Error = null);

/// <summary>Runs registry logout and verifies the resulting login state for both UI entry points.</summary>
public sealed class RegistryLogoutService(
    IWslcService wslc,
    IRegistryCredentialStore credentials,
    IWslcSettingsFileService settings)
{
    /// <summary>Logs out of the requested registry, cleaning both independent Docker Hub engine keys.</summary>
    public async Task<RegistryLogoutResult> LogoutAsync(string server, CancellationToken ct = default,
        IWslcService? sessionClient = null, Func<CancellationToken, Task>? verifyTarget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ct.ThrowIfCancellationRequested();
        string? backend;
        try
        {
            backend = (await settings.ReadAsync(ct).ConfigureAwait(false)).CredentialStore?.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Commands can still remove the login, but an unreadable configuration cannot
            // establish whether Windows credential metadata reflects the active backend.
            backend = "unknown";
        }

        var target = server.Trim();
        IEnumerable<string> targets = RegistryServerAddress.IsDockerHub(target)
            ? new[] { target, "docker.io", "registry-1.docker.io" }.Distinct(StringComparer.Ordinal)
            : [target];
        foreach (var logoutTarget in targets)
        {
            if (verifyTarget is not null) await verifyTarget(ct).ConfigureAwait(false);
            var result = await (sessionClient ?? wslc).LogoutRegistryAsync(logoutTarget, ct).ConfigureAwait(false);
            if (!result.Success && !CommandErrorText.IsNotLoggedIn(result))
            {
                return new(RegistryLogoutStatus.Failed, result);
            }
        }

        if (verifyTarget is not null) await verifyTarget(ct).ConfigureAwait(false);
        if (string.Equals(backend, "file", StringComparison.OrdinalIgnoreCase))
        {
            // The engine owns file-backend credentials; old Windows entries are unrelated.
            return new(RegistryLogoutStatus.Completed);
        }

        if (!string.IsNullOrWhiteSpace(backend) &&
            !string.Equals(backend, "default", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(backend, "wincred", StringComparison.OrdinalIgnoreCase))
        {
            return new(RegistryLogoutStatus.CredentialStateUnknown);
        }

        return new(credentials.GetLoginStatus(target, out _) switch
        {
            RegistryCredentialStatus.Missing => RegistryLogoutStatus.Completed,
            RegistryCredentialStatus.Present => RegistryLogoutStatus.CredentialsRemain,
            _ => RegistryLogoutStatus.CredentialStateUnknown,
        });
    }
}
