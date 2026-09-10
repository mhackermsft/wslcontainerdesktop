# Compose conformance corpus v1

## Scope and evidence

`tests/WslContainerDesktop.Tests/Fixtures/Compose/v1` is a synthetic, offline configuration corpus
for issue #86, extended by issue #82. It uses the existing xUnit runner and links the real
`ComposeImporter`, including its audited YamlDotNet dependency. No Docker Desktop, daemon, image
pulls, WSL installation or package activation are needed. #82 changes parsing, not orchestration.

**All 21 cases now have actual `config --format json` reference captures** from the
official Windows x64 standalone Compose v2.39.4 binary. Initial expectations were hand-authored
spec projections; those projections are now compared with the captured output. Capture ran on
2026-09-10 in an isolated synthetic directory/environment, with no engine/workloads. The runtime
harness remains unexecuted and there is no real-engine runtime certification. Required-variable
cases now assert fail-closed, secret-safe errors; the missing-file case still characterizes an
unsafe diagnostic gap for #83. Resolved #82 differences no longer have divergence exemptions;
newly observed CLI differences are recorded explicitly rather than hidden by the projection.

The reference is pinned in `reference-provenance.json`:

| Reference | Pin / provenance |
|---|---|
| Compose specification | Commit [`c0c3dba71a73260cf9649e05370dcad6fe29e11c`](https://github.com/compose-spec/compose-spec/tree/c0c3dba71a73260cf9649e05370dcad6fe29e11c), not an invented numbered spec release |
| Reference CLI | Standalone Docker Compose **v2.39.4**, equivalent to the Compose plugin's `docker compose config` command |
| CLI publication | [GitHub release](https://github.com/docker/compose/releases/tag/v2.39.4), `published_at` **2025-09-19T08:49:23Z**; audited via GitHub release API |
| Binary integrity | `docker-compose-windows-x86_64.exe`, 77,505,536 bytes, SHA-256 `6b3bccfabcdd172e1d9e15d011b54c9b5b13b93b1153148108f55e4349055955`; checked against current authoritative GitHub release asset metadata before execution |
| Binary license | [Apache-2.0](https://github.com/docker/compose/blob/v2.39.4/LICENSE); binary cached only in session artifact tools, not committed or installed on PATH |
| Fixture origin | Original synthetic YAML and spec-derived projected expectations; `reference.json` files are actual config-only CLI output with per-case provenance |
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
uniquely named case directory under the test output folder (inside the checkout). The parent environment and working directory are not
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
| Environment | App `KEY=VALUE` list becomes a map; bare `KEY` remains null, distinct from `KEY=`; config JSON's `$$` serialization escape in keys and values becomes literal `$`, without resolving `$VAR`; actual ambient values are never resolved by the projection |
| Ports | Canonical config port objects become app-style `host_ip:published:target/protocol`; default TCP omitted; list order retained |
| Mounts | Canonical config mount objects become `source:target[:ro]`; omitted mount lists (including after reset) become empty lists; tests cover named-volume short/long forms, not every bind/driver option |
| Commands / entrypoints | Config argv is joined with whitespace-bearing tokens quoted for the selected simple examples. Separate parser tests check empty tokens, mixed quotes, newlines and Windows paths through argument construction; no runtime certification |
| Secrets / configs | Compare source/target only, expanding relative/default targets under `/run/secrets/` or `/`; ownership/mode are not normalized away and must not be inferred as supported |
| Dependencies | Compare named edge conditions; default `required` metadata excluded; optional edges/restart propagation require later fixtures |
| Networks / health | Compare explicit aliases/IPs and desired health argv/timing fields; implicit default networks and supervisor-added service aliases are outside this projection |
| Profiles | Capture with `--profile '*'` to compare the full imported structure; profile activation/startup is a separate lifecycle concern |

Unselected keys are **not tested**. A passing case must not be generalized to every form of that
feature. No normalization silently strips mismatching selected keys. Missing projection keys fail,
and stale/resolved divergence records fail instead of allowing any actual value.

For a known config difference, `checks` holds the intended reference projection and `divergences`
holds the exact current `app` value, rationale, and tracking issue. For a `referenceError` case,
there is no valid reference config. Cases with `appError` assert required message fragments and
`forbiddenDiagnostics`, and the worker returns a failure envelope with empty `serviceNames`, not
a project. Remaining gaps use `diagnosticLimitation` and characterize current output. Both forms
retain `referenceError` for future CLI capture; rejecting configuration is not a success snapshot.

## Coverage and known gaps

| Case | Covered behavior / current evidence |
|---|---|
| `interpolation` | Set/unset/default, bare `$VAR`, escaped dollar, exact empty `${VAR-default}` |
| `yaml-anchors`, `yaml-block` | Anchor merge, quoted string scalars/comments, literal block with strip chomping; these examples agree |
| `environment` | Process > `.env`, inline > env files, explicit empty values and later env-file precedence |
| `override` | Map merge, command replacement, unique ports and appended DNS |
| `include` | Imported service retained; its env file uses the wrong base directory and is skipped (#83) |
| `extends` | Cross-file inheritance with child environment override agrees for this example |
| `profiles` | Profile metadata retained; no claim about activation or explicit-service selection |
| `mounts-ports` | Short/long named read-only mounts and TCP/UDP ports agree for these examples |
| `networks` | Per-network aliases/IP retained; exact actionable legacy capability warning |
| `health-dependencies` | Health test argv/timing/retries and all three dependency conditions retained; not proof of startup behavior |
| `unsupported` | Exact ignored-privileged/scaling diagnostics; not a safe-to-launch assertion |
| `required-variable`, `required-override` | Required variables reject without leaking custom error text, even if the override would replace the invalid base value |
| `missing-env-file` | Reference should reject; current importer still returns a service without a diagnostic (#83) |
| `interpolation-nested` | Nested/alternative/required operators, empty/process/`.env` precedence, escaped dollars, literal mapping keys and structure-safe substituted values |
| `unset-warning` | Unset direct substitution warns without exposing values; empty variables/defaults/unused branches do not warn |
| `override-unique` | Mixed map/list environment/labels; port IP/protocol identity; target-key mounts/secrets/configs; command/entrypoint/health-test replacement. Explicit captured divergences: CLI removes duplicate DNS but retains equivalent short/numeric-long port bindings; the importer retains DNS duplicates and normalizes port tuple identity |
| `interpolation-unused-required` | Actual CLI rejection of a nested required expression in an unused default branch; importer lazy success is explicitly characterized, not claimed equivalent. Kept separate so all other nested-success keys still compare against valid captured config |
| `override-tags` | Reset removes attributes and individual environment entries; override replaces collections/maps; null versus empty commands |
| `yaml-multiline` | Flow collections, sequence aliases, merge precedence, quoted escapes and literal/folded blank lines/chomping |

`ComposeSemanticsTests` adds focused operator/error matrices, YAML duplicate/invalid/cycle/shape
rejections, alias/depth/size bounds, all six folding/chomping variants, explicit indentation, bare-CR
input, canonical ranges/IPv6, mixed build args/resource labels, unsupported-resource warnings,
bad override rejection, one-pass interpolation and saved-schema/argv round trips.
Additional cases needed in later layers include recursive include/extends conflicts, full dotenv
syntax, required-file semantics, profile dependency validation, optional dependencies and lifecycle drift.
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
Input hashes use `sha256-utf8-lf`: decode UTF-8, normalize CRLF to LF, and hash UTF-8 bytes, so Git
checkout line endings do not invalidate identical text on Windows versus Linux.

Review each diff and run the fixture suite before committing captures. Once present, captures are
always compared to the selected intended reference values (not the divergent app baseline).
Input hashes invalidate stale captures, including changed environments/expectations. If the real
reference disagrees with a hand-authored value, inspect the semantic source and fix the expectation
or projection transparently; never relabel unexecuted expectations as captured CLI output. Keep
the original spec-derived expectation origin as historical context; per-case capture records establish
actual evidence. `capturedCases` in the manifest makes deletion of established captures fail the suite.
Actual v2.39.4 capture revealed that environment dollar literals are re-escaped as `$$` in config
JSON; the projection now explicitly decodes that serialization layer, rather than declaring an
app semantic divergence or modifying captured output.

## Runtime boundary and opt-in protocol

**`ComposeRuntimeTests` is implemented but has not been executed against a real engine.** Existing
`ComposeNetworkOrchestratorTests`, `ComposeNetworkSupervisorTests`, and `NativeHealthTests` use test
doubles for mutation ordering, capability decisions, failure cleanup and supervision. In particular,
`LegacyFallbackIsExplicitAndUnknownDoesNotDowngrade` checks a documented fallback versus unknown
capability rejection; creation/connect failures check that start is not attempted and only owned
objects are removed. These are not evidence from an installed legacy engine.

The opt-in test uses the real `WslcService`, `ComposeProjectSupervisor`, and
`ComposeNetworkOrchestrator` behind a deny-by-default test lease. It requires an explicitly approved
absolute WSLC executable plus matching SHA-256, an **already loaded immutable 64-hex image ID**,
and an existing absolute evidence directory. The image must provide BusyBox-compatible `sh`,
`sleep`, `touch`, `test`, `rm`, and `nslookup`; supply no credentials or untrusted fixture images.
The host/Windows account's selected WSLC session must be disposable and have no containers.
WSLC has no per-test distro selector here: do not point this at a shared production session.

An explicit consent string is required even to probe the engine. Without it, the runtime fact is
reported skipped. With consent, missing configuration, mismatched binary/image identity, nonempty
container inventory, or unsupported/unknown network connect/disconnect capability fails before
mutation with a diagnostic. The harness never calls pull/build/registry/prune/session-terminate,
publishes ports, binds host paths, changes GPU/special network mode, or deploys/registers the app.

```powershell
# ONLY after obtaining additional runtime permission on a disposable WSLC session:
$env:WCD_COMPOSE_RUNTIME = 'I-authorize-disposable-WSLC-resources'
$env:WCD_RUNTIME_WSLC = 'C:\approved-tools\wslc.exe'
$env:WCD_RUNTIME_WSLC_SHA256 = '<approved-wslc-binary-sha256>'
$env:WCD_RUNTIME_IMAGE_ID = '<already-loaded-64-hex-image-id>'
$env:WCD_RUNTIME_EVIDENCE = 'C:\existing-runtime-evidence'
dotnet test tests\WslContainerDesktop.Tests\WslContainerDesktop.Tests.csproj `
  -c Debug -p:Platform=x64 -f net10.0-windows10.0.26100.0 --no-restore `
  --filter Category=ComposeRuntime --logger 'trx;LogFileName=compose-runtime.trx'
Remove-Item Env:\WCD_COMPOSE_RUNTIME
```

One run owns a GUID project prefix and `com.wsldesktop.conformance-run` label. The lease rejects
name collisions and unapproved calls, enrolls attempted creations for partial-failure recovery,
and checks both exact inspected ID and ownership label before mutation/deletion. Cleanup is
independent of scenario cancellation, bounded per resource, container-first then network, and
never globally prunes. Unknown/changed ownership is preserved and reported as failure, not
silently deleted. Baseline network identities and volume names must remain unchanged and the
post-run container inventory must be empty; concurrent foreign changes fail verification rather
than being cleaned up. A run-specific JSON event/evidence file records version, binary/image
identity, capabilities, selected observations and cleanup diagnostics; raw credentials/configuration
are not captured.

| Runtime scenario | Required evidence before claiming it passes |
|---|---|
| Startup ordering | Real supervisor on reversed service order, all three dependency conditions; actual exit-code check before dependent start and actual exec-health observation after supervisor startup timestamp |
| Network aliases | Actual `nslookup` calls from an owned peer for aliases on both owned networks; unsupported/unknown native capability fails preflight, not downgraded silently |
| Recreation | Native no-op reconciliation retains ID, explicit owned remove/create changes only server ID and preserves client ID; this is not automatic drift-detection certification |
| Supervision | Actual readiness-marker success/failure probes; real service non-explicit start preserves manual-stop suppression and explicit start clears it |
| Cleanup | Cancellation immediately before start exercises real orchestrator rollback; finally cleanup verifies exact IDs/labels and baseline inventories |

The health/status monitor ports are test adapters fed **real CLI observations** synchronously after
the supervisor requests refresh; this does not run the WinUI background poller or application
restart/auto-heal watchdog loops. Full packaged watchdog retries/backoff, automatic drift recreation,
app-close behavior, and per-version runtime certification still require additional permission and
evidence. The integrated scenario has a three-minute cancellation budget and each mutation/cleanup
has a separate bound. Normal offline tests check creation/ownership policy and compile the runtime
suite but must not be described as a runtime pass.

Strict unsupported-input rejection remains a production change for later layers, not a test-harness
fix. The captured negative cases and existing capability-double tests preserve the distinction
between diagnosed safe failure and the importer's documented current gaps. Remaining requirements
are actual authorized hardware/runtime evidence and subsequent production semantic fixes, not
missing config-reference files.
