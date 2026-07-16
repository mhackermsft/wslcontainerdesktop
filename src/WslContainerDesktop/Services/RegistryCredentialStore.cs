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

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WslContainerDesktop.Services;

/// <summary>Reads registry login state from the store where the wslc engine keeps it.</summary>
public interface IRegistryCredentialStore
{
    /// <summary>
    /// Returns true if the engine has a stored credential for <paramref name="server"/> (i.e. the
    /// user is logged in to it), with <paramref name="username"/> set to the stored account name
    /// when available. The secret itself is never read.
    /// </summary>
    bool IsLoggedIn(string server, out string? username);

    /// <summary>
    /// Reads the stored username and secret for <paramref name="server"/> so a caller can
    /// authenticate directly to a registry's HTTP API (e.g. to browse its catalog). Returns
    /// true only when a credential exists. The secret is returned to the caller but must never
    /// be logged or persisted. Used only for generic (non-Azure) registries; Azure registries
    /// browse via the signed-in `az` session instead.
    /// </summary>
    bool TryGetCredential(string server, out string? username, out string? password);
}

/// <summary>
/// Reads the wslc engine's registry credentials from the Windows Credential Manager. wslc's default
/// credential backend is <c>wincred</c>, which stores each registry login as a Generic credential
/// named <c>wslc-credential/&lt;server&gt;</c> (e.g. <c>wslc-credential/docker.io</c>). Only the
/// presence and the stored username are read — never the secret blob — so this can drive an
/// accurate "logged in / anonymous" indicator that reflects logins made from the CLI too.
/// </summary>
public sealed class RegistryCredentialStore(ILogger<RegistryCredentialStore> logger) : IRegistryCredentialStore
{
    private const string TargetPrefix = "wslc-credential/";
    private const int CRED_TYPE_GENERIC = 1;

    public bool IsLoggedIn(string server, out string? username)
    {
        username = null;
        if (string.IsNullOrWhiteSpace(server))
        {
            return false;
        }

        foreach (var candidate in CandidateServers(server))
        {
            if (TryReadUser(TargetPrefix + candidate, out username))
            {
                return true;
            }
        }

        return false;
    }

    public bool TryGetCredential(string server, out string? username, out string? password)
    {
        username = null;
        password = null;
        if (string.IsNullOrWhiteSpace(server))
        {
            return false;
        }

        foreach (var candidate in CandidateServers(server))
        {
            if (TryReadSecret(TargetPrefix + candidate, out username, out password))
            {
                return true;
            }
        }

        return false;
    }
    /// any of Docker's canonical aliases depending on how the server was passed, so all are checked.
    /// </summary>
    private static IEnumerable<string> CandidateServers(string server)
    {
        var s = server.Trim();
        yield return s;

        if (IsDockerHub(s))
        {
            foreach (var alias in new[] { "docker.io", "index.docker.io", "registry-1.docker.io", "https://index.docker.io/v1/" })
            {
                if (!string.Equals(alias, s, StringComparison.OrdinalIgnoreCase))
                {
                    yield return alias;
                }
            }
        }
    }

    private static bool IsDockerHub(string server) =>
        server.Contains("docker.io", StringComparison.OrdinalIgnoreCase);

    private bool TryReadUser(string target, out string? username)
    {
        username = null;
        var handle = IntPtr.Zero;
        try
        {
            if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out handle))
            {
                return false;
            }

            var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
            username = string.IsNullOrWhiteSpace(cred.UserName) ? null : cred.UserName;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read credential {Target}.", target);
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CredFree(handle);
            }
        }
    }

    /// <summary>Reads the secret blob (UTF-8) alongside the username. wslc's wincred backend
    /// stores the password as UTF-8 bytes in the credential blob.</summary>
    private bool TryReadSecret(string target, out string? username, out string? password)
    {
        username = null;
        password = null;
        var handle = IntPtr.Zero;
        try
        {
            if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out handle))
            {
                return false;
            }

            var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
            username = string.IsNullOrWhiteSpace(cred.UserName) ? null : cred.UserName;

            if (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0)
            {
                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, cred.CredentialBlobSize);
                password = System.Text.Encoding.UTF8.GetString(bytes);
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read credential secret {Target}.", target);
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CredFree(handle);
            }
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr cred);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }
}
