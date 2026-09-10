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

### Owned local AI runtime (`LocalAiSetupService`)

Ollama container lifecycle is separate from native providers and capability observation.
Setup serializes with removal, resolves discovery names to full inspected IDs, and requires
managed/operation labels plus a matching labelled model-volume snapshot and exact loopback
API binding. New setup uses a cached image ID with `--pull never`; shared create-help
tri-state capabilities select GPU or definitive unsupported CPU before any mutation.
Create/verify/start is not retried through another backend on failure.

Only a current-operation labelled container can be automatically cleaned up, by immutable ID,
under a bounded recovery token. Existing/unrelated resources and all model data are retained.
Volume deletion by mutable name has no atomic ownership guarantee in WSLC, so runtime removal
returns separate runtime/data outcomes and explicitly reports unfulfilled model deletion.
`LocalRuntimeResourceState` can be reused by native runtimes; container labels, mounts and GPU
creation rules must not leak into a Foundry adapter. Capability caches are invalidated before
owned changes and after completion/failure. Running/created is not model or tool readiness.
See `AI-CONTRACT-TESTS.md` for regression coverage, preparation policy and recovery limitations.

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

### WSL virtual machine (`WslSystemService`)

Host-level operations on the **WSL VM itself**, as opposed to the container engine, backing the
*WSL engine* page. It reads `.wslconfig` resource limits (`[wsl2]` memory/processors/swap), reports
platform info via `wsl --version` and the distro list via `wsl -l -v` (both run with `WSL_UTF8=1`
so `wsl.exe` emits UTF-8, not UTF-16LE), and shuts WSL down via `ShutdownWslAsync`
(`wsl --shutdown`). Note that a WSL `.vhdx` grows but never shrinks on its own; the reliable way to
reclaim space is pruning images/containers/volumes on the *Disk usage* page.

### Run profiles (`RunProfileStore`, `ComposeImporter`)

Reusable named run configurations (image, name, ports, env vars, volumes, network, flags) are
persisted to `run-profiles.json` next to `settings.json`, so a workload can be relaunched in one
click without re-entering the same options. The Run dialog offers a profile picker (prefill) plus
*Save as profile* / *Delete*; the Images page exposes a per-image **Run profile** submenu.

The Containers toolbar has an **Import from docker run** button (`DockerRunParser` →
`ImportDockerRunDialog`): a pasted `docker run`/`podman run` command line is tokenized (honoring
quotes and `\` line continuations) and mapped onto `RunContainerOptions`, then the Run dialog opens
pre-filled; unrepresentable flags are reported as warnings rather than failing. (The two dialogs are
shown sequentially so no nested `ContentDialog` is ever open.) Conversely, a **running container can
be saved as a profile** (`ContainerConfigImporter` → `SaveRunProfileDialog`): the container's
`wslc inspect` JSON is diffed against the image's `inspect` so only user-specified
env/cmd/entrypoint/workdir/user are kept. `ContainerMounts` normalizes available inspect metadata;
recoverable named volumes and absolute Windows/UNC binds (including read-only mode) are captured.
Anonymous, internal/Linux, ambiguous or conflicting mounts are omitted with warnings shown before
saving; absent legacy mount metadata remains unknown, not an empty mount configuration.
Existing profiles keep their original schema. Hostname recovery remains unavailable.
`ComposeImporter` seeds profiles from a *basic* `docker-compose.yml` (one profile per service,
common single-container fields only) using a small indentation-aware reader. Load/parse failures
never crash the app.

### Container inventory, capabilities and volume usage

`WslcJsonParser.ParseContainers` accepts legacy numeric states/structured ports and current textual
states/display ports in arrays or object streams. Unknown state/date/port information remains
explicitly unknown. `ContainerPortResolver` bounds and caches inspect work for authoritative
published ports; it is not a live health cache. Malformed inventory or failed list commands throw:
only successful empty responses count as a complete zero-container inventory.

`IWslcCapabilitiesService` probes the configured executable using bounded, non-mutating help/version
commands. Snapshots report `Supported`, `Unsupported` or `Unknown` independently per feature,
including separate run/create health flags. Executable identity changes invalidate cached results,
and concurrent callers share probes. The minimum stays **WSLC 2.9.9.0**: optional integration uses
native operations only with positive evidence, definite absence selects a documented legacy path,
and unknown evidence surfaces a diagnostic. Never retry a failed native mutation through a fallback.
`AiCapabilityGuidance` sends only feature names and detected states to diagnostics, not executable
paths, raw help, errors or inspect data. Availability is not Docker parity or proof of app integration.

`VolumeUsageResolver` builds one bounded inspect snapshot across all containers, including stopped
containers and volumes shared by several containers. `Exact`, `Partial`, `Unknown`, `Estimated` and
`Unused` distinguish positive mount evidence, incomplete metadata and legacy timestamp estimates.
Only confirmed `Unused` volumes enter the reclaimable display; unknown/estimated absence is never
proof of eligibility. Prune behavior is unchanged and the engine remains the deletion authority.

### Templates gallery (`TemplateCatalog`, `TemplateConfigStore`, `ImageUpdateService`)

The **Templates** page (`TemplatesViewModel`) renders a curated, static catalog (`TemplateCatalog`)
of `StackTemplate`s grouped by category. Single-container templates carry prefilled
`RunContainerOptions`; Compose templates carry raw `docker-compose.yml` text. **Launch** starts a
template directly with no dialog — single containers via `wslc run` (offering to replace a
name-conflicting container first, since named volumes keep the data), Compose stacks via
`ComposeViewModel.ImportAndUpAsync` (a non-interactive import + bring-up). A per-card **Settings**
button opens the Run dialog (single container) or `ConfigureComposeDialog` (editable project name +
YAML) to configure *before* starting; confirming both starts the template **and** saves the choices.
Saved per-template configs are keyed by `StackTemplate.Id` and persisted to `template-configs.json`
(next to `settings.json`) by `TemplateConfigStore`, so a later Launch reuses the most recent config.
Load/persist failures never crash the app.

`ImageUpdateService` powers the Images page's **Check for updates**: it reads each local image's
repo-digests and compares them against the registry's current manifest digest, flagging images that
are behind so the UI can show an **↓ Update** badge and offer a one-click pull.

### Container file transfers (`WslcFileTransfer`)

Transfers select native `wslc container cp` only when detected; definite absence selects the existing
exec/base64/tar backend (running container with the required tools), and unknown availability produces
a diagnostic. Native failures are not retried through legacy code. Transfer destinations are directories:
basenames are preserved, host destinations are created, and upload archives include the requested
destination hierarchy to retain mkdir-p behavior. Native container paths must be absolute and
traversal-free; downloaded basenames must be representable on Windows.

Native transfer does not require an in-container shell, but still uses host `tar.exe` and staging.
Uploads use streamed, disk-backed PAX archives under the concrete MSIX `LocalCacheFolder` path.
`WslcCopyInput` is a narrow exception to ordinary `ArgumentList` execution: a fixed
`cmd /d /v:off /s /c` template redirects an archive file to seekable stdin, needed by WSLC's
archive Content-Length handling. Executable/target/archive values use quoted, single-expansion,
child-only environment entries; quote/newline/NUL delimiters and oversized strings are rejected.
No dynamic command fragments, `CALL`, AutoRun or delayed expansion are allowed.

Source links are archived without enumerating directory-link targets; native extraction may follow
existing container destination-directory links. Overwriting a host symbolic link is rejected.
No ownership/metadata preservation guarantee is made (`--archive` is not emitted; upstream treats
it as a compatibility no-op). Cancellation can leave partial destination contents. Only the
operation-owned staging archive is cleaned; sharing-violation cleanup retries are bounded, and a
retained archive path is surfaced rather than silently abandoned.

**Evidence boundary:** native archive behavior comes from
[WSL 2.9.11 ContainerTasks.cpp](https://github.com/microsoft/WSL/blob/2.9.11/src/windows/wslc/tasks/ContainerTasks.cpp),
[WSLCContainer.cpp](https://github.com/microsoft/WSL/blob/2.9.11/src/windows/wslcsession/WSLCContainer.cpp)
and [upstream copy tests](https://github.com/microsoft/WSL/blob/2.9.11/test/windows/wslc/e2e/WSLCE2EContainerCpTests.cpp),
plus controlled local stopped/shell-removed transfer fixtures. This is not a promise of Docker parity.
The Files page's **Download path** supports known-path transfers without browsing; browsing,
text preview, path editing and diff remain shell-dependent.

### Container filesystem diff (`WslcService.DiffContainerAsync`)

The container detail **Changes** tab is a `docker diff` equivalent. Because `wslc` exposes no `diff`
primitive, the changeset is **emulated**: the running container's root filesystem is walked via
`wslc exec … find / -xdev -exec stat …` and compared against a pristine baseline walk of the same
image (`wslc run --rm --entrypoint sh <image> -c …`). `-xdev` keeps the walk on the root device, so
pseudo filesystems and volumes drop out automatically; the container's own `/proc/mounts` targets
(plus `/.dockerenv`) are additionally excluded so engine-injected bind mounts like `/etc/hosts` and
`/etc/resolv.conf` don't show up as spurious changes. Comparing the `mode|size|mtime` of each path
yields **added / changed / deleted** entries. The baseline requires an image with a shell, so
distroless/scratch images surface a friendly message instead of a diff.

### Compose projects (`ComposeProjectStore`, `ComposeProjectSupervisor`)

For fuller compose support the app acts as the **orchestration layer above `wslc`**
("desktop-as-daemon"). `ComposeImporter.ParseProject` reads a much larger subset of the spec into a
`ComposeProject` (services + dependency graph): `image`, `build` (context/dockerfile/args/target/
labels), `container_name`, `command`, `entrypoint`, `ports` (short and long form), `environment`
(list/map), `env_file`, `volumes` (short and long form), `networks`/`network_mode` (multiple networks
per service), `user`, `working_dir`, `hostname`, `domainname`, `labels`,
`cpus`/`mem_limit`/`deploy.resources.limits`, `tmpfs`, `ulimits`, `shm_size`, `stop_signal`,
`dns`/`dns_search`/`dns_opt`, `secrets`/`configs` refs, `restart`, `stop_grace_period`, `profiles`,
`extra_hosts`, `depends_on` (list and `condition:` form, including `service_completed_successfully`),
and `healthcheck`. Top-level `networks:`, `volumes:`, `secrets:` and
`configs:` blocks are parsed too. YamlDotNet 16.3.0 reads syntax into a bounded private value tree;
`ComposeImporter.Yaml` handles aliases/merge keys and value-only interpolation, and
`ComposeImporter.Merge` handles Compose normalization/merging independently of YAML parsing.
Nested default/required/alternative expressions distinguish unset/empty and preserve `$$`.
Mappings merge, ordinary sequences append, resource sequences merge by their spec keys, and
command/entrypoint/health-test values replace. `!reset` and `!override` work on mapping values.
Block scalar blank lines, indentation, folding and chomping are preserved.
Top-level `include:` remains a best-effort merge (main file wins), not the spec's independent
project mechanism; `extends:` resolves local/external bases but still has file-resolution gaps.
The first present sibling override is merged over the base. Active `profiles:` come from
`COMPOSE_PROFILES`. The persisted `compose-projects.json` schema is unchanged.
See [the parser decision, publication audit, boundaries and #83 contracts](COMPOSE-PARSER.md).

`ComposeProjectSupervisor` brings a project **up / down / restart as a unit**: on `up` it first
**provisions** declared (non-external) `networks:`/`volumes:` via `wslc network/volume create`, then
**builds** images for services with a `build:` section (tagged `project_service`), then starts each
service as a labelled container (`com.wsldesktop.project` / `com.wsldesktop.service`) in `depends_on`
topological order. It **skips services excluded by the active `profiles:`** (a service with no
profile always starts; a profiled service starts only when one of its profiles is active). It gives
each container its **service name as a `--network-alias`** so siblings
resolve it by name (Compose-style DNS discovery), bind-mounts file-backed `secrets`/`configs`
read-only (there is no `wslc` secret store), gates `service_healthy` edges on a health probe and
`service_completed_successfully` edges on the dependency exiting 0, appends `extra_hosts:` entries to
each container's `/etc/hosts` via `exec` after start (there is no `--add-host` flag), enrolls
services that declare a `healthcheck` into `HealthWatchdog`, and seeds `restart:` policies for
services without positive health auto-heal into `RestartPolicyWatchdog` (which restarts an exited container
within a budget; `on-failure` inspects the exit code, and a user's manual stop suppresses
`unless-stopped`/`on-failure` restarts). Stops honor each service's `stop_grace_period` and
`stop_signal` (`wslc stop -t/-s`). Project-created volumes and networks are **namespaced with the
project name** at import (`myproj_data`, `myproj_appnet`, like `docker compose`), with service mount
sources and network references rewritten to match, so resources are isolated between projects;
external volumes/networks keep their exact declared name. `down` also removes project-created
networks (volumes are preserved, like `docker compose down`); **removing** a project (deleting its
definition) additionally deletes the volumes it created (like `docker compose down --volumes`), while
external volumes are always preserved. Because enforcement is in-process there is **no background
daemon** — restart, auto-heal and app-owned probes apply only while the app runs; native checks
are engine-owned. `ReconcileAsync` re-adopts
still-present projects on the next launch. The Compose page lists projects with up/restart/down/
remove; import is from that page.

**Config/secret staging (MSIX-safe).** File-backed `configs:`/`secrets:` are not bound from their
original location; each source file is first **materialized (copied) into a staging directory** and
that staged path is bound read-only. Staging exists to dodge two failure modes: (1) some Windows
directories don't enumerate reliably inside the `wslc` VM's 9P share, so an in-place file bind's
parent isn't found and runc falls back to `mkdir` on the read-only share; and (2) **MSIX AppData
redirection** — for the packaged app, writes to `%LOCALAPPDATA%` are transparently redirected into
`...\Packages\<PFN>\LocalCache\Local`, but `wslc` (an external process without the package's
redirection view) would be handed the *unredirected* path, find nothing there, and let runc
pre-create the bind source as a **directory** — so the container reads a directory where its
config/secret file should be (e.g. nginx: `pread() … Is a directory`). The staging root is therefore
the package's **real** `LocalCacheFolder` (`ApplicationData.Current.LocalCacheFolder.Path`, not
further redirected) when packaged, and `%LOCALAPPDATA%` when unpackaged — in both cases the path the
app writes equals the path `wslc` reads. As a defense-in-depth net against the same directory-race
under `wslc` session mount pressure (its bind-mount subsystem leaks a slot per distinct host path,
hard cap 15/session, released only by `wslc system session terminate`), each staged bind is
**verified from inside the VM** (a throwaway `busybox` container checks the source mounts as a file)
before the real container runs; on failure it re-stages at a fresh unique path (which busts `wslc`'s
per-path negative cache) and retries, and only surfaces a "restart the WSL session" recovery prompt
if every attempt still fails.

Import is **file-based** (the user picks the `docker-compose.yml` from disk) rather than paste-based,
so the importer knows the file's folder: it seeds `${VAR}` interpolation defaults from a sibling
`.env` file and resolves relative `env_file`, `build.context`, and `secrets`/`configs` `file:` paths
against that folder. During import the parser also **collects warnings** for any compose keys it does
not honor (e.g. `privileged`, `cap_add`, `logging`, unknown top-level keys) plus partially-supported
features (`deploy.replicas` scaling); these are shown in a confirmation dialog
so the user can cancel or import anyway before the project is saved. `x-` extension keys and
recognized-but-cosmetic keys (`version`) are never flagged.
Interpolation precedence is explicit importer entries > process environment > sibling `.env`;
empty values override lower sources. Service `env_file` never seeds YAML interpolation; later
env files override earlier files, and inline environment overrides all of them. Dotenv parsing
remains a simple assignment reader. Invalid YAML/shapes and required/malformed interpolation throw
`ComposeConfigurationException` with value-free diagnostics, before any project persistence or
engine call. Template configuration is now saved only after import validation succeeds, and
parse failures no longer produce a misleading "launched" status. Framework browse/navigation work
uses `UiSafe.Run`.

**Multi-network lifecycle.** `ComposeNetworkOrchestrator` chooses a backend from detected support.
Native multi-network services use create -> connect -> start, so required attachments precede workload
startup. Network-scoped aliases and static IPv4 options survive parsing, persistence and cloning;
one IPv4 IPAM configuration is supported. Definite absence retains the first network with a warning;
unknown support fails the affected service before replacing its container. Native attachment failure
cleans only the operation's new container/endpoints, not external or unverified-owner networks.
Re-adoption validates endpoint state: missing attachments can be repaired with disconnect rollback
support; mismatched aliases/IPs request recreation rather than silently disrupting a live endpoint.
Dependency readiness and watchdog enrollment include only successfully ready services.

#### Compose feature support

These are implementation capabilities, not full specification conformance claims. The
[versioned conformance corpus](COMPOSE-CONFORMANCE.md) distinguishes passing configuration subsets,
known differences, actionable warnings, and missing fail-closed diagnostics. All 21
spec-derived projections now have actual pinned CLI config captures. A separate opt-in real-WSLC
harness covers owned lifecycle scenarios; it has not been executed or runtime-certified. The
issue #82 layer updates the parser and its assertions; no supervisor or capability policy changes.

| Feature | Support |
|---|---|
| `image`, `container_name`, `command`, `entrypoint`, `user`, `working_dir`, `hostname`, `domainname`, `labels` | **Supported** — command argv preserves quoted/empty tokens; empty command/entrypoint clearing of image defaults at runtime is not certified |
| `build:` (short + long form: `context`, `dockerfile`, `args`, `target`, `labels`, `no_cache`, `pull`, `pull_policy`) | **Supported** — built and tagged `project_service` on up; `context` resolves against the compose folder; `pull_policy: always/build` maps to `--pull` |
| `ports` (short `"h:c"` and long `host_ip/target/published/protocol`) | **Supported subset** — normalized uniqueness, IPv6 host bindings and bounded ranges; other long-form fields warn |
| `volumes` (short `"s:t[:ro]"` and long `type/source/target/read_only`) | **Supported subset** — target-key merging; unsupported long mount types/modes reject and unsupported nested options warn |
| `environment` (list and map), `env_file` (scalar, list, and long `path:`/`required:` form) | **Partial** — exact empty/null distinction and last-file/inline precedence for simple assignments; dotenv syntax and required-file failure handling remain incomplete (#83) |
| Top-level `networks:` / `volumes:` **creation** (driver, `driver_opts`, labels; `external` skipped) | **Supported** — created on up via `wslc network/volume create`; networks removed on down |
| `networks` / `network_mode` per service, service-name DNS aliases | **Capability-gated** — create/connect/start for multiple native endpoints; legacy first-network fallback with warning. Special host/none/container/service modes remain distinct. |
| `secrets:` / `configs:` (file-backed) | **Supported (best-effort)** — source file bind-mounted read-only (`/run/secrets/<name>` or the config target); no in-engine secret store |
| `tmpfs`, `ulimits`, `shm_size`, `stop_signal`, `stop_grace_period`, `dns`/`dns_search`/`dns_opt` | **Supported** — mapped to the matching `wslc run`/`wslc stop` flags |
| `profiles:` | **Supported** — services with a profile start only when one of their profiles is in the project's active set (from `COMPOSE_PROFILES` in the environment / `.env`); unprofiled services always start |
| `extends:` (same-file and cross-file `file:`/`service:`) | **Partial** — resolved before model projection using shared merging; missing references, cycles, path ownership and extends-specific sequence deduplication remain #83 work |
| `include:` (top-level) | **Partial** — short `- file.yml` and long `- path:` forms; files are merged under the main file, unlike Compose's independent project/conflict rules; included `env_file` paths currently resolve against the main directory |
| `extra_hosts:` | **Supported (best-effort)** — appended to the container's `/etc/hosts` via `exec` after start (no `--add-host` flag); `host-gateway` resolves to the container's default gateway |
| Sibling override merge rules | **Supported normalized subset** — recursive maps, appended sequences, target-key volumes/secrets/configs, tuple-key ports; command/entrypoint/healthcheck.test replace; mixed environment/labels/build args merge by key. Captured CLI differences remain for duplicate DNS and equivalent mixed-form ports |
| `!reset` / `!override` | **Supported** on mapping values; tags at document roots or on sequence items are explicitly rejected |
| `$VAR`, `${VAR}`, `-`/`:-`, `?`/`:?`, `+`/`:+`, nesting, `$$` | **Supported subset** — per-file/value-only, unset versus empty, explicit > process > `.env`; required/malformed errors are secret-safe; no shell substitution or pattern replacement. Lazy unused nested required evaluation differs from pinned CLI rejection |
| YAML quoting, flow/block collections, anchors/aliases, `<<`, `\|`/`>` folding/chomping | **Supported within bounded single-document Compose YAML** via maintained parser; invalid syntax/duplicates/cycles reject; no arbitrary tagged types or full Compose schema validation |
| `deploy.resources.limits.{cpus,memory}`, `cpus`, `mem_limit` | **Supported** |
| `healthcheck` | **Capability-gated** — native shell checks when required run/create flags are supported; complete app backend for `CMD` argv or unsupported flags; unknown support is surfaced |
| `depends_on` incl. `condition: service_healthy` / `service_completed_successfully` | **Supported** — start ordering + health/exit gating |
| `restart:` (`no`/`always`/`on-failure`/`unless-stopped`) | **Supported (best-effort)** while the app runs; restart backoff timing is not byte-for-byte identical to Docker |
| Project lifecycle (`up`/`down`/`restart`), re-adoption on relaunch | **Supported** |
| `cap_add`/`cap_drop`, arbitrary `devices`, `sysctls`, `privileged`, rootfs `read_only`, `init`, `pid`/`ipc`, `mac_address`, `logging` drivers | **Not supported** — not advertised in current CLI help; GPU and read-only mount support are separate |
| `deploy.replicas` / scaling, Swarm `deploy` | **Not implemented** — no native Compose/scaling command; local scaling does not inherently require a daemon |
| Always-on restart after the app closes | **Not supported** — restart/auto-heal remain app-owned; no advertised native `--restart` |

#### Native and app-owned health

`NativeHealthOptions` preserves Compose test argv, interval, timeout, start period, start interval,
retries and disable/NONE independently from restart budgets. WSLC 2.9.11's `--health-cmd` is a
shell check, so `CMD` argv uses `ExecHealthAsync` without joining argv into shell source. Native
selection requires every requested flag for the actual run/create path; unsupported `start_interval`
selects the whole app backend rather than dropping timing flags. Desired settings remain saved.
Fallback scheduling has 1-second resolution; sub-second interval limitations are diagnosed.

`StatusMonitor` owns `NativeHealthMonitor`: bounded fresh inspect (at most four concurrent,
3 seconds each / 8 seconds total), rotating work, explicit unknown failures and an absent/disabled
configuration cache keyed by container ID/generation. Port metadata caching is separate and never
supplies live health. Native starting/healthy/unhealthy/absent states do not change process state.
`HealthWatchdog` observes native results instead of issuing duplicate command probes; TCP checks,
auto-heal and restart remain app-owned. Probe retries and restart budgets are independent.
`service_healthy` waits on the shared snapshots with verified startup ID, generation and freshness,
and blocks dependents on failure rather than optimistically starting them.

Native settings apply at creation, not live update. Existing incompatible/inherited checks remain
visible, while conflicting app command probes/auto-heal are suspended rather than duplicated.
The app never recreates an existing container solely to apply a health edit. Deferred create
enrollment is bounded and process-local, occurs only after successful start, and is cleared after
successful removal; Compose re-adoption restores saved desired configuration on a later app launch.

Controlled 2.9.11 fixtures demonstrated starting -> healthy, unhealthy while running, explicit
disable and progressing native timestamps without app ownership. This was **not** an actual
desktop close/reopen trial or a packaged watchdog-lifecycle certification. Legacy help fixtures are
synthetic and do not substitute for runtime validation on an installed 2.9.9 binary.

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
