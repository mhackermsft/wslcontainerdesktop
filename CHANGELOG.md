# Changelog

Notable changes to WSL Container Desktop, newest first.

This file records what actually changed for someone using the app — new capabilities, fixed
behavior, and anything that alters defaults. Internal refactoring and test-only work are omitted
unless they change what you can do or what you should expect.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Each version links to its release,
where the signed MSIX and installation steps live.

## [Unreleased]

Slated for **1.8.0**. The largest set of changes so far. The **Container AI Assistant** grows from a
chat box into a permissioned tool-calling agent, **Docker Compose** support becomes substantially
more faithful to the spec, and the **Templates** gallery gains a set of Azure emulators.

### Added

- **Compose compatibility review before deployment.** Generated and template Compose stacks now show
  exactly what will be created, reused, approximated, or blocked — active instances, ports, mounts,
  networks, limits, and who owns health and restart behavior — before anything runs. Blockers can't
  be overridden, and a review expires after ten minutes and is revalidated against live inventory.
- **Local Compose service scaling** via `scale` / `deploy.replicas` or a saved per-service count.
  Reconciliation preserves unchanged instances rather than recreating the stack.
- **Targeted Compose lifecycle.** Apply, stop, or restart individual services from **Manage
  services**, with required dependencies pulled in automatically.
- **Multi-network Compose services.** Where the engine supports native network connect, a service is
  created, connected to every declared network, then started, keeping per-network aliases and static
  IPv4 addresses.
- **Compose GPU reservations.** `deploy.resources.reservations.devices` entries requesting the `gpu`
  capability now map to GPU passthrough. Previously Compose had no way to express GPU access at all,
  so GPU-dependent services silently ran on CPU.
- **Azure emulator templates** in a new **Azure** category: **Cosmos DB** (with Data Explorer),
  **Service Bus**, and **Event Hubs**, joining **Azurite**. The messaging emulators deploy with their
  required SQL Server backend and wait for it to pass a real health check before starting; each comes
  up with a default namespace, so there is no configuration file to write.
- **New templates** elsewhere: **SQL Server 2025** and **Open WebUI + Ollama**. Open WebUI reuses an
  Ollama container you already have (including the one behind the local AI assistant) and otherwise
  deploys its own, offering to download a model so the chat UI works immediately.
- **Assistant Compose and Kubernetes tools.** The assistant can deploy Compose stacks and templates,
  operate saved projects, and take scoped k3s actions — each through the same explicit review the UI
  uses, even when a tool is set to auto-approve.
- **Streaming assistant responses**, with loading, approval-waiting, execution, and actual tool
  outcomes shown separately from model narration.
- **Independent AI capability reporting.** Settings now reports chat, tool calling, structured JSON,
  streaming, and context observations separately from endpoint and authentication state. Unknown
  means unverified rather than supported, and **Test capabilities** runs bounded synthetic checks
  with a 90-second deadline.
- **The assistant knows the current date and time.** Every turn carries this PC's local and UTC
  clock and time zone, so it can answer "today"/"now" and reason about container ages instead of
  guessing from training data.
- **Native container copy** (`container cp`) where the engine advertises it, including transfers to
  and from stopped containers without an in-container shell.
- **Native engine health** is observed and surfaced in badges and the tray, separately from the
  app-owned watchdog that performs restarts.
- **Foundry Local** support for connecting to a locally installed Microsoft Foundry Local runtime.
  Implemented and exercised end to end, but **not currently exposed in the provider picker**; see
  [docs/FOUNDRY-LOCAL.md](docs/FOUNDRY-LOCAL.md).

### Changed

- **Compose parsing is far closer to the spec** — interpolation operators, YAML anchors, aliases and
  merge keys, block scalars, sibling override merging with `!reset` / `!override`, and strict local
  `include:` / `extends:` graphs. Invalid configuration now fails the import instead of producing a
  partially honored project. A versioned conformance corpus compares 21 cases against captured
  output from Docker Compose v2.39.4.
- **Repeated `up` reconciles** rather than recreating: unchanged running containers are kept, and
  only changed configuration or images cause a recreate.
- **Assistant evidence is sanitized before truncation.** Sensitive JSON fields, environment
  assignments, YAML blocks, authentication headers, connection strings, URL credentials, and private
  key blocks are masked in everything sent to a provider or shown in approval details. Detection is
  rule-based and best-effort, not arbitrary secret detection.
- **Approved tool arguments are frozen and validated** before execution, so a container target
  cannot change between approval and the action.
- **Local AI setup is ownership-safe.** It creates or reuses only its own verified container on
  `127.0.0.1:11434` and never adopts an unrelated Ollama just because the endpoint answers.
- **Review dialogs lead with the plain-language outcome**, show warnings only when the operation
  actually does the thing being warned about, and keep full detail one click away. A clean run no
  longer interrupts with a summary dialog.
- **Java dev sandbox now publishes host port 8095** (container 8080). It previously published 8080,
  which collided with the Nginx template — whichever launched second failed to bind.
- **Saved run profiles preserve recoverable mounts**, including named volumes and Windows/UNC binds,
  warning before saving about mounts it cannot represent.

### Fixed

- **First-time Compose deployments were blocked** whenever the project declared a new network or
  volume. The engine reports a missing resource as plain text (`Network not found: '…'`), which was
  treated as an unreadable-inventory error rather than the normal "does not exist yet" case.
- **Compose teardown could leave resources behind.** A network still holding a foreign endpoint
  failed to remove, and that failure aborted the run before volumes were deleted — silently keeping
  data the user asked to remove. Foreign endpoints are now disconnected first and failures no longer
  stop the remaining cleanup.
- **Volume usage is resolved from container inspect mounts**, so "Used by" accounts for named and
  anonymous volumes and stopped containers instead of reporting unknown.
- **Container inventory failures no longer surface as an empty list.** A failed listing reports an
  engine error rather than appearing to be a machine with no containers.
- **App icons are present in dev builds**, which previously showed no taskbar or title-bar icon.

### Security

- **The 7-day dependency-age rule is now an explicit, documented policy** covering every dependency —
  transitive, build/test-only, Actions, tools, and images — with the supply-chain rationale stated.

## [1.7.0] — 2026-09-09

Engine-compatibility release: the app adapts to what the installed `wslc.exe` actually supports
rather than assuming one schema or flag set.

### Added

- **Shared optional-capability detection.** Optional engine commands and flags are probed
  independently, with `Supported` / `Unsupported` / `Unknown` kept distinct. Unknown surfaces a
  diagnostic instead of being treated as unsupported, and a failed native operation is never retried
  through a legacy path.
- **Native container copy** with seekable archive input.
- **Multi-network Compose** with an ownership-safe create → connect → start lifecycle.
- **Native engine health observation** with restart suppression, kept separate from app-owned
  auto-heal.

### Fixed

- **Legacy and WSLC 2.9.11 container schemas** are both normalized, including the newer text states
  (`exited`), so container lists populate correctly across engine versions.
- **Volume usage** resolves from container inspect mounts.
- **Saved run profiles** preserve recoverable mounts instead of dropping them.
- **Inventory failures are contained** in launch and prune paths rather than propagating as empty
  results.

## [1.6.0] — 2026-09-02

### Fixed

- **WSL 2.9.9 JSON compatibility.** Support the engine's object-stream output and its updated image
  metadata schema, and stop duplicate built-in networks from appearing. This release also documented
  the minimum required WSL preview version (**2.9.9.0**).

---

Older versions (1.0.0 – 1.5.3) predate this changelog. Their tags remain in the repository, and
their changes can be reviewed with `git log v1.5.2..v1.5.3` and similar.

[Unreleased]: https://github.com/mhackermsft/wslcontainerdesktop/compare/v1.7.0...main
[1.7.0]: https://github.com/mhackermsft/wslcontainerdesktop/releases/tag/v1.7.0
[1.6.0]: https://github.com/mhackermsft/wslcontainerdesktop/releases/tag/v1.6.0
