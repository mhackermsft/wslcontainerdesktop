# WSL Container Desktop — Copilot instructions

A native **WinUI 3 / .NET 10** desktop app (Docker-Desktop-like) that manages **WSL containers**
via the `wslc.exe` preview CLI, a single-node **k3s** cluster inside WSL, and container registries.
It is a packaged (MSIX-identity) app that minimizes to the system tray.

For deep design detail read `docs/ARCHITECTURE.md`; user-facing features live in `README.md`.

## Environment & build

- **Requires Windows 11** with the WSL container preview (`wslc.exe`, default
  `C:\Program Files\WSL\wslc.exe`) and the **.NET 10 SDK**. The app cannot fully build on Linux —
  the WindowsAppSDK XAML compiler step requires Windows.
- Work from `src\WslContainerDesktop`. Always target the **x64** platform.
- **Build:** `dotnet build -c Debug -p:Platform=x64`
- **Run (dev):** `dotnet run -c Debug -p:Platform=x64`. Use `dotnet run`, **not** the bare
  `bin\...\WslContainerDesktop.exe` — this is a packaged app; running the raw exe crashes at
  startup with `REGDB_E_CLASSNOTREG`. `dotnet run` registers the debug MSIX identity, refreshes the
  loose layout, and launches with package identity. A plain `dotnet build` leaves the *registered*
  app pointing at a stale layout.
- **Fast dev loop:** `tools\launcher\Build-And-Run.ps1` rebuilds, redeploys, and launches in one step.
- **Release:** `.github/workflows/release.yml` (manual `workflow_dispatch`) builds a signed MSIX;
  publish profiles live in `Properties/PublishProfiles/`.
- **No test project exists.** Do not invent a test command; validate by building to **0 warnings**
  and running the app.

## Architecture (the big picture)

Strict **MVVM + constructor DI** (`Microsoft.Extensions.DependencyInjection`, wired in
`App.xaml.cs ConfigureServices`). Layer boundaries, top to bottom:

- **Views/** (XAML + thin code-behind) — no business logic. `Views/Controls/` holds extracted `UserControl`s.
- **ViewModels/** — CommunityToolkit `[ObservableProperty]` / `[RelayCommand]`; one VM per page.
- **Services/** — all I/O and orchestration; interface-backed (`IWslcService`, `IKubernetesService`, …).
- **Models/**, **Helpers/** (converters, `NativeMethods` P/Invoke, `UiSafe`), **Tray/**.

Key cross-cutting services to understand before changing behavior:

- **All external process calls funnel through `ProcessExecutor.RunAsync`.** `ProcessRunner` wraps
  `wslc.exe`; `WslRootShell` wraps `wsl.exe -u root -e sh -c "…"` for k3s and owns shell escaping.
  Long-lived streams (`logs -f`, `port-forward`) are owned by `LogStreamer` / `PortForwardManager`,
  not `ProcessExecutor`.
- **`StatusMonitor`** is the *single* background poller and source of truth for engine + cluster
  health (tray, status bar, pages all observe it). It raises events on the UI thread via a captured
  `DispatcherQueue`, so it is registered with a DI **factory** and first resolved in `OnLaunched`.
- **`KubernetesService`** is a thin facade over collaborators (`K8sInstaller`, `K8sResourceClient`,
  `PortForwardManager`, `K8sManifestSanitizer`). k3s status probes use sentinel markers
  (`@@STATE=`, `@@NODES`, …) — never hand-write them; use the constants in `K8sStatusProtocol`.
- **Compose** is "desktop-as-daemon": `ComposeImporter` parses `docker-compose.yml`;
  `ComposeProjectSupervisor` brings a project up/down/restart as a unit, and `HealthWatchdog` /
  `RestartPolicyWatchdog` enforce health & `restart:` policies. There is **no background daemon** —
  these only run while the app is open. See the compose feature matrix in `docs/ARCHITECTURE.md`.
- **`Program.cs`** is a custom entry point (`DISABLE_XAML_GENERATED_MAIN`) enforcing a single
  running instance via `AppInstance` before starting WinUI.

## Conventions specific to this codebase

- **Every source file starts with the GPLv3 header** (see any file in `Services/`). Keep it on new files.
- Nullable reference types and implicit usings are **on**; keep the build at **0 warnings**.
- File-scoped namespaces; one primary type per file. Prefer primary constructors for simple services.
- **Never build a command line by string concatenation.** Route all external process calls through
  `ProcessExecutor` (or `WslRootShell` for k3s), and escape *every* interpolated value; prefer
  `ArgumentList` where a shell isn't required. **Secrets never touch a command line** — use
  `--password-stdin` and keep tokens in memory only; never log them.
- **`wslc` capability gaps to emulate, not assume:** there is no `wslc cp` (copy via `exec` +
  base64 / tar), no `network connect` (a container attaches to only its first network at run time),
  and no `--add-host` (`extra_hosts` appended to `/etc/hosts` via `exec` after start).
- Framework `async void` handlers must route work through **`Helpers/UiSafe.Run`** (awaits inside
  try/catch and logs) so a failing handler can't crash the app. Log swallowed exceptions (≥ Debug)
  or leave a one-line comment justifying a silent catch.
- Extracted section `UserControl`s expose the page's VM as a `DependencyProperty` whose change
  callback calls `Bindings.Update()` so compiled `x:Bind` re-evaluates.
- **MSIX AppData redirection gotcha:** for the packaged app, writes to `%LOCALAPPDATA%` are
  redirected to `...\Packages\<PFN>\LocalCache\Local`. Any path handed to an external process
  (`wslc`) must use `ApplicationData.Current.LocalCacheFolder.Path`, not the literal `%LOCALAPPDATA%`.
- Persisted state is plain JSON under the app's local data folder (`settings.json`,
  `run-profiles.json`, `compose-projects.json`); load/parse failures must **never crash the app**
  (fall back to defaults + log).
- **Adding a page:** register the VM in `App.xaml.cs ConfigureServices`, add a `NavigationViewItem`
  (with `Tag`) in `MainWindow.xaml`, add a matching case in `MainWindow.xaml.cs
  NavView_SelectionChanged`, and create `Views/XxxPage.xaml(.cs)` that resolves the VM via
  `App.Current.Services` and calls `RefreshAsync` in `OnNavigatedTo`.
