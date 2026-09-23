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
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.Storage;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <inheritdoc cref="IAppUpdateService"/>
public sealed class AppUpdateService : IAppUpdateService
{
    private const string PendingMarkerName = "pending-update.json";
    private const string RestartArgument = "--updated";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    // Windows only honours RegisterApplicationRestart for a process that has run for 60 seconds, a
    // guard against restart loops. An update started straight from a cold-start toast click could
    // otherwise install without the app coming back.
    private static readonly TimeSpan MinimumUptimeForRestart = TimeSpan.FromSeconds(65);

    private readonly ILogger<AppUpdateService> _logger;

    // Dedicated client: the shared one has a 20-second timeout, far too short for a package download.
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly Lazy<PackageContext?> _package;

    public AppUpdateService(ILogger<AppUpdateService> logger)
    {
        _logger = logger;
        _package = new Lazy<PackageContext?>(ReadPackageContext);
    }

    public Version? CurrentVersion => _package.Value?.Identity.Version;

    public bool CanInstallInPlace => _package.Value?.SignerThumbprint is not null;

    public async Task<AppUpdateRelease?> CheckAsync(CancellationToken ct = default)
    {
        var package = _package.Value
            ?? throw new AppUpdateException("Updates are only available for the installed app.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{AppConstants.UpdateRepository}/releases/latest");
        request.Headers.UserAgent.ParseAdd($"WslContainerDesktop/{package.Identity.Version}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        string body;
        try
        {
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Update check: {Repository} has no published release.", AppConstants.UpdateRepository);
                return null;
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw new AppUpdateException("GitHub is limiting requests from this network right now. Try again later.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AppUpdateException($"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase} when checking for updates.");
            }

            body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AppUpdateException("GitHub did not answer the update check in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new AppUpdateException("Could not reach GitHub to check for updates.", ex);
        }

        AppUpdateRelease? release;
        try
        {
            release = AppUpdateReleaseParser.Parse(body, AppConstants.UpdateRepository, package.Identity.Architecture);
        }
        catch (JsonException ex)
        {
            throw new AppUpdateException("GitHub returned an unreadable answer to the update check.", ex);
        }

        if (release is null)
        {
            _logger.LogInformation("Update check: the latest release has no installable {Architecture} package.", package.Identity.Architecture);
            return null;
        }

        var newer = AppUpdateReleaseParser.IsNewer(release.Version, package.Identity.Version);
        _logger.LogInformation(
            "Update check: installed {Installed}, latest {Latest} ({Result}).",
            package.Identity.Version, release.Version, newer ? "update available" : "up to date");
        return newer ? release : null;
    }

    public async Task InstallAsync(AppUpdateRelease release, IProgress<AppUpdateProgress>? progress, Action? beforeInstall, CancellationToken ct = default)
    {
        // No ConfigureAwait(false) here: beforeInstall and everything after the download must run on
        // the caller's (UI) context, because preparing for shutdown touches view-model state.
        var package = _package.Value
            ?? throw new AppUpdateException("Updates are only available for the installed app.");
        if (package.SignerThumbprint is null)
        {
            throw new AppUpdateException("This copy is not a signed release install, so it cannot update itself. Install the release from GitHub instead.");
        }

        var folder = UpdatesFolder();
        var path = Path.Combine(folder, release.AssetName);

        progress?.Report(new AppUpdateProgress(AppUpdatePhase.Downloading, 0));

        // Windows only relaunches a process that has run for about a minute. Wait for that alongside
        // the download rather than after the checks, so the verified package is installed at once and
        // never sits on disk between verification and installation.
        var uptimeWait = EnsureRestartableUptimeAsync(ct);
        var lastPercent = 0;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(DownloadTimeout);
            try
            {
                Directory.CreateDirectory(folder);
                await AppUpdatePackageVerifier.DownloadAsync(
                    _http,
                    release,
                    path,
                    new InlineProgress(f =>
                    {
                        // One UI update per percent is plenty; the download reports every buffer.
                        var percent = (int)(f * 100);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress?.Report(new AppUpdateProgress(AppUpdatePhase.Downloading, f));
                        }
                    }),
                    timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AppUpdateException("The update download took too long and was stopped.");
            }
        }

        _logger.LogInformation("Downloaded update {Version} to {Path}.", release.Version, path);

        if (!uptimeWait.IsCompleted)
        {
            progress?.Report(new AppUpdateProgress(AppUpdatePhase.Preparing));
        }

        await uptimeWait;
        if (ct.IsCancellationRequested)
        {
            TryDelete(path);
            ct.ThrowIfCancellationRequested();
        }

        progress?.Report(new AppUpdateProgress(AppUpdatePhase.Verifying));

        string? reason;
        try
        {
            var candidate = MsixPackageInspector.ReadIdentity(path);
            var signature = MsixPackageInspector.ReadPackageSignature(path);
            using var signer = signature is null ? null : MsixPackageInspector.GetVerifiedSigner(signature);
            reason = AppUpdatePackageVerifier.Verify(
                release,
                package.Identity,
                candidate,
                package.SignerThumbprint,
                signer is null ? null : MsixPackageInspector.Thumbprint(signer));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            reason = "The downloaded file is not a readable app package.";
            _logger.LogWarning(ex, "Could not inspect downloaded update {Path}.", path);
        }

        if (reason is not null)
        {
            TryDelete(path);
            _logger.LogWarning("Refused update {Version}: {Reason}", release.Version, reason);
            throw new AppUpdateException(reason);
        }

        progress?.Report(new AppUpdateProgress(AppUpdatePhase.Installing));
        ct.ThrowIfCancellationRequested();

        WritePendingMarker(folder, release.Version, package.Identity.Version);
        var registered = NativeMethods.RegisterApplicationRestart(RestartArgument, NativeMethods.RESTART_NO_CRASH | NativeMethods.RESTART_NO_HANG | NativeMethods.RESTART_NO_REBOOT);
        if (registered != 0)
        {
            _logger.LogWarning("RegisterApplicationRestart failed with 0x{HResult:X8}; the app will not reopen by itself.", registered);
        }

        try
        {
            beforeInstall?.Invoke();
        }
        catch (Exception ex)
        {
            // Preparing for shutdown is best effort; Windows closes the app regardless.
            _logger.LogWarning(ex, "Pre-install shutdown preparation failed.");
        }

        _logger.LogInformation("Installing update {Version}; Windows will close and relaunch the app.", release.Version);

        // From here Windows closes this process as part of the deployment. Reaching the code after
        // the await means the package manager refused the update.
        var operation = new PackageManager().AddPackageAsync(new Uri(path), null, DeploymentOptions.ForceApplicationShutdown);
        DeploymentResult? result = null;
        Exception? failure = null;
        try
        {
            result = await operation;
        }
        catch (Exception ex)
        {
            failure = ex;
            try
            {
                result = operation.GetResults();
            }
            catch (Exception resultsEx)
            {
                _logger.LogDebug(resultsEx, "No deployment result was available after the failure.");
            }
        }

        if (failure is null && result?.ExtendedErrorCode is null)
        {
            // Installed but the app was not closed (rare); the new version runs on the next launch.
            _logger.LogInformation("Update {Version} installed without closing the app.", release.Version);
            return;
        }

        NativeMethods.UnregisterApplicationRestart();
        TryDelete(Path.Combine(folder, PendingMarkerName));
        TryDelete(path);

        var detail = !string.IsNullOrWhiteSpace(result?.ErrorText) ? result!.ErrorText.Trim() : failure?.Message;
        _logger.LogError(failure ?? result?.ExtendedErrorCode, "Windows could not install update {Version}: {Detail}", release.Version, detail);
        throw new AppUpdateException($"Windows could not install the update. {detail}".Trim());
    }

    public AppUpdateOutcome? CompleteLaunch()
    {
        var package = _package.Value;
        if (package is null)
        {
            return null;
        }

        string folder;
        try
        {
            folder = UpdatesFolder();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No local cache folder; nothing to clean up after an update.");
            return null;
        }

        AppUpdateOutcome? outcome = null;
        var marker = Path.Combine(folder, PendingMarkerName);
        try
        {
            if (File.Exists(marker))
            {
                var pending = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(marker));
                if (pending is not null && Version.TryParse(pending.TargetVersion, out var target))
                {
                    var succeeded = !AppUpdateReleaseParser.IsNewer(target, package.Identity.Version);
                    outcome = new AppUpdateOutcome(succeeded, target, package.Identity.Version);
                    _logger.LogInformation(
                        "Previous session installed update {Target}: {Result} (running {Current}).",
                        target, succeeded ? "succeeded" : "did not complete", package.Identity.Version);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the pending update marker.");
        }

        try
        {
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not clean up the updates folder.");
        }

        return outcome;
    }

    /// <summary>The package manager runs outside the app, so it must get the real (unredirected) path.</summary>
    private static string UpdatesFolder() => Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "Updates");

    /// <summary>Completes once the process is old enough to be relaunched; never faults, so it can run unobserved.</summary>
    private static async Task EnsureRestartableUptimeAsync(CancellationToken ct)
    {
        using var process = Process.GetCurrentProcess();
        var uptime = DateTime.Now - process.StartTime;
        if (uptime < MinimumUptimeForRestart)
        {
            try
            {
                await Task.Delay(MinimumUptimeForRestart - uptime, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The caller checks its token after awaiting.
            }
        }
    }

    private void WritePendingMarker(string folder, Version target, Version from)
    {
        try
        {
            var json = JsonSerializer.Serialize(new PendingUpdate { TargetVersion = target.ToString(), FromVersion = from.ToString() });
            File.WriteAllText(Path.Combine(folder, PendingMarkerName), json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the next launch's "updated to" message depends on the marker.
            _logger.LogWarning(ex, "Could not record the pending update.");
        }
    }

    private PackageContext? ReadPackageContext()
    {
        Package package;
        try
        {
            package = Package.Current;
        }
        catch (Exception ex)
        {
            // Package.Current throws (APPMODEL_ERROR_NO_PACKAGE) when the process has no identity.
            _logger.LogInformation(ex, "Running without package identity; in-app updates are unavailable.");
            return null;
        }

        var id = package.Id;
        var identity = new MsixIdentity(
            id.Name,
            id.Publisher,
            new Version(id.Version.Major, id.Version.Minor, id.Version.Build, id.Version.Revision),
            ArchitectureName(id.Architecture));

        string? thumbprint = null;
        try
        {
            var signature = MsixPackageInspector.ReadInstalledSignature(package.InstalledLocation.Path);
            using var signer = signature is null ? null : MsixPackageInspector.GetVerifiedSigner(signature);
            thumbprint = signer is null ? null : MsixPackageInspector.Thumbprint(signer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the installed package signature.");
        }

        if (thumbprint is null)
        {
            _logger.LogInformation("Installed package is not signed (development registration); updates can be found but not installed in place.");
        }

        return new PackageContext(identity, thumbprint);
    }

    // Matches the manifest's ProcessorArchitecture spelling ("x64", "arm64", "neutral", ...).
    private static string ArchitectureName(Windows.System.ProcessorArchitecture architecture) =>
        architecture.ToString().ToLowerInvariant();

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Path}.", path);
        }
    }

    private sealed record PackageContext(MsixIdentity Identity, string? SignerThumbprint);

    private sealed class PendingUpdate
    {
        public string? TargetVersion { get; set; }
        public string? FromVersion { get; set; }
    }

    /// <summary>Reports synchronously; the caller's <see cref="Progress{T}"/> marshals to the UI.</summary>
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
