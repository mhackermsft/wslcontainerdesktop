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
using System.Text;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed class AiCredentialStore(ILogger<AiCredentialStore> logger) : IAiCredentialStore
{
    private const string TargetPrefix = "WslContainerDesktop/ai/";
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    public bool TryReadSecret(AiProviderKind provider, out string? secret)
    {
        secret = null;
        var handle = IntPtr.Zero;
        try
        {
            if (!CredReadW(Target(provider), CRED_TYPE_GENERIC, 0, out handle))
            {
                return false;
            }

            var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize <= 0)
            {
                return true;
            }

            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, cred.CredentialBlobSize);
            secret = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read AI credential for {Provider}.", provider);
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

    public void WriteSecret(AiProviderKind provider, string secret)
    {
        if (provider == AiProviderKind.None || string.IsNullOrEmpty(secret))
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target(provider),
                CredentialBlob = blob,
                CredentialBlobSize = bytes.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName,
            };

            if (!CredWriteW(ref credential, 0))
            {
                logger.LogWarning("Failed to write AI credential for {Provider}: Win32 error {Error}.", provider, Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write AI credential for {Provider}.", provider);
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void DeleteSecret(AiProviderKind provider)
    {
        try
        {
            CredDeleteW(Target(provider), CRED_TYPE_GENERIC, 0);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to delete AI credential for {Provider}.", provider);
        }
    }

    private static string Target(AiProviderKind provider) => TargetPrefix + provider.ToString().ToLowerInvariant();

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWriteW(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDeleteW(string target, int type, int flags);

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
