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

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

public partial class ContainersViewModel : ObservableObject, IDisposable
{
    private const int MaxInlinePreviewBytes = 65_536;
    private readonly IWslcService _wslc;
    private readonly StatusMonitor _monitor;
    private readonly HealthWatchdog _watchdog;
    private readonly DialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly RegistryAuthRefresher _authRefresher;
    private readonly IRunProfileStore _profiles;
    private readonly ILogger<ContainersViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly LogStreamer _logStreamer;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _showAll = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ContainerRowViewModel? _selected;

    // ---- Detail fields (populated from inspect on selection) ----
    [ObservableProperty]
    private string _detailCommand = "-";

    [ObservableProperty]
    private string _detailIp = "-";

    [ObservableProperty]
    private string _detailStarted = "-";

    [ObservableProperty]
    private string _detailNetwork = "-";

    [ObservableProperty]
    private string _detailWorkingDir = "-";

    [ObservableProperty]
    private string _detailInspectJson = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNavigateUp))]
    private string _filesCurrentPath = "/";

    [ObservableProperty]
    private string _filesStatusMessage = "Open the Files tab to browse the container filesystem.";

    [ObservableProperty]
    private bool _isFilesBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    private ContainerFileEntry? _selectedFile;

    // ---- Live stats (Stats tab) ----
    [ObservableProperty]
    private string _statCpu = "-";

    [ObservableProperty]
    private double _statCpuValue;

    [ObservableProperty]
    private string _statMem = "-";

    [ObservableProperty]
    private double _statMemValue;

    [ObservableProperty]
    private string _statMemUsage = "-";

    [ObservableProperty]
    private string _statNetIO = "-";

    [ObservableProperty]
    private string _statBlockIO = "-";

    [ObservableProperty]
    private int _statPids;

    private bool _hasAttemptedFilesLoad;

    // Staging task for drag-out: when a file is selected, a background copy to a temp folder begins.
    private Task<string?>? _dragStagingTask;
    private ContainerFileEntry? _dragStagingEntry;

    public ObservableCollection<string> DetailEnvironment { get; } = new();

    public ObservableCollection<string> DetailMounts { get; } = new();

    public ObservableCollection<ContainerFileEntry> DetailFiles { get; } = new();

    public bool HasSelection => Selected is not null;

    public bool HasSelectedFile => SelectedFile is not null;

    public bool CanNavigateUp => FilesCurrentPath != "/";

    /// <summary>Returns the in-progress or completed temp-file staging task for the currently selected file entry.</summary>
    public Task<string?>? DragStagingTask => _dragStagingTask;

    /// <summary>Raised (on the UI thread) for each streamed log line of the selected container.</summary>
    public event Action<string>? LogLineReceived;

    /// <summary>Raised when the log view should be cleared (selection changed).</summary>
    public event Action? LogCleared;

    /// <summary>Raised when the current selection was removed (detail page should go back).</summary>
    public event Action? SelectionCleared;

    public ObservableCollection<ContainerRowViewModel> Containers { get; } = new();

    public ContainersViewModel(IWslcService wslc, StatusMonitor monitor, HealthWatchdog watchdog, DialogService dialogs, ISettingsService settings, RegistryAuthRefresher authRefresher, IRunProfileStore profiles, ILogger<ContainersViewModel> logger)
    {
        _wslc = wslc;
        _monitor = monitor;
        _watchdog = watchdog;
        _dialogs = dialogs;
        _settings = settings;
        _authRefresher = authRefresher;
        _profiles = profiles;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _logStreamer = new LogStreamer(settings, _dispatcher);
        _logStreamer.LineReceived += line => LogLineReceived?.Invoke(line);

        _monitor.StatusChanged += OnStatusChanged;
        _watchdog.HealthChanged += OnHealthChanged;

        if (_monitor.Latest is not null)
        {
            Reconcile(_monitor.Latest.Containers);
        }
    }

    partial void OnShowAllChanged(bool value) => RequestRefresh();

    partial void OnSelectedChanged(ContainerRowViewModel? value)
    {
        // Reset detail + logs for the new selection.
        _logStreamer.Stop();
        LogCleared?.Invoke();
        DetailEnvironment.Clear();
        DetailMounts.Clear();
        DetailCommand = "-";
        DetailIp = "-";
        DetailStarted = "-";
        DetailNetwork = "-";
        DetailWorkingDir = "-";
        DetailInspectJson = string.Empty;
        ResetFilesState(value);

        if (value is null)
        {
            return;
        }

        _logStreamer.Start(value.Id);
        _ = LoadDetailsAsync(value.Id);
    }

    partial void OnSelectedFileChanged(ContainerFileEntry? value)
    {
        // When a non-directory file is selected, pre-copy it to a temp location so it can be
        // dragged out to Windows Explorer with minimal delay.
        _dragStagingTask = null;
        _dragStagingEntry = null;

        if (value is { IsDirectory: false } && Selected is { } container)
        {
            _dragStagingEntry = value;
            _dragStagingTask = StageDragFileAsync(container.Id, value);
        }
    }

    private async Task LoadDetailsAsync(string id)
    {
        try
        {
            var result = await _wslc.InspectContainerAsync(id);
            if (!result.Success)
            {
                return;
            }

            // Guard against a selection change while awaiting.
            if (Selected?.Id != id)
            {
                return;
            }

            var details = ContainerDetails.Parse(result.StandardOutput);
            DetailCommand = string.IsNullOrWhiteSpace(details.Command) ? "-" : details.Command;
            DetailIp = details.IpAddress;
            DetailStarted = details.StartedAt;
            DetailNetwork = details.NetworkMode;
            DetailWorkingDir = details.WorkingDir;
            DetailInspectJson = result.StandardOutput.Trim();

            DetailEnvironment.Clear();
            foreach (var e in details.Environment)
            {
                DetailEnvironment.Add(e);
            }

            DetailMounts.Clear();
            foreach (var m in details.Mounts)
            {
                DetailMounts.Add(m);
            }
        }
        catch (Exception ex)
        {
            // ignore inspect failures for detail panel
            _logger.LogDebug(ex, "Container inspect for the detail panel failed.");
        }
    }

    public Task EnsureFilesLoadedAsync()
    {
        if (_hasAttemptedFilesLoad || Selected is null)
        {
            return Task.CompletedTask;
        }

        return LoadFilesAsync(FilesCurrentPath);
    }

    public Task RefreshFilesAsync() => LoadFilesAsync(FilesCurrentPath);

    public Task NavigateUpAsync()
    {
        if (!CanNavigateUp)
        {
            return Task.CompletedTask;
        }

        return LoadFilesAsync(GetParentPath(FilesCurrentPath));
    }

    public async Task OpenFileEntryAsync(ContainerFileEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            await LoadFilesAsync(entry.Path);
            return;
        }

        await OpenFileAsync(entry);
    }

    /// <summary>Copies the file to a temp location and opens it read-only with the OS default handler.</summary>
    public async Task OpenFileAsync(ContainerFileEntry? entry = null)
    {
        entry ??= SelectedFile;
        if (entry is null || entry.IsDirectory || Selected is null)
        {
            return;
        }

        IsFilesBusy = true;
        FilesStatusMessage = $"Opening {entry.Name}…";
        try
        {
            var tempDir = GetTempDir(Selected.Id);
            var result = await _wslc.CopyFromContainerAsync(Selected.Id, entry.Path, tempDir);
            if (!result.Success)
            {
                FilesStatusMessage = DescribeFileCommandFailure(result, "Could not open this file.");
                return;
            }

            var localPath = Path.Combine(tempDir, entry.Name);
            if (!File.Exists(localPath))
            {
                FilesStatusMessage = "File was not found after copy.";
                return;
            }

            // Mark read-only to discourage edits that can't be written back.
            try
            {
                new FileInfo(localPath).IsReadOnly = true;
            }
            catch
            {
                // Ignore; read-only is advisory.
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = localPath,
                UseShellExecute = true,
            });

            FilesStatusMessage = $"Opened {entry.Name}";
        }
        catch (Exception ex)
        {
            FilesStatusMessage = ex.Message;
        }
        finally
        {
            IsFilesBusy = false;
        }
    }

    /// <summary>Copies the selected entry's container path to the Windows clipboard.</summary>
    public void CopyPathToClipboard(ContainerFileEntry? entry = null)
    {
        entry ??= SelectedFile;
        if (entry is null)
        {
            return;
        }

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(entry.Path);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        FilesStatusMessage = $"Copied path: {entry.Path}";
    }

    /// <summary>Prompts for a new name and renames the selected entry.</summary>
    public async Task RenameAsync(ContainerFileEntry? entry = null)
    {
        entry ??= SelectedFile;
        if (entry is null || Selected is null)
        {
            return;
        }

        var dialog = new Dialogs.SimpleInputDialog("Rename", "New name", entry.Name)
        {
            Value = entry.Name,
        };
        var dialogResult = await _dialogs.ShowDialogAsync(dialog);
        if (dialogResult != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            return;
        }

        var newName = dialog.Value.Trim();
        if (string.IsNullOrEmpty(newName) || newName == entry.Name)
        {
            return;
        }

        var newPath = CombineContainerPath(FilesCurrentPath, newName);
        await ExecuteAsync($"Renaming {entry.Name}…",
            () => _wslc.RenamePathAsync(Selected.Id, entry.Path, newPath));

        await LoadFilesAsync(FilesCurrentPath);
    }

    /// <summary>Prompts for a folder name and creates a new directory in the current path.</summary>
    public async Task CreateFolderAsync()
    {
        if (Selected is null)
        {
            return;
        }

        var dialog = new Dialogs.SimpleInputDialog("New folder", "Folder name", "new-folder");
        var dialogResult = await _dialogs.ShowDialogAsync(dialog);
        if (dialogResult != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            return;
        }

        var name = dialog.Value.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var newPath = CombineContainerPath(FilesCurrentPath, name);
        await ExecuteAsync($"Creating folder {name}…",
            () => _wslc.CreateDirectoryAsync(Selected.Id, newPath));

        if (Selected.IsRunning)
        {
            await LoadFilesAsync(FilesCurrentPath);
        }
    }

    public async Task CopyIntoCurrentDirectoryAsync(string hostPath)
    {
        if (Selected is null || string.IsNullOrWhiteSpace(hostPath))
        {
            return;
        }

        await ExecuteAsync($"Copying {Path.GetFileName(hostPath)} into {Selected.Name}…",
            () => _wslc.CopyToContainerAsync(Selected.Id, hostPath, FilesCurrentPath));

        if (Selected.IsRunning)
        {
            await LoadFilesAsync(FilesCurrentPath);
        }
    }

    /// <summary>Copies multiple host paths into the current container directory (used for multi-file drag-in).</summary>
    public async Task CopyMultipleIntoCurrentDirectoryAsync(IEnumerable<string> hostPaths)
    {
        if (Selected is null)
        {
            return;
        }

        var paths = hostPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        IsFilesBusy = true;
        FilesStatusMessage = $"Uploading {paths.Count} item(s) to {Selected.Name}…";
        var failed = new List<string>();
        try
        {
            foreach (var path in paths)
            {
                var result = await _wslc.CopyToContainerAsync(Selected.Id, path, FilesCurrentPath);
                if (!result.Success)
                {
                    failed.Add(Path.GetFileName(path));
                }
            }

            if (failed.Count > 0)
            {
                await _dialogs.ShowMessageAsync("Upload incomplete",
                    $"The following items could not be copied:\n{string.Join('\n', failed)}");
            }
        }
        finally
        {
            IsFilesBusy = false;
        }

        if (Selected.IsRunning)
        {
            await LoadFilesAsync(FilesCurrentPath);
        }
    }

    public async Task CopySelectedFileOutAsync(string hostDirectory)
    {
        if (Selected is null || SelectedFile is null || string.IsNullOrWhiteSpace(hostDirectory))
        {
            return;
        }

        await ExecuteAsync($"Copying {SelectedFile.Name} to the host…",
            () => _wslc.CopyFromContainerAsync(Selected.Id, SelectedFile.Path, hostDirectory));
    }

    public async Task DeleteSelectedFileAsync()
    {
        if (Selected is null || SelectedFile is null)
        {
            return;
        }

        var kind = SelectedFile.IsDirectory ? "folder" : "file";
        var ok = await _dialogs.ShowConfirmAsync(
            "Delete from container",
            $"Delete {kind} \"{SelectedFile.Name}\" from {Selected.Name}? This cannot be undone.",
            "Delete");
        if (!ok)
        {
            return;
        }

        await ExecuteAsync($"Deleting {SelectedFile.Name}…",
            () => _wslc.DeletePathAsync(Selected.Id, SelectedFile.Path));

        if (Selected.IsRunning)
        {
            await LoadFilesAsync(FilesCurrentPath);
        }
    }

    private async Task LoadFilesAsync(string path)
    {
        var selected = Selected;
        if (selected is null)
        {
            return;
        }

        _hasAttemptedFilesLoad = true;
        var requestedPath = NormalizeContainerPath(path);

        if (!selected.IsRunning)
        {
            DetailFiles.Clear();
            SelectedFile = null;
            FilesCurrentPath = requestedPath;
            FilesStatusMessage = "Files can be browsed while the container is running. Copy in/out may still work for stopped containers.";
            return;
        }

        IsFilesBusy = true;
        // Set FilesCurrentPath before the await so that a subsequent navigation to a
        // different path can be detected as a stale response in the post-await guard below.
        FilesCurrentPath = requestedPath;
        FilesStatusMessage = $"Loading {requestedPath}…";
        try
        {
            var result = await _wslc.ListFilesAsync(selected.Id, requestedPath);

            // Guard against stale responses: verify both the container and the path still match.
            if (Selected?.Id != selected.Id || FilesCurrentPath != requestedPath)
            {
                return;
            }

            if (!result.Success)
            {
                DetailFiles.Clear();
                SelectedFile = null;
                FilesStatusMessage = DescribeFileCommandFailure(result, "Could not list this directory.");
                return;
            }

            var (currentPath, entries) = ContainerFileEntry.ParseListing(result.StandardOutput, requestedPath);
            DetailFiles.Clear();
            foreach (var entry in entries)
            {
                DetailFiles.Add(entry);
            }

            FilesCurrentPath = currentPath;
            SelectedFile = null;
            FilesStatusMessage = entries.Count == 0
                ? $"No files in {currentPath}"
                : $"{entries.Count} item(s) in {currentPath}";
        }
        catch (Exception ex)
        {
            FilesStatusMessage = ex.Message;
            DetailFiles.Clear();
            SelectedFile = null;
        }
        finally
        {
            IsFilesBusy = false;
        }
    }

    private void ResetFilesState(ContainerRowViewModel? selected)
    {
        _hasAttemptedFilesLoad = false;
        _dragStagingTask = null;
        _dragStagingEntry = null;
        DetailFiles.Clear();
        SelectedFile = null;
        FilesCurrentPath = "/";
        FilesStatusMessage = selected?.IsRunning == true
            ? "Open the Files tab to browse the container filesystem."
            : "Files can be browsed while the container is running. Copy in/out may still work for stopped containers.";
    }

    /// <summary>Background-copies the selected file to a temp folder for drag-out support.</summary>
    private async Task<string?> StageDragFileAsync(string containerId, ContainerFileEntry entry)
    {
        try
        {
            var tempDir = GetTempDir(containerId);
            var result = await _wslc.CopyFromContainerAsync(containerId, entry.Path, tempDir)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                return null;
            }

            var localPath = Path.Combine(tempDir, entry.Name);
            return File.Exists(localPath) ? localPath : null;
        }
        catch
        {
            return null;
        }
    }

    // Matches the "short ID" length displayed elsewhere in the UI and provides
    // sufficient uniqueness for temp-directory names across containers on the same host.
    private const int ShortIdLength = 12;

    private const string TempRootFolderName = "WslContainerDesktop";

    /// <summary>Root of the temp tree used to stage files opened/downloaded from containers.</summary>
    private static string TempRoot => Path.Combine(Path.GetTempPath(), TempRootFolderName);

    private static string GetTempDir(string containerId)
    {
        // Use the first ShortIdLength hex characters of the container ID as the subfolder name.
        var dir = Path.Combine(TempRoot, containerId.Length >= ShortIdLength ? containerId[..ShortIdLength] : containerId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Best-effort deletion of the temp tree used to stage files opened/downloaded from containers.
    /// Files staged for a read-only "open" are marked read-only, so their attributes are cleared
    /// before deletion. Run at startup (rather than only on Dispose) so files left behind by a
    /// crash or forced exit are still reclaimed.
    /// </summary>
    public static void ClearTempFiles(ILogger? logger = null)
    {
        try
        {
            if (!Directory.Exists(TempRoot))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(TempRoot, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // A locked/open file can't be reset; the delete below will skip it and we retry next launch.
                }
            }

            Directory.Delete(TempRoot, recursive: true);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to clear staged container temp files at {Path}.", TempRoot);
        }
    }

    private static string NormalizeContainerPath(string path) =>
        string.IsNullOrEmpty(path) ? "/" : path;

    private static string GetParentPath(string path)
    {
        var normalized = NormalizeContainerPath(path).TrimEnd('/');
        if (string.IsNullOrEmpty(normalized) || normalized == "/")
        {
            return "/";
        }

        var slash = normalized.LastIndexOf('/');
        return slash <= 0 ? "/" : normalized[..slash];
    }

    private static string CombineContainerPath(string directory, string name)
    {
        var dir = string.IsNullOrEmpty(directory) ? "/" : directory;
        return dir == "/" ? "/" + name : dir.TrimEnd('/') + "/" + name;
    }

    private static string DescribeFileCommandFailure(CommandResult result, string fallback)
    {
        var combined = (result.StandardError + "\n" + result.StandardOutput).Trim();
        if (combined.Contains("__WSLCD_NOT_DIR__", StringComparison.Ordinal))
        {
            return "The selected path is not a directory.";
        }

        if (combined.Contains("__WSLCD_NOT_FILE__", StringComparison.Ordinal))
        {
            return "The selected path is not a regular file.";
        }

        if (combined.Contains("__WSLCD_TOO_LARGE__", StringComparison.Ordinal))
        {
            return $"Preview is limited to files up to {MaxInlinePreviewBytes / 1024} KiB.";
        }

        if (combined.Contains("not running", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("stopped", StringComparison.OrdinalIgnoreCase))
        {
            return "Files can be browsed while the container is running. Copy in/out may still work for stopped containers.";
        }

        return string.IsNullOrWhiteSpace(combined) ? fallback : combined;
    }

    /// <summary>Stops the live log stream (call when navigating away from the page).</summary>
    public void StopStreaming() => _logStreamer.Stop();

    /// <summary>Restarts the live log stream for the current selection (e.g., page re-entry).</summary>
    public void ResumeStreaming()
    {
        if (Selected is not null && _logStreamer.CurrentContainerId != Selected.Id)
        {
            LogCleared?.Invoke();
            _logStreamer.Start(Selected.Id);
        }
    }

    private CancellationTokenSource? _statsCts;

    /// <summary>Starts polling live resource stats for the selected container.</summary>
    public void StartStatsPolling()
    {
        StopStatsPolling();

        var target = Selected;
        if (target is null)
        {
            return;
        }

        _statsCts = new CancellationTokenSource();
        var token = _statsCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var stats = await _wslc.GetStatsAsync(target.Id, token).ConfigureAwait(false);
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (Selected?.Id != target.Id)
                        {
                            return;
                        }

                        if (stats is null)
                        {
                            StatCpu = "-";
                            StatCpuValue = 0;
                            StatMem = "-";
                            StatMemValue = 0;
                            StatMemUsage = "-";
                            StatNetIO = "-";
                            StatBlockIO = "-";
                            StatPids = 0;
                        }
                        else
                        {
                            StatCpu = stats.CpuPercent;
                            StatCpuValue = stats.CpuValue;
                            StatMem = stats.MemPercent;
                            StatMemValue = stats.MemValue;
                            StatMemUsage = stats.MemUsage;
                            StatNetIO = stats.NetIO;
                            StatBlockIO = stats.BlockIO;
                            StatPids = stats.Pids;
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // ignore transient stats errors
                    _logger.LogDebug(ex, "Transient container stats poll error.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    public void StopStatsPolling()
    {
        try
        {
            _statsCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        _statsCts?.Dispose();
        _statsCts = null;
    }

    /// <summary>
    /// Tears down the log stream (and its wsl.exe child) and any stats polling. Invoked when the
    /// DI container is disposed at app shutdown, since this view model is a singleton.
    /// </summary>
    public void Dispose()
    {
        StopStatsPolling();
        _monitor.StatusChanged -= OnStatusChanged;
        _watchdog.HealthChanged -= OnHealthChanged;
        _logStreamer.Dispose();
    }

    private void OnStatusChanged(object? sender, EngineStatusSnapshot e)
    {
        Reconcile(e.Containers);
        StatusMessage = e.Summary;
    }

    private void Reconcile(IReadOnlyList<ContainerInfo> incoming)
    {
        var filtered = ShowAll
            ? incoming
            : incoming.Where(c => c.State == ContainerState.Running).ToList();

        var byId = filtered.ToDictionary(c => c.Id, StringComparer.Ordinal);

        // Remove rows that no longer exist.
        for (var i = Containers.Count - 1; i >= 0; i--)
        {
            if (!byId.ContainsKey(Containers[i].Id))
            {
                Containers.RemoveAt(i);
            }
        }

        // Update existing / add new, preserving incoming order.
        var existing = Containers.ToDictionary(r => r.Id, StringComparer.Ordinal);
        for (var i = 0; i < filtered.Count; i++)
        {
            var model = filtered[i];
            if (existing.TryGetValue(model.Id, out var row))
            {
                row.Update(model);
                var currentIndex = Containers.IndexOf(row);
                if (currentIndex != i && i < Containers.Count)
                {
                    Containers.Move(currentIndex, i);
                }
            }
            else
            {
                var newRow = new ContainerRowViewModel(model);
                if (i <= Containers.Count)
                {
                    Containers.Insert(i, newRow);
                }
                else
                {
                    Containers.Add(newRow);
                }
            }
        }

        // Resolve network names for rows that don't have one yet (via inspect, cached per row).
        foreach (var row in Containers.Where(r => !r.NetworkResolved))
        {
            _ = ResolveNetworkAsync(row);
        }

        // Probe GPU passthrough once per running container (via a cheap exec check).
        foreach (var row in Containers.Where(r => r.IsRunning && !r.GpuChecked))
        {
            _ = ResolveGpuAsync(row);
        }

        RefreshHealth();
    }

    /// <summary>Applies configured-flag and the latest watchdog health state onto each row.</summary>
    private void RefreshHealth()
    {
        var configured = _settings.HealthChecks
            .Where(h => h.Enabled && h.IsValid)
            .Select(h => h.ContainerName)
            .ToHashSet(StringComparer.Ordinal);

        var states = _watchdog.Latest.Containers
            .ToDictionary(c => c.ContainerName, StringComparer.Ordinal);

        foreach (var row in Containers)
        {
            row.HasHealthCheck = configured.Contains(row.Name);
            if (states.TryGetValue(row.Name, out var snapshot))
            {
                row.Health = snapshot.State;
                row.HealthRestartCount = snapshot.RestartCount;
                row.HealthMaxRestarts = snapshot.MaxRestarts;
            }
            else if (!row.HasHealthCheck)
            {
                row.Health = ContainerHealthState.Unknown;
                row.HealthRestartCount = 0;
                row.HealthMaxRestarts = 0;
            }
        }
    }

    private void OnHealthChanged(object? sender, HealthSnapshot e) => RefreshHealth();

    private async Task ResolveGpuAsync(ContainerRowViewModel row)
    {
        row.GpuChecked = true;
        try
        {
            var (hasGpu, gpuName) = await _wslc.GetGpuInfoAsync(row.Id);
            row.HasGpu = hasGpu;
            row.GpuName = gpuName;
        }
        catch
        {
            // leave GpuChecked true; a transient failure shouldn't hammer exec every poll
        }
    }

    private async Task ResolveNetworkAsync(ContainerRowViewModel row)
    {
        row.NetworkResolved = true;
        try
        {
            var result = await _wslc.InspectContainerAsync(row.Id);
            if (!result.Success)
            {
                row.NetworkResolved = false;
                return;
            }

            var details = ContainerDetails.Parse(result.StandardOutput);
            row.Network = string.IsNullOrWhiteSpace(details.NetworkMode) ? "-" : details.NetworkMode;
        }
        catch
        {
            row.NetworkResolved = false;
        }
    }

    private void RequestRefresh() => _monitor.RequestRefresh();

    [RelayCommand]
    private void Refresh() => RequestRefresh();

    [RelayCommand]
    private async Task RunAsync()
    {
        var dialog = new RunContainerDialog(_wslc, _settings.Registries, _profiles);
        var result = await _dialogs.ShowDialogAsync(dialog);
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary || dialog.Options is null)
        {
            return;
        }

        await ExecuteAsync($"Creating container from {dialog.Options.Image}…",
            async () =>
            {
                // `wslc run` auto-pulls if needed — refresh Azure auth just-in-time.
                await _authRefresher.EnsureFreshForReferenceAsync(dialog.Options.Image);
                return await _wslc.RunContainerAsync(dialog.Options);
            });
    }

    [RelayCommand]
    private async Task ImportComposeAsync()
    {
        var dialog = new ImportComposeDialog();
        if (await _dialogs.ShowDialogAsync(dialog) != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(dialog.Yaml))
        {
            return;
        }

        IReadOnlyList<RunProfile> parsed;
        try
        {
            parsed = ComposeImporter.Parse(dialog.Yaml);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Import failed", ex.Message);
            return;
        }

        if (parsed.Count == 0)
        {
            await _dialogs.ShowMessageAsync(
                "Nothing to import",
                "No services with an image were found in the compose file.");
            return;
        }

        foreach (var profile in parsed)
        {
            _profiles.Save(profile);
        }

        StatusMessage = $"Imported {parsed.Count} profile{(parsed.Count == 1 ? "" : "s")}";
        await _dialogs.ShowMessageAsync(
            "Compose imported",
            $"Saved {parsed.Count} run profile{(parsed.Count == 1 ? "" : "s")}: " +
            string.Join(", ", parsed.Select(p => p.Name)) +
            ".\n\nLoad one from the Run dialog, or from an image's ⋯ menu.");
    }

    [RelayCommand]
    private Task StartAsync(ContainerRowViewModel? row) =>
        row is null ? Task.CompletedTask :
        ExecuteAsync($"Starting {row.Name}…", () => _wslc.StartContainerAsync(row.Id));

    [RelayCommand]
    private Task StopAsync(ContainerRowViewModel? row) =>
        row is null ? Task.CompletedTask :
        ExecuteAsync($"Stopping {row.Name}…", () => _wslc.StopContainerAsync(row.Id));

    [RelayCommand]
    private Task RestartAsync(ContainerRowViewModel? row) =>
        row is null ? Task.CompletedTask :
        ExecuteAsync($"Restarting {row.Name}…", () => _wslc.RestartContainerAsync(row.Id));

    [RelayCommand]
    private Task KillAsync(ContainerRowViewModel? row) =>
        row is null ? Task.CompletedTask :
        ExecuteAsync($"Killing {row.Name}…", () => _wslc.KillContainerAsync(row.Id));

    [RelayCommand]
    private async Task RemoveAsync(ContainerRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            "Remove container",
            $"Remove container \"{row.Name}\" ({row.ShortId})? This cannot be undone.",
            "Remove");
        if (!ok)
        {
            return;
        }

        await ExecuteAsync($"Removing {row.Name}…", () => _wslc.RemoveContainerAsync(row.Id));

        // If we removed the currently-selected container, notify the detail page to navigate back.
        if (Selected?.Id == row.Id)
        {
            SelectionCleared?.Invoke();
        }
    }

    [RelayCommand]
    private async Task LogsAsync(ContainerRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Fetching logs for {row.Name}…";
        try
        {
            var result = await _wslc.GetLogsAsync(row.Id);
            var text = string.IsNullOrWhiteSpace(result.StandardOutput)
                ? (string.IsNullOrWhiteSpace(result.StandardError) ? "(no output)" : result.StandardError)
                : result.StandardOutput;
            await _dialogs.ShowMessageAsync($"Logs · {row.Name}", text);
            StatusMessage = "Ready";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Terminal(ContainerRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _wslc.OpenTerminal(row.Id);
        StatusMessage = $"Opened terminal for {row.Name}";
    }

    [RelayCommand]
    private async Task HealthCheckAsync(ContainerRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var existing = _settings.HealthChecks
            .FirstOrDefault(h => string.Equals(h.ContainerName, row.Name, StringComparison.Ordinal));
        var hostPorts = row.Model.Ports
            .Where(p => p.HostPort > 0)
            .Select(p => p.HostPort)
            .Distinct()
            .ToList();

        var dialog = new HealthCheckDialog(row.Name, hostPorts, existing);
        var result = await _dialogs.ShowDialogAsync(dialog);
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            return;
        }

        // Replace any existing policy for this container. Build a new list and swap the reference
        // atomically so the watchdog's background loop never observes a mid-mutation list.
        var updated = _settings.HealthChecks
            .Where(h => !string.Equals(h.ContainerName, row.Name, StringComparison.Ordinal))
            .ToList();

        if (dialog.Result is { IsValid: true } config)
        {
            updated.Add(config);
            StatusMessage = config.Enabled
                ? $"Health check configured for {row.Name}"
                : $"Health check disabled for {row.Name}";
        }
        else
        {
            StatusMessage = $"Health check removed for {row.Name}";
        }

        _settings.HealthChecks = updated;
        _settings.Save();
        RefreshHealth();
    }

    [RelayCommand]
    private void FollowLogs(ContainerRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _wslc.FollowLogs(row.Id);
        StatusMessage = $"Streaming logs for {row.Name}";
    }

    [RelayCommand]
    private async Task InspectAsync(ContainerRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _wslc.InspectContainerAsync(row.Id);
            await _dialogs.ShowMessageAsync($"Inspect · {row.Name}",
                result.Success ? result.StandardOutput : result.ErrorText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenPort(ContainerRowViewModel? row)
    {
        var port = row?.PrimaryHttpPort;
        if (port is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = port.HostUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore browser launch failures
        }
    }

    [RelayCommand]
    private async Task PruneAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            "Prune containers",
            "Remove all stopped containers?",
            "Prune");
        if (!ok)
        {
            return;
        }

        await ExecuteAsync("Pruning stopped containers…", () => _wslc.PruneContainersAsync());
    }

    private async Task ExecuteAsync(string message, Func<Task<CommandResult>> action)
    {
        IsBusy = true;
        StatusMessage = message;
        try
        {
            var result = await action();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync("Operation failed", result.ErrorText);
                StatusMessage = "Operation failed";
            }
            else
            {
                StatusMessage = "Done";
            }
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Operation failed", ex.Message);
            StatusMessage = "Operation failed";
        }
        finally
        {
            IsBusy = false;
            _monitor.RequestRefresh();
        }
    }
}
