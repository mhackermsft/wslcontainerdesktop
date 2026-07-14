# Architecture

This document describes how **WSL Container Desktop** is put together, for contributors. It
covers the layer boundaries, the key runtime services, and a few non-obvious design decisions.

> User-facing setup and features live in the top-level `README.md`. This document is for people
> changing the code.

---

## Overview

WSL Container Desktop is a native **WinUI 3 / .NET 10** desktop app that manages:

- **WSL containers** via the `wslc.exe` preview CLI,
- a single-node **Kubernetes (k3s)** cluster hosted inside a WSL distro, and
- **container registries** (Docker Hub, generic, and Azure Container Registry).

It is a packaged (MSIX) app that minimizes to the system tray and can start at sign-in.

The app follows **MVVM** with constructor **dependency injection**
(`Microsoft.Extensions.DependencyInjection`). There is no business logic in the views; pages are
thin and bind to view models, which call services.

```
Views (XAML + code-behind)      Pages, dialogs, and small UserControls. No business logic.
        |  binds to
ViewModels (CommunityToolkit)   Observable state + [RelayCommand]s. Orchestrate services.
        |  calls
Services                        All I/O: process execution, settings, registries, k3s, Azure.
        |  shells out to
External processes              wslc.exe, wsl.exe, az, curl | k3s installer.
```

Supporting layers: **Models** (DTOs, parsers, and simple state records), **Helpers**
(converters, native interop, formatting, `UiSafe`), and **Tray** (Win32 notify-icon).

---

## Project layout

| Folder | Contents |
|--------|----------|
| `Services/` | All I/O and orchestration services (see below). |
| `ViewModels/` | One view model per page, plus row/detail view models. |
| `Views/` | Pages and their code-behind. `Views/Controls/` holds extracted `UserControl`s. |
| `Dialogs/` | `ContentDialog` subclasses built in code (Run, Pull, Build, Apply YAML, …). |
| `Models/` | Data holders, `kubectl`/`wslc` JSON parsers, option builders. |
| `Helpers/` | Value converters, `NativeMethods` (P/Invoke), formatting, `UiSafe`. |
| `Tray/` | System-tray icon and its status-driven icon rendering. |

---

## Application startup

`Program.cs` is a custom entry point (the XAML-generated `Main` is disabled via
`DISABLE_XAML_GENERATED_MAIN`). It:

1. enforces a **single running instance** using `AppInstance` key registration and redirects a
   second launch's activation to the primary instance, then exits;
2. starts the WinUI application.

`App.xaml.cs`:

- builds the DI container in `ConfigureServices()` (constructed on the UI thread);
- installs global exception handlers (`UnhandledException`, `AppDomain.UnhandledException`,
  `TaskScheduler.UnobservedTaskException`) that route to the logger;
- in `OnLaunched`, resolves the `StatusMonitor`, wires the tray, creates the main window, and
  either shows it or hides it to the tray (honoring the *Start minimized* setting and detecting
  launch-at-sign-in via the `StartupTask` activation kind);
- disposes the DI container on exit so `IDisposable` singletons are torn down.

---

## Key services

### Process execution (`ProcessExecutor`, `ProcessRunner`, `WslRootShell`)

Every external command goes through a **single** helper, `ProcessExecutor.RunAsync`, which owns
process start, UTF-8 output draining, optional stdin, an optional streaming callback, an optional
timeout, and cancel/timeout process-tree kill. Callers only build a `ProcessStartInfo`:

- `ProcessRunner` wraps `wslc.exe` for container/image/volume/network operations.
- `WslRootShell` wraps `wsl.exe -u root -e sh -c "…"` for all k3s operations, and centralizes
  **shell-argument escaping** (`ShellEscape`, `SafeKind`, `NsSelector`, `NsArg`).
- `AzureCliService` builds its own `ProcessStartInfo` for `az` (with a default timeout).

Long-lived child processes (streaming `logs -f`, `kubectl port-forward`) are **not** run through
`ProcessExecutor`; they are owned by `LogStreamer` and `PortForwardManager` respectively, which
hold the `Process` and tear it down on stop/dispose.

**Secrets never touch a command line.** Registry logins use `wslc login --password-stdin` and the
token/password is written to stdin. ACR tokens obtained from `az acr login --expose-token` live
only in memory and are never logged.

### Kubernetes (`KubernetesService` facade + collaborators)

`KubernetesService` implements `IKubernetesService` but is a thin **facade**. The work is split
into cohesive collaborators over the shared `WslRootShell`:

- `K8sInstaller` — install/upgrade/uninstall/start/stop and version resolution.
- `K8sResourceClient` — status probes, resource list queries, single-object actions, `apply`.
- `PortForwardManager` — the lifecycle of `kubectl port-forward` sessions.
- `K8sManifestSanitizer` — a pure function that strips server-managed fields so a live
  `kubectl get -o yaml` can be re-applied.
- `K8sStatusProtocol` — the marker protocol (below).

### Status polling (`StatusMonitor`)

A **single** background poller is the source of truth for engine and cluster health, so the tray,
the status bar, and every page observe one stream instead of polling independently. It:

- polls the container engine and the k3s footer status on the configured cadence,
- raises `StatusChanged` / `K8sStatusChanged` **on the UI thread** (via the captured
  `DispatcherQueue`),
- compares consecutive snapshots to emit toast notifications for engine up/down transitions
  and for containers that stopped running (via `INotificationService`), and
- periodically refreshes Azure ACR tokens in the background.

Because it needs the UI `DispatcherQueue`, it is registered with a DI **factory** that captures
`DispatcherQueue.GetForCurrentThread()`; it is first resolved from `OnLaunched` on the UI thread.

### Notifications (`NotificationService`)

Wraps the Windows App SDK `AppNotificationManager` (available because the app has package
identity) to raise toasts for noteworthy events: image pull/build completion or failure,
container-stopped, and engine down/recovered. Toasts carry a `page` argument so a click routes
back into the app and navigates the relevant page (`App` handles both live `NotificationInvoked`
clicks and cold-start `AppNotification` activation). Every toast respects the Settings toggles —
a master *Show notifications* switch plus per-category switches — so notifications can be globally
muted (also from the tray menu).

The **tray** menu is status-driven: it shows the live running-container count, per-container quick
start/stop actions, and a *Mute notifications* toggle.

### Settings (`SettingsService`)

Plain-JSON settings persisted to `settings.json` under the app's local data folder. **No
credentials are stored** — only registry host/username metadata; the actual login is delegated to
the engine's credential store. Corrupt settings never crash the app (they fall back to defaults
and log a warning).

### Run profiles (`RunProfileStore`, `ComposeImporter`)

Reusable named run configurations (image, name, ports, env vars, volumes, network, flags) are
persisted to `run-profiles.json` next to `settings.json`, so a workload can be relaunched in one
click without re-entering the same options. The Run dialog offers a profile picker (prefill) plus
*Save as profile* / *Delete*; the Images page exposes a per-image **Run profile** submenu.
`ComposeImporter` seeds profiles from a *basic* `docker-compose.yml` (one profile per service,
common single-container fields only) using a small indentation-aware reader. Load/parse failures
never crash the app.

### Compose projects (`ComposeProjectStore`, `ComposeProjectSupervisor`)

For fuller compose support the app acts as the **orchestration layer above `wslc`**
("desktop-as-daemon"). `ComposeImporter.ParseProject` reads a much larger subset of the spec into a
`ComposeProject` (services + dependency graph): `image`, `container_name`, `command`, `entrypoint`,
`ports` (short and long form), `environment` (list/map), `env_file`, `volumes` (short and long
form), `networks`/`network_mode` (multiple networks per service), `user`, `working_dir`,
`hostname`, `labels`, `cpus`/`mem_limit`/`deploy.resources.limits`, `restart`, `depends_on`
(list and `condition:` form), and `healthcheck`. Values support `${VAR}` / `${VAR:-default}`
interpolation, and the reader resolves YAML anchors/aliases (`&`/`*`), `<<` merge keys, and `|`/`>`
block scalars. Projects persist to `compose-projects.json`.

`ComposeProjectSupervisor` brings a project **up / down / restart as a unit**: it starts each
service as a labelled container (`com.wsldesktop.project` / `com.wsldesktop.service`) in `depends_on`
topological order, gates `service_healthy` edges on a health probe, enrolls services that declare a
`healthcheck` into `HealthWatchdog`, and seeds `restart:` policies for services *without* a
healthcheck into `RestartPolicyWatchdog` (which restarts an exited container within a budget;
`on-failure` inspects the exit code, and a user's manual stop suppresses `unless-stopped`/`on-failure`
restarts). Because enforcement is in-process there is **no background daemon** — restart/health
policies apply only while the app runs, and `ReconcileAsync` re-adopts still-present projects on the
next launch. The Compose page lists projects with up/restart/down/remove; import is from that page.

Import is **file-based** (the user picks the `docker-compose.yml` from disk) rather than paste-based,
so the importer knows the file's folder: it seeds `${VAR}` interpolation defaults from a sibling
`.env` file and resolves relative `env_file` paths against that folder.

#### Compose feature support

| Feature | Support |
|---|---|
| `image`, `container_name`, `command`, `entrypoint`, `user`, `working_dir`, `hostname`, `labels` | **Supported** |
| `ports` (short `"h:c"` and long `target/published/protocol`) | **Supported** |
| `volumes` (short `"s:t[:ro]"` and long `type/source/target/read_only`) | **Supported** |
| `environment` (list and map), `env_file` | **Supported** — relative `env_file` paths resolve against the compose file's folder; a sibling `.env` seeds interpolation |
| `networks` / `network_mode` (multiple per service) | **Supported in model/import**; `wslc run` attaches the **first** network at run time |
| `${VAR}` / `${VAR:-default}` interpolation, anchors/aliases, `<<` merge, `\|`/`>` block scalars | **Supported** (block scalars are best-effort: blank lines not preserved) |
| `deploy.resources.limits.{cpus,memory}`, `cpus`, `mem_limit` | **Supported** |
| `healthcheck` | **Supported** — seeded into `HealthWatchdog` |
| `depends_on` incl. `condition: service_healthy` | **Supported** — start ordering + health gating |
| `restart:` (`no`/`always`/`on-failure`/`unless-stopped`) | **Supported (best-effort)** while the app runs; restart backoff timing is not byte-for-byte identical to Docker |
| Project lifecycle (`up`/`down`/`restart`), re-adoption on relaunch | **Supported** |
| Top-level named `volumes:` / `networks:` **creation**, `deploy.replicas`, `secrets`/`configs`, `build:`, Swarm `deploy` | **Not supported** (services require an `image`) |

### Diagnostics (`FileLoggerProvider`)

A dependency-free `ILoggerProvider` writes daily-rotated, size-capped logs (plus the debugger
output window in Debug builds). It resolves the **real** MSIX container path via
`ApplicationData.Current.LocalCacheFolder` so *Settings → Diagnostics → Open logs folder* points
at a path Explorer can actually open. Services log their previously-silent failures here.

---

## Notable design decisions

### The k3s status marker protocol

Each `wsl.exe` invocation pays cold-start/distro-attach overhead, so the status probes do the
install-check + service-state + resource-JSON in **one** shell script. The script emits sentinel
markers (`@@STATE=…`, `@@NODES`, `@@PODS`) that the C# side parses. To keep the producer (shell
script) and consumer (C#) from drifting, both use the constants and helpers in
**`K8sStatusProtocol`** — never hand-write the marker strings.

### Installer trust (trust-on-first-use)

k3s is installed by the upstream `get.k3s.io` script running as root. Rather than piping
`curl | sh`, the app **downloads the script to a file, hashes it (SHA-256), and only runs it if
the hash matches a stored pin**. The first install records the pin (trust-on-first-use); later
installs/upgrades re-verify and only prompt the user if the remote script has *changed*. The k3s
**version** is a free parameter, so install and upgrade are unaffected. The pin lives in
`settings.json` as `K3sInstallerSha256`.

### async void handlers

Framework event signatures force some `async void` handlers. To stop a failing handler from
crashing the app, route the work through **`Helpers/UiSafe.Run`**, which awaits inside a
try/catch and logs failures. New `async void` handlers should follow this pattern.

### x:Bind in extracted UserControls

Section `UserControl`s (e.g. `K8sDashboardSection`) that need the page's view model expose it as a
**`DependencyProperty`** whose change callback calls `Bindings.Update()`, so compiled `x:Bind`
expressions re-evaluate once the host passes the view model in.

---

## Build, run, and package

- **Build:** `dotnet build -p:Platform=x64 -c Debug`
- **Run (dev):** `dotnet run -c Debug -p:Platform=x64` registers the debug MSIX identity and
  launches with package identity. A plain `dotnet build` leaves the registered loose-layout stale,
  so use `dotnet run` (or re-launch from the AppsFolder AUMID) after building.
- **Package:** publish profiles live in `Properties/PublishProfiles/`. The manifest publisher
  (`Package.appxmanifest`) must match the code-signing certificate used for a distributable MSIX.

---

## Conventions

- Nullable reference types and implicit usings are **on**; keep the build at **0 warnings**.
- File-scoped namespaces; one primary type per file.
- Prefer primary constructors for simple services; keep `_field` naming for classes that also hold
  non-injected state.
- All new external-process calls go through `ProcessExecutor` (or the `WslRootShell` helpers for
  k3s); never build a command line by string concatenation.
- Escape **every** value interpolated into a shell command; prefer `ArgumentList` where a shell
  isn't required.
- Log swallowed exceptions (at least at `Debug`) or leave a one-line comment justifying a silent
  catch.
