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

using System.Net.Http;
using System.Security.Cryptography;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Downloads a release MSIX and decides whether it may replace the running installation. Kept free
/// of Windows package APIs so the rules can be exercised directly by tests.
/// </summary>
public static class AppUpdatePackageVerifier
{
    private const int BufferSize = 81920;

    /// <summary>
    /// Streams the release asset to <paramref name="destinationPath"/>, enforcing the size GitHub
    /// reported and, when GitHub published one, its SHA-256. Nothing is left at the destination
    /// unless every check passed.
    /// </summary>
    /// <exception cref="AppUpdateException">The download failed or did not match the release.</exception>
    public static async Task DownloadAsync(
        HttpClient http,
        AppUpdateRelease release,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var partial = destinationPath + ".partial";
        try
        {
            using var response = await http.GetAsync(release.AssetDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new AppUpdateException($"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase} for the update download.");
            }

            if (response.Content.Headers.ContentLength is { } length && length != release.AssetSize)
            {
                throw new AppUpdateException("The update download is not the size GitHub reported for the release.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > release.AssetSize)
                    {
                        throw new AppUpdateException("The update download is larger than GitHub reported for the release.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report((double)received / release.AssetSize);
                }
            }

            if (received != release.AssetSize)
            {
                throw new AppUpdateException("The update download ended before the whole package arrived.");
            }

            if (release.AssetSha256 is { } expected
                && !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expected, StringComparison.Ordinal))
            {
                throw new AppUpdateException("The update download does not match the checksum GitHub published for it.");
            }

            File.Move(partial, destinationPath, overwrite: true);
        }
        catch (HttpRequestException ex)
        {
            throw new AppUpdateException("Could not download the update. Check your network connection and try again.", ex);
        }
        catch (IOException ex)
        {
            throw new AppUpdateException("Could not save the update download to disk.", ex);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <summary>
    /// Returns null when <paramref name="candidate"/> is a newer build of the installed package, of
    /// the version the release advertises, signed by the same certificate; otherwise a user-facing
    /// reason it must not be installed.
    /// </summary>
    public static string? Verify(
        AppUpdateRelease release,
        MsixIdentity installed,
        MsixIdentity candidate,
        string? installedSignerThumbprint,
        string? candidateSignerThumbprint)
    {
        if (!string.Equals(candidate.Name, installed.Name, StringComparison.Ordinal)
            || !string.Equals(candidate.Publisher, installed.Publisher, StringComparison.Ordinal))
        {
            return "The downloaded package is not WSL Container Desktop from the same publisher.";
        }

        if (!string.Equals(candidate.Architecture, installed.Architecture, StringComparison.OrdinalIgnoreCase))
        {
            return $"The downloaded package is for {candidate.Architecture}, but this installation is {installed.Architecture}.";
        }

        if (candidate.Version != release.Version)
        {
            return $"The downloaded package is version {candidate.Version}, not the {release.Version} the release advertises.";
        }

        if (!AppUpdateReleaseParser.IsNewer(candidate.Version, installed.Version))
        {
            return $"Version {candidate.Version} is not newer than the installed {installed.Version}.";
        }

        if (string.IsNullOrEmpty(installedSignerThumbprint))
        {
            return "This installation is not a signed release, so it cannot be updated in place.";
        }

        if (string.IsNullOrEmpty(candidateSignerThumbprint))
        {
            return "The downloaded package does not carry a valid signature.";
        }

        return string.Equals(installedSignerThumbprint, candidateSignerThumbprint, StringComparison.OrdinalIgnoreCase)
            ? null
            : "The downloaded package is signed by a different certificate than this installation.";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover .partial is overwritten by the next attempt and cleared at the next launch.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}
