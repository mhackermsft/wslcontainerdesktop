# WSL Container Desktop — Copilot instructions

A native **WinUI 3 / .NET 10** desktop app (Docker-Desktop-like) that manages **WSL containers**
via the WSL/wslc 3.0.1+ `wslc.exe` CLI, a single-node **k3s** cluster inside WSL, and container registries.
It is a packaged (MSIX-identity) app that minimizes to the system tray.

For deep design detail read `docs/ARCHITECTURE.md`; user-facing features live in `README.md`.

## Dependency policy (supply-chain security)

**Never add or upgrade to a dependency version published less than 7 days ago.** A just-released
version is exactly when a compromised, hijacked, or typosquatted package is most likely to be live
and least likely to have been reported and pulled. Age is therefore treated as a security control
in its own right, independent of how reputable the publisher is or how badly a fix is wanted.

- **Scope is every dependency**, not just NuGet: direct, transitive, and build/test-only packages,
  plus GitHub Actions, CLI tools, container images, and scripts the build or tests fetch.
- **Verify the publication date from an authoritative source** — the nuget.org registration index
  or the upstream release feed — *before* restoring it. "Looks established" is not evidence, and a
  package's own build timestamp is not its publication date.
- **If the date cannot be verified, stop and say so.** Never assume compliance. Waiting a few days
  is always cheaper than tracing a compromised dependency through a shipped build.
- **Prefer what is already referenced.** Adding a package needs the same justification as any other
  change; it is not a free shortcut. Most feature work here needs no new dependency at all.
- A version that is otherwise required but too new is a **blocker to raise**, not a judgment call to
  make silently. (This mirrors the MSIT dependency-age requirement.)

## Environment & build

- **Requires Windows 11** with WSL/wslc 3.0.1 or later (`wslc.exe`, default
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
  publish profiles live in `Properties/PublishProfiles/`. **Update `CHANGELOG.md` first** — see
  [Releasing](#releasing) below.
- **Tests:** `dotnet test tests\WslContainerDesktop.Tests\WslContainerDesktop.Tests.csproj -c Debug -p:Platform=x64`
  from the repository root; use focused filters for changed behavior. Build to **0 warnings**.
  Coordinate packaged smoke runs: deployment affects the registered app, even from another worktree.

## Localization

- Keep localization changes focused and include the affected XAML or C# display text and both
  locale resources in the same change.
- Keep `Strings/en-US/Resources.resw` and `Strings/zh-Hans/Resources.resw` key sets identical. A
  `x:Uid` applies every matching property to every element with that Uid. Share one Uid only among
  the same control type with compatible properties; a TextBox cannot consume a Button's `.Content`.
  Resource keys must also be unique ignoring case. Run `I18nResourceTests` as a quick check,
  then open the affected page in a packaged test build: the XAML compiler does not catch every
  resource property error.
- Set the nonempty effective language before constructing the shell. Pair each `x:Uid` with
  `i18n:Localization.Uid` so a runtime language switch updates existing controls and preserves
  input, selection and operation state. Refresh computed display properties on `UiText.LanguageChanged`.
  An empty saved preference means System default; resolve it independently of the persisted
  application override before writing to WinRT language APIs. See
  [the localization guide](../docs/LOCALIZATION.md) for resource and protocol-value rules.
- Follow [the localization validation steps](../docs/LOCALIZATION.md#validate-changes) for
  automated checks and packaged GUI coverage. Record the tested version, date and remaining gaps.
  Automated tests do not verify WinUI rendering or state retention during language switching.

## Releasing

`CHANGELOG.md` is the user-facing record of each release, and the generated release notes link to
it at the release's own tag. **Update and commit it before starting the release workflow**: the tag
is created from the repository as it stands, so a changelog written afterwards leaves that release's
link pointing at an entry that does not mention it.

Between releases, accumulate entries under a `## [Unreleased]` heading at the top. Add to it as
user-facing work lands rather than reconstructing the whole release at the end — that is when the
detail is still known, and it keeps an unreleased entry from claiming a version and date that do
not exist yet.

Steps, in order:

1. **Promote `[Unreleased]` to the new version** — `## [X.Y.Z] — YYYY-MM-DD`, newest at the top —
   and update the link list at the bottom of the file: point `[X.Y.Z]` at
   `…/releases/tag/vX.Y.Z`, and re-point `[Unreleased]` at `…/compare/vX.Y.Z...main`. Keep the list
   in the same newest-first order.
2. **Group entries under the headings that apply** — `Added`, `Changed`, `Fixed`, `Removed`,
   `Deprecated`, `Security` — and omit headings with nothing under them.
3. **Write for someone using the app**, not someone reading the diff. State what they can now do,
   what behaves differently, or what was broken and now isn't; when a fix is non-obvious, say what
   the user would have seen. Leave out pure refactoring, test-only work, and internal renames unless
   they change observable behavior or a default.
4. **Call out anything that changes an existing default** (a template's published port, a provider
   default, a settings value), because that is what silently breaks a working setup.
5. **Derive entries from the actual commit range** (`git log --no-merges vPREV..HEAD`), not from
   memory. Verify a claim before writing it; an inaccurate changelog is worse than a terse one.
6. **Commit the changelog**, then run **Actions → Build & Release (MSIX) → Run workflow** with the
   same `X.Y.Z`. The workflow stamps the manifest version, tags `vX.Y.Z`, and publishes the signed
   MSIX plus the `.cer` and `Install.ps1`.

Versions follow SemVer, and the workflow refuses to reuse an existing tag. Windows only treats a
package as an in-place update when the version increases.

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
  Long-lived streams (`logs -f`, `wslc events`, `port-forward`, the k3s keep-alive) are owned by
  `LogStreamer` / `EngineEventStream` / `PortForwardManager` / `KubernetesKeepAlive`, not `ProcessExecutor`.
- **`StatusMonitor`** is the *single* background poller and source of truth for engine + cluster
  health (tray, status bar, pages all observe it). It raises events on the UI thread via a captured
  `DispatcherQueue`, so it is registered with a DI **factory** and first resolved in `OnLaunched`.
- **Background polling must never start a WSL distribution** (issue #126). Starting one boots WSLg,
  which resets Windows' display and sleep idle timers. Only run a command inside a distribution from a
  background poll while `KubernetesKeepAlive` holds it: a distribution nothing holds can stop between a
  `wsl --list --running --quiet` check and the command, which then starts it again; see
  `K8sFooterProbePolicy`. Polling that only feeds visible UI waits on
  `IWindowVisibility` (hidden, minimized or display-off pauses it).
- **`WslRequirementService` / `WslPolicyService` / `EngineEventStream` / `WslcSettingsFileService`**
  own the 3.0.1 gate, enterprise WSL policy checks, `wslc events`, and `system info`-discovered settings file edits.
- **`KubernetesService`** is a thin facade over collaborators (`K8sInstaller`, `K8sResourceClient`,
  `PortForwardManager`, `KubernetesKeepAlive`, `K8sManifestSanitizer`). k3s status probes use sentinel
  markers (`@@STATE=`, `@@NODES`, …); never hand-write them, use the constants in `K8sStatusProtocol`.
  While the app runs, `KubernetesKeepAlive` holds one idle session so the pinned k3s distribution
  stays up; it ends with the app, and nothing changes WSL or Windows configuration.
- **Compose** is "desktop-as-daemon": `ComposeImporter` parses `docker-compose.yml`;
  `ComposeProjectSupervisor` brings a project up/down/restart as a unit, and `HealthWatchdog` /
  `RestartPolicyWatchdog` enforce app probes, auto-heal & `restart:` policies while the app is open.
  Native checks are engine-owned; `StatusMonitor` owns their bounded inspect observations. Never use
  the port metadata cache for live health or duplicate engine command probes.
  See the compose feature matrix in `docs/ARCHITECTURE.md`.
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
- Native copy's `WslcCopyInput` is the sole narrow fixed-template exception: seekable archive stdin
  via `cmd /d /v:off`, with validated quoted environment data. Never generalize it into a shell
  command builder. Native copy uploads still use a tar archive over stdin; browsing/diff remain
  separate shell-based features.
- **WSL/wslc 3.0.1 is the gated baseline.** Code directly against commands and flags advertised by
  3.0.1. Use `IWslcCapabilitiesService.GetAsync` only for features beyond that baseline (currently
  run/create `--health-start-interval`); `Unknown` must surface a diagnostic. Never infer optional
  availability from version alone or retry a failed native mutation through another path.
- **Inspect schemas vary:** use `ContainerMounts` and normalized `ContainerInfo`; missing metadata
  is unknown, not empty. List failures throw; successful empty inventory is valid.
- **Remaining advertised gaps:** no `--add-host` (`extra_hosts` uses `exec` after start), no
  advertised native restart policy flag, and no advertised Compose command. Do not conflate native
  health checks with app-owned auto-heal.
- Framework `async void` handlers must route work through **`Helpers/UiSafe.Run`** (awaits inside
  try/catch and logs) so a failing handler can't crash the app. Log swallowed exceptions (≥ Debug)
  or leave a one-line comment justifying a silent catch.
- Extracted section `UserControl`s expose the page's VM as a `DependencyProperty` whose change
  callback calls `Bindings.Update()` so compiled `x:Bind` re-evaluates.
- **Explain non-obvious engine flags in the UI.** A new option that maps to a `wslc` flag and isn't
  self-explanatory gets a `Helpers/FlagHelp` entry (plain fields such as name, ports or command
  don't). Only add a control a person would actually want; hide actions that can't work in the
  current state instead of leaving no-op buttons. Buttons (including toggle and drop-down buttons) show it in a rich
  hover tooltip; other controls (checkboxes, switches, fields, labels) get an `InfoTip` (i) next to
  their label. Verify the wording against the engine's `--help` and the arguments the app really passes.
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
