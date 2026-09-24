# Changelog

Notable changes to WSL Container Desktop, newest first.

This file records what actually changed for someone using the app — new capabilities, fixed
behavior, and anything that alters defaults. Internal refactoring and test-only work are omitted
unless they change what you can do or what you should expect.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Each version links to its release,
where the signed MSIX and installation steps live.

## [Unreleased]

### Fixed

- **Prune no longer hangs on WSLC 2.9.12 and later.** These engines ask "Are you sure you want to
  continue? [y/N]" before pruning, on a console nobody could type into, so **Prune** on the
  Containers, Images, Volumes and Networks pages — and **Reclaim space → Prune all** — stayed busy
  forever and removed nothing. The app already asks you to confirm, so it now passes `--force` to
  engines that support it. Older engines, which don't prompt, run exactly as before. If the app
  can't tell which kind of engine it has, it prunes nothing and says why rather than guessing.
- **An unexpected engine prompt can no longer freeze the app.** Prunes and deletes now run without
  a console to wait on, so a confirmation prompt fails with an explanation instead of looking like a
  success that did nothing. They also give up after 10 minutes instead of waiting indefinitely.

### Changed

- **Keep STDIN open (-i) now requires Run in background (-d).** In the foreground, `-i` left the
  Run dialog's command waiting for terminal input until the container exited. The dialog now
  explains this and disables **Run** until `-d` is on; a saved profile with that combination is
  refused with the same explanation.
- **Importing `docker run -it …` runs the container in the background with STDIN kept open**, with
  a note saying so, instead of starting a foreground run that could never receive input. Open a
  terminal to the container to use its shell. Imports without `-i` are unchanged — a plain
  `docker run image cmd` still runs in the foreground.
- **A Dockerfile path of `-` is refused.** It means "read the Dockerfile from standard input",
  which the app cannot supply, so the build waited forever; enter a file path instead.
- **Exiting the app now stops the engine commands it started.** Previously a stuck or long-running
  `wslc` command (for example an image pull or build in progress) kept running hidden after the app
  closed. Containers themselves keep running, and terminal windows opened from the app are not
  affected.

## [1.9.0] — 2026-09-23

The app can now **update itself** from GitHub releases, and **Set up Ollama** is a true one-click
local AI setup. Install this release manually; from the next release on, the app offers updates
itself.

### Added

- **The app can update itself.** Each time it starts it checks GitHub for a newer release; when there
  is one, a bar at the top of the window and a Windows notification offer **Update now**, which
  downloads the release, closes the app, installs the update and reopens it. It refuses any package
  that is not a newer build of WSL Container Desktop signed with the same certificate as your
  installation. Running containers are not stopped; Kubernetes port-forwards are. The launch check is
  **on by default** — it contacts `api.github.com` once per launch (retrying a few times if the
  network isn't ready) — and can be turned off, or run by hand, under **Settings → About**. Copies
  installed from a local development build can see updates but not install them. Updates start
  working from the release after this one, since this release must be installed manually.

### Changed

- **Set up Ollama is now one click.** If the Ollama image isn't on your machine, setup downloads
  `ollama/ollama:latest` itself (a few GB), showing how many layers are downloaded, and carries on,
  instead of stopping with "The Ollama image is not on this machine yet… Setup does not download
  images on your behalf." An Ollama image you already have is used as is, never re-downloaded. If the
  download fails, setup says why and nothing is created.
- **Set up Ollama works on older WSL container engines.** Engines whose `create` command has no
  `--pull` option were refused with advice to "prepare the runtime manually with an age-audited
  image"; setup now runs on them too.

## [1.8.1] — 2026-09-15

### Security

- **Container file exports cannot use Linux filenames to escape the Windows destination folder.**
  Legacy downloads and automatic drag-out staging now share the native path's protections, reject
  Windows device/alternate-stream names and ambiguous filenames, and refuse existing host symbolic
  links in destination paths. Both backends require a folder below the drive or share root.
  Legacy directory archives are also checked before extraction so crafted member or link names
  cannot overwrite files beside the requested source folder.
- **Dev-container host commands now require explicit approval on every start or rebuild.**
  The confirmation identifies the Windows workspace and exact `initializeCommand` hooks instead
  of treating a generic import confirmation as permission to run host commands. Declining stops
  the operation before host commands or container changes. Host initialization now rejects
  UNC, device, extended-length and overly long workspace paths instead of letting CMD silently
  execute in a different directory.

### Fixed

- **Disk usage offered space it could not free.** Every untagged image counted as reclaimable, but
  an image a container still holds cannot be pruned — and pulling a newer tag leaves the previous
  image untagged, so an image is routinely dangling *and* in use at the same time. "Remove dangling
  images" then freed nothing and the row stayed put, which looks exactly like a page refusing to
  refresh. Those images are still listed, but no longer counted as reclaimable, and the card says how
  many are being held back.
- **Images now say which container is using them.** A dangling row read `<none>` in both the
  repository and tag columns, which says an image is untagged but not why it is still on disk or why
  removing it fails. Rows in the Images list and the disk-usage dangling list now show "In use by
  &lt;container&gt;".

## [1.8.0] — 2026-09-14

The largest set of changes so far. The **Container AI Assistant** grows from a
chat box into a permissioned tool-calling agent, **Docker Compose** support becomes substantially
more faithful to the spec, and the **Templates** gallery gains a set of Azure emulators.

### Added

- **Deployments no longer disturb what is already running.** Launching a template, and asking the
  assistant to run a container or deploy a container template, take a free container name
  (`sqlserver-2`), free host ports, and their own named volumes instead of colliding. A repeat launch
  of a Compose template becomes its own project, with its own network and any pinned
  `container_name` moved too. Previously a name clash was either an error the assistant resolved by
  stopping and removing the container in its way, or a prompt to "Replace" the running deployment.
  (Importing a Compose file by hand still asks you to name the project, and assistant-deployed
  Compose stacks show what they will create or reuse in the compatibility review.)
- **A template can be deployed more than once.** Launch stays available after the first deployment
  and confirms what the repeat will run before starting it. The card counts the deployments, and
  Remove asks which one — leaving the others running. Deleting a deployment's data volumes reads the
  mounts of the container actually being removed, so a repeat cannot destroy the first one's data.
- **Assistant permissions are now a capability, not just a prompt.** *Allow the assistant to delete
  things* is off by default: until it is on, removing containers, volumes, networks, Kubernetes
  resources, or bringing a Compose project down is refused outright, and those tools are not even
  offered to the model. A separate *act without asking* switch waives approval prompts without
  granting deletion.
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

- **Tool activity is collapsed in the chat.** Tool requests, executions and results now appear as a
  single expandable line rather than raw container ids and JSON interrupting the conversation. The
  evidence is still there — it is what proves what happened — just one click away.
- **The context budget follows the model.** Rather than a fixed ceiling for every provider, a
  reported context window now raises it through a deliberately pessimistic conversion (a measured
  byte limit still wins). Combined with a smaller tool catalog, a conversation has roughly eight
  times the room it had before hitting "context budget exceeded".
- **Truncated history is summarized, not just announced.** When older turns no longer fit, they are
  replaced by a short factual digest of what was asked and which tools ran with what outcome.
- **Invalid tool calls explain themselves.** A rejected call now names the fields the tool actually
  accepts, so the assistant can correct itself instead of retrying the same shape, and it is
  reported as an assistant mistake rather than a configuration problem you need to fix.
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

- **The dashboard's live performance table never reported anything.** Total CPU sat at 0% and the
  table read "No running containers" regardless of what was running: the stats poll was cross-checked
  against the container inventory by exact ID, but `wslc stats` reports the full 64-character ID
  while `wslc list` reports the 12-character short form, so every row was discarded.
- **Icon-only buttons now have accessible names.** Seventy-four buttons across eleven views carried
  only a tooltip, which a screen reader does not announce, so Stop, Restart, Remove and Kill were all
  read as an unnamed "button".
- **Container creation times are shown as dates**, not the raw Unix seconds the engine reports, in
  the assistant's approval prompt and the activity timeline.
- **The assistant crashed once you had opened the Activity page.** Recording an action wrote to a
  collection bound to that page from a background thread, which surfaced as an unexplained
  `COMException` that killed the turn — and because the write happened immediately before the
  approval prompt was raised, the prompt never appeared, leaving the assistant explaining that it
  needed authorization it had never asked for.
- **The Compose review dialog would not scroll.** An `Expander` nested inside the scroll region
  reported its collapsed size, so the scrollbar appeared but had nothing to scroll.
- **The assistant status dot could show caution while the assistant was working.** The dot reflects
  a cached capability observation that expires, and a chat turn refreshing that cache never told the
  badge — so the app could observe tool support, use it, and still show a caution dot for the rest
  of the session. Capability changes are now announced, and opening the panel re-reads provider
  metadata (metadata only: no generation, download, or model load is started).
- **The status dot now explains itself on hover**, naming the first unmet condition — endpoint
  unreachable, runtime not running, model missing, still loading, or a model that cannot call tools
  — instead of leaving you to guess which of those it meant.
- **The Compose review no longer masks the names it exists to show.** Secret masking worked by value
  match, so an environment value that equalled a project, service, network, or volume name blanked
  that name throughout the dialog — routine for a WordPress file that sets `MYSQL_DATABASE=wordpress`
  in a `wordpress` project. Because the summary counted distinct service names, two masked services
  also collapsed into one, so a two-service project announced "1 service" on the screen you approve
  from. Those identifiers are no longer masked; every other value still is.
- **Running out of tool iterations is no longer reported as "Configuration needed".** The limit is a
  safety stop working as designed, usually because the model kept retrying a call that failed the
  same way; nothing is misconfigured, and the message now says so and points at the recorded
  outcomes.
- **Container targets given with a leading slash were not found.** The engine reports names as
  `/sqlserver` in its own error messages, but matching required the bare form.
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

[Unreleased]: https://github.com/mhackermsft/wslcontainerdesktop/compare/v1.9.0...main
[1.9.0]: https://github.com/mhackermsft/wslcontainerdesktop/releases/tag/v1.9.0
[1.8.1]: https://github.com/mhackermsft/wslcontainerdesktop/releases/tag/v1.8.1
[1.8.0]: https://github.com/mhackermsft/wslcontainerdesktop/releases/tag/v1.8.0
[1.7.0]: https://github.com/mhackermsft/wslcontainerdesktop/releases/tag/v1.7.0
[1.6.0]: https://github.com/mhackermsft/wslcontainerdesktop/releases/tag/v1.6.0
