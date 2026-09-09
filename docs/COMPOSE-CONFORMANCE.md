# Compose conformance corpus v1

## Scope and evidence

`tests/WslContainerDesktop.Tests/Fixtures/Compose/v1` is a synthetic, offline configuration corpus
for issue #86. It uses the existing xUnit runner and links the real `ComposeImporter`; no new
dependencies, Docker Desktop, daemon, image pulls, WSL installation, or package activation are needed.
It deliberately changes no production parsing or orchestration behavior.

**Current evidence is characterization against hand-authored spec projections, not verified output
of `docker compose config`.** No reference CLI was available when v1 was authored. There are no
captured `reference.json` files, and no real-engine runtime certification. Required-variable and
missing-file cases explicitly characterize unsafe diagnostic gaps instead of asserting successful
conformance. Later stack layers should fix these and remove the matching divergence entries.

The reference is pinned in `reference-provenance.json`:

| Reference | Pin / provenance |
|---|---|
| Compose specification | Commit [`c0c3dba71a73260cf9649e05370dcad6fe29e11c`](https://github.com/compose-spec/compose-spec/tree/c0c3dba71a73260cf9649e05370dcad6fe29e11c), not an invented numbered spec release |
| Reference CLI | Standalone Docker Compose **v2.39.4**, equivalent to the Compose plugin's `docker compose config` command |
| CLI publication | [GitHub release](https://github.com/docker/compose/releases/tag/v2.39.4), `published_at` **2025-09-19T08:49:23Z**; audited via GitHub release API |
| Fixture origin | Original synthetic YAML and hand-authored projected expectations; not copied runtime output |
| Runtime / legacy status | None; legacy help fixtures elsewhere in the suite are not certification on WSLC 2.9.9.0 |

Semantic sources are the pinned spec's [interpolation](https://github.com/compose-spec/compose-spec/blob/c0c3dba71a73260cf9649e05370dcad6fe29e11c/12-interpolation.md),
[merge](https://github.com/compose-spec/compose-spec/blob/c0c3dba71a73260cf9649e05370dcad6fe29e11c/13-merge.md),
[include](https://github.com/compose-spec/compose-spec/blob/c0c3dba71a73260cf9649e05370dcad6fe29e11c/14-include.md),
[profiles](https://github.com/compose-spec/compose-spec/blob/c0c3dba71a73260cf9649e05370dcad6fe29e11c/15-profiles.md),
and [services](https://github.com/compose-spec/compose-spec/blob/c0c3dba71a73260cf9649e05370dcad6fe29e11c/05-services.md)
sections. Docker's [config command documentation](https://docs.docker.com/reference/cli/docker/compose/config/)
describes canonical expansion and JSON rendering. The spec snapshot and CLI release are separate
pins, not a claim that the CLI implements exactly that spec commit.

## Run without engines

From the repository root, using the existing restored dependencies:

```powershell
dotnet test tests\WslContainerDesktop.Tests\WslContainerDesktop.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter FullyQualifiedName~Compose
```

Use `--filter Category=ComposeConfiguration` for the fixture theory alone, or
`--filter FullyQualifiedName~ComposeConformance` to include harness/projection checks. Windows runs
both portable and Windows test targets. The test assembly has a test-only entry point; xUnit still
runs normally through VSTest.

Each import runs in a fresh child process with an empty inherited environment. Only the Windows
system directory, disposable temporary/home directories, and declared synthetic `WCD_*` variables
(plus `COMPOSE_PROFILES`) are supplied. `.env`, includes and `env_file` resolve only in a copied,
uniquely named temporary case directory. The parent environment and working directory are not
changed. Each process is bounded to 30 seconds and its owned directory is removed in `finally`.
No fixture uses real credentials; `example.invalid` image names must never be pulled.

## Expectations and normalization

Each folder contains `compose.yaml`, supporting files, and `expectations.json`. `checks` uses
JSON-pointer-like keys into the **documented subset projection**, not arbitrary model serialization.
Every case asserts service inventory so dropping a service cannot silently pass. Failure messages
identify the case/source, semantic key, expected/app/reference values, and the tracked difference.
Exact warning lists are checked independently of config values.

| Representation | Normalization / scope |
|---|---|
| Services | Dictionary by name; `serviceNames` sorted ordinally; runtime IDs, timestamps, default project names and generated labels excluded |
| Environment | App `KEY=VALUE` list becomes a map; bare `KEY` remains null, distinct from `KEY=`; actual ambient values are never resolved by the projection |
| Ports | Canonical config port objects become app-style `host_ip:published:target/protocol`; default TCP omitted; list order retained |
| Mounts | Canonical config mount objects become `source:target[:ro]`; tests cover named-volume short/long forms, not every bind/driver option |
| Commands | Config argv is joined with whitespace-bearing tokens quoted, matching the app representation for the covered simple examples; not an argv-fidelity certification |
| Dependencies | Compare named edge conditions; default `required` metadata excluded; optional edges/restart propagation require later fixtures |
| Networks / health | Compare explicit aliases/IPs and desired health argv/timing fields; implicit default networks and supervisor-added service aliases are outside this projection |
| Profiles | Capture with `--profile '*'` to compare the full imported structure; profile activation/startup is a separate lifecycle concern |

Unselected keys are **not tested**. A passing case must not be generalized to every form of that
feature. No normalization silently strips mismatching selected keys. Missing projection keys fail,
and stale/resolved divergence records fail instead of allowing any actual value.

For a known config difference, `checks` holds the intended reference projection and `divergences`
holds the exact current `app` value, rationale, and tracking issue. For a `referenceError` case,
there is no valid reference config: `checks` instead characterizes current app output and
`diagnosticLimitation` states the missing rejection. Fixing that rejection requires updating the
worker/test contract, not replacing the reference error with a successful snapshot.

## Coverage and known gaps

| Case | Covered behavior / current evidence |
|---|---|
| `interpolation` | Set/unset/default, bare `$VAR`, escaped dollar; empty `${VAR-default}` incorrectly uses fallback (#82) |
| `yaml-anchors`, `yaml-block` | Anchor merge, quoted string scalars/comments, literal block with strip chomping; these examples agree |
| `environment` | Shell > `.env`, inline > env files; explicit empty value becomes bare variable and first env file wins (#82) |
| `override` | Map merge and command replacement agree; ports and DNS incorrectly replace the sequence (#83) |
| `include` | Imported service retained; its env file uses the wrong base directory and is skipped (#83) |
| `extends` | Cross-file inheritance with child environment override agrees for this example |
| `profiles` | Profile metadata retained; no claim about activation or explicit-service selection |
| `mounts-ports` | Short/long named read-only mounts and TCP/UDP ports agree for these examples |
| `networks` | Per-network aliases/IP retained; exact actionable legacy capability warning |
| `health-dependencies` | Health test argv/timing/retries and all three dependency conditions retained; not proof of startup behavior |
| `unsupported` | Exact ignored-privileged/scaling diagnostics; not a safe-to-launch assertion |
| `required-variable`, `missing-env-file` | Reference should reject; current importer returns a service without a diagnostic (#82) |

Additional cases needed in later layers include nested/alternative interpolation, duplicate keys,
invalid YAML, tagged reset/override, unique-key mount merges, recursive include/extends conflicts,
bind-path normalization, profile dependency validation, optional dependencies, and lifecycle drift.
This inventory supports the qualified compatibility matrix in README/architecture, not a blanket
Compose conformance claim.

## Explicit reference capture

Normal tests never regenerate outputs. The opt-in PowerShell 7 script only runs `version --short`
and `config --format json`; it does not install tools, invoke a daemon, resolve image digests, or
run/up/down workloads.

Before acquiring a standalone CLI, independently verify the pinned GitHub release's publication
date is at least seven days old and verify the platform binary's SHA-256 against that release's
published checksums. Follow the same policy for any separately installed prerequisites. Do not use
`latest`, a newly published version, an unverified binary, or a wrapper around a different CLI.
The script requires an already-installed binary and operator-approved checksum, rejects a hash
or version mismatch, and records both the binary digest and observed version.

```powershell
.\tools\compose\Update-ComposeReferences.ps1 `
  -ComposeExecutable C:\approved-tools\docker-compose.exe `
  -ApprovedSha256 '<64-hex-digits-from-the-verified-release-checksum>' `
  -Regenerate
```

The script copies synthetic inputs to a unique temporary directory; clears environment and Docker
config/context inheritance; sets an unusable loopback daemon endpoint; passes the project directory,
fixed project name, all profiles, and explicit base/override file list. It captures successful config
JSON or expected diagnostic failures. Owned absolute directory prefixes become `$FIXTURE`. The
resulting `reference.json` includes arguments, capture time, version, binary SHA-256, input hashes,
exit code, stderr and config. No arbitrary caller files or host `.env` files are captured.

Review each diff and run the fixture suite before committing captures. Once present, captures are
always compared to the selected intended reference values (not the divergent app baseline).
Input hashes invalidate stale captures, including changed environments/expectations. If the real
reference disagrees with a hand-authored value, inspect the semantic source and fix the expectation
or projection transparently; never relabel unexecuted expectations as captured CLI output. Keep
the initial provenance note as historical context; per-case capture records establish actual evidence.
The initial script has only syntax/fail-closed guard validation, not a successful real-CLI capture.

## Runtime boundary and opt-in protocol

**No real-engine runtime tests are implemented or enabled by this foundation.** Existing
`ComposeNetworkOrchestratorTests`, `ComposeNetworkSupervisorTests`, and `NativeHealthTests` use test
doubles for mutation ordering, capability decisions, failure cleanup and supervision. In particular,
`LegacyFallbackIsExplicitAndUnknownDoesNotDowngrade` checks a documented fallback versus unknown
capability rejection; creation/connect failures check that start is not attempted and only owned
objects are removed. These are not evidence from an installed legacy engine.

Future runtime certification must be a separate, explicit opt-in harness, not a default xUnit side
effect. Obtain permission before app registration/deployment on a shared Windows machine. Require
an operator-selected disposable engine/distro, already-approved local images (no implicit pulls),
recorded actual WSLC version/capability diagnostics, and a run-unique project name plus ownership
label (for example `wcd-conformance-<guid>`). Refuse name collisions and preserve a before-inventory.
Track exact returned resource IDs; on cancellation/failure clean only those still carrying the
run's ownership label. Never use global prune, wildcard deletion, or remove external volumes/networks.

| Runtime scenario | Required evidence before claiming it passes |
|---|---|
| Startup ordering | Timestamped start/health/exit observations proving all three dependency conditions, timeout and cancellation behavior |
| Network aliases | Actual DNS checks from owned peers on each network, native success and diagnosed unsupported/unknown behavior |
| Recreation | No-op retains IDs; material change replaces only the intended service; drift/failure never deletes unrelated objects |
| Supervision | Recorded health and restart decisions, manual-stop suppression, bounded retries and app-lifetime limitations |
| Cleanup | Before/after inventory proves unrelated resources untouched and owned partial creations cleaned on success/failure/cancel |

Pinned real CLI capture, strict unsupported-input rejection, and real-engine runtime evidence remain
unmet portions of #86; they are explicitly deferred rather than fabricated or represented by
permanently failing tests.
