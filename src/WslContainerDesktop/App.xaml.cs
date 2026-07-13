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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tray;
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop;

public partial class App : Application
{
    private MainWindow? _window;
    private TrayIcon? _tray;
    private StatusMonitor? _monitor;
    private HealthWatchdog? _watchdog;
    private EngineHealth _engineHealth = EngineHealth.Unknown;
    private string _engineSummary = string.Empty;
    private ContainerHealthState _worstContainerHealth = ContainerHealthState.Unknown;
private ILogger<App>? _logger;
private bool _isExiting;

public App()
{
    InitializeComponent();
    Services = ConfigureServices();

    // Route otherwise-fatal, unobserved failures to the log so a crash leaves a breadcrumb.
    UnhandledException += OnUnhandledException;
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled AppDomain exception (terminating: {Terminating}).", e.IsTerminating);
    TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        _logger?.LogError(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    };
}

public new static App Current => (App)Application.Current;

public IServiceProvider Services { get; }

public MainWindow? MainWindow => _window;

/// <summary>True while a real shutdown is in progress (lets the window close for good).</summary>
public bool IsExiting => _isExiting;

private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
{
    _logger?.LogCritical(e.Exception, "Unhandled UI exception: {Message}", e.Message);
}

protected override void OnLaunched(LaunchActivatedEventArgs args)
{
    _logger = Services.GetRequiredService<ILogger<App>>();
    _logger.LogInformation("WSL Container Desktop starting.");

    var settings = Services.GetRequiredService<ISettingsService>();
    settings.Load();

        // Reclaim files staged from containers in previous sessions (including any a crash left
        // behind), since they are never deleted while the app is running.
        ContainersViewModel.ClearTempFiles(_logger);
        // The status monitor needs the UI DispatcherQueue. It's resolved here (on the UI thread)
        // so its DI factory can capture the dispatcher; the same singleton is later injected into
        // the view models.
        _monitor = Services.GetRequiredService<StatusMonitor>();

        _tray = new TrayIcon();
        _tray.OpenRequested += () => _monitor!.Dispatcher.TryEnqueue(ShowMainWindow);
        _tray.QuitRequested += () => _monitor!.Dispatcher.TryEnqueue(ExitApplication);
        _tray.Initialize();

        _monitor.StatusChanged += OnEngineStatusChanged;
        _monitor.Start();

        // Health watchdog rides on the monitor's polling and enforces per-container probes.
        _watchdog = Services.GetRequiredService<HealthWatchdog>();
        _watchdog.HealthChanged += OnHealthChanged;
        _watchdog.NotificationRequested += OnHealthNotification;
        _watchdog.Start();

        _window = new MainWindow();
        _window.ApplyTheme(settings.Theme);

        // When Windows launches us at sign-in (via the StartupTask), start quietly in the
        // tray so we don't steal focus on login; otherwise honor the StartMinimized setting.
        var launchedAtLogin = WasActivatedByStartupTask();

        if (settings.StartMinimized || launchedAtLogin)
        {
            _window.HideToTray();
        }
        else
        {
            _window.Activate();
        }
    }

    private static bool WasActivatedByStartupTask()
    {
        try
        {
            var kind = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent()
                .GetActivatedEventArgs().Kind;
            return kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask;
        }
        catch
        {
            return false;
        }
    }

    private void OnEngineStatusChanged(object? sender, EngineStatusSnapshot e)
    {
        _engineHealth = e.Health;
        _engineSummary = e.Summary;
        UpdateTray();
    }

    private void OnHealthChanged(object? sender, HealthSnapshot e)
    {
        _worstContainerHealth = e.Worst;
        UpdateTray();
    }

    private void OnHealthNotification(string title, string message) =>
        _tray?.ShowNotification(title, message);

    /// <summary>Rolls the engine status and per-container health into the single tray glyph.</summary>
    private void UpdateTray()
    {
        // Engine reachability dominates: if it's unreachable, nothing else matters.
        var health = _engineHealth;
        if (health == EngineHealth.Healthy)
        {
            health = _worstContainerHealth switch
            {
                ContainerHealthState.Down => EngineHealth.Down,
                ContainerHealthState.Degraded => EngineHealth.Degraded,
                _ => EngineHealth.Healthy,
            };
        }

        var tooltip = _engineHealth switch
        {
            EngineHealth.Healthy => $"WSL Container Desktop — {_engineSummary}",
            EngineHealth.Down => "WSL Container Desktop — engine unreachable",
            _ => "WSL Container Desktop",
        };

        if (health == EngineHealth.Degraded)
        {
            tooltip += " · a container is unhealthy";
        }
        else if (health == EngineHealth.Down && _engineHealth == EngineHealth.Healthy)
        {
            tooltip += " · a container is down";
        }

        _tray?.UpdateStatus(health, tooltip, _engineSummary.Length == 0 ? "Status: unknown" : _engineSummary);
    }

    public void ShowMainWindow()
    {
        _window ??= new MainWindow();
        _window.ShowFromTray();
    }

    /// <summary>
    /// Brings the app's window forward. Called when a second instance redirects its
    /// activation here. Marshals to the UI thread since the redirect fires on a
    /// background thread.
    /// </summary>
    public void BringToForeground()
    {
        var dispatcher = _monitor?.Dispatcher;
        if (dispatcher is not null)
        {
            dispatcher.TryEnqueue(ShowMainWindow);
        }
        else
        {
            ShowMainWindow();
        }
    }

    public void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _logger?.LogInformation("WSL Container Desktop exiting.");

        // Tear down any running port-forwards so no wsl.exe processes are orphaned.
        try
        {
            Services.GetRequiredService<IKubernetesService>().StopAllPortForwards();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to stop port-forwards during shutdown.");
        }

        _watchdog?.Dispose();
        _monitor?.Dispose();
        _tray?.Dispose();
        _window?.ForceClose();

        // Reclaim files staged from containers this session (best-effort; skips any still open in
        // an external editor, which the next startup cleanup will retry).
        ContainersViewModel.ClearTempFiles(_logger);

        // Dispose the DI container so IDisposable singletons (e.g. ContainersViewModel's
        // LogStreamer and its wsl.exe child) are torn down deterministically.
        (Services as IDisposable)?.Dispose();

        Exit();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Diagnostics: a rolling file log under %LOCALAPPDATA%\WslContainerDesktop\logs plus the
        // debugger output window. This is the single logging seam the rest of the app writes to so
        // previously-swallowed failures leave a breadcrumb. The provider is also registered as a
        // singleton so the Settings page can offer "open logs folder".
#if DEBUG
        var fileLogger = new FileLoggerProvider(LogLevel.Debug);
#else
        var fileLogger = new FileLoggerProvider(LogLevel.Information);
#endif
        services.AddSingleton(fileLogger);
        services.AddLogging(builder =>
        {
#if DEBUG
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
#else
            builder.SetMinimumLevel(LogLevel.Information);
#endif
            builder.AddProvider(fileLogger);
        });

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<IWslcService, WslcService>();
        services.AddSingleton<IKubernetesService, KubernetesService>();
        services.AddSingleton<IAzureCliService, AzureCliService>();
        services.AddSingleton<IRegistryCredentialStore, RegistryCredentialStore>();
        services.AddSingleton<IRunProfileStore, RunProfileStore>();
        services.AddSingleton<RegistryAuthRefresher>();
        services.AddSingleton<StartupService>();
        services.AddSingleton<DialogService>();

        // StatusMonitor needs the UI DispatcherQueue. Because the singleton is first resolved from
        // OnLaunched (which runs on the UI thread), the factory can capture the dispatcher directly
        // here — no static bridge required. GetForCurrentThread must be non-null at that point.
        services.AddSingleton(sp => new StatusMonitor(
            sp.GetRequiredService<IWslcService>(),
            sp.GetRequiredService<IKubernetesService>(),
            sp.GetRequiredService<RegistryAuthRefresher>(),
            sp.GetRequiredService<ISettingsService>(),
            DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("StatusMonitor must first be resolved on the UI thread."),
            sp.GetRequiredService<ILogger<StatusMonitor>>()));

        services.AddSingleton<HealthWatchdog>();

        services.AddSingleton<ContainersViewModel>();
        services.AddSingleton<ImagesViewModel>();
        services.AddSingleton<VolumesViewModel>();
        services.AddSingleton<ReclaimSpaceViewModel>();
        services.AddSingleton<NetworksViewModel>();
        services.AddSingleton<RegistriesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<KubernetesViewModel>();
        services.AddTransient<K8sDetailViewModel>();

        return services.BuildServiceProvider();
    }
}
