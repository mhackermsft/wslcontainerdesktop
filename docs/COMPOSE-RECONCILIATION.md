# Compose reconciliation and targeted lifecycle

## Reference and initial behavior inventory

The target is the same Compose specification commit
`c0c3dba71a73260cf9649e05370dcad6fe29e11c` and Docker Compose CLI **v2.39.4**
used by the [configuration corpus](COMPOSE-CONFORMANCE.md). In particular,
[up](https://github.com/docker/compose/blob/v2.39.4/docs/reference/compose_up.md)
reuses running services and recreates changed configuration/images while retaining mounted
volumes, whereas
[restart](https://github.com/docker/compose/blob/v2.39.4/docs/reference/compose_restart.md)
does not apply edited configuration. The specification's `depends_on` long form supplies
`required`, `restart`, and readiness conditions. These are reference-derived expectations,
not captured CLI output or certification against a live WSLC workload.

| Area | Before #85 | Implemented behavior |
|---|---|---|
| Repeated up | Stops/removes every selected existing container and builds every build section | Fingerprint and observed identity comparison; keep unchanged running instances, start unchanged stopped instances, selectively recreate changed ones |
| Restart | Whole-project down followed by up, including resource removal and builds | Stop/start existing instances with their applied configuration; no creation, image acquisition, or shared resource deletion |
| Selection | Whole project with active-profile filtering during startup | Shared deterministic selection/order; targeted apply includes dependencies, targeted stop/remove excludes them, restart follows explicit restart edges |
| Completed dependency | Logs failed completion then starts the dependent anyway | Required completion failure blocks startup; an unchanged successful one-shot dependency need not rerun |
| Preflight | Per-service network checks could happen after earlier services were replaced | Complete selected graph, ownership, capability/health, image/build, file staging and storage preparation before the first destructive operation |
| Applied state | Desired project was also assumed to describe every running service | Per-instance applied snapshots survive reimport and partial apply; successful services are committed and enrolled individually |

## One shared plan, not a separate preview implementation

`ComposeOperationRequest`, `ComposeServicePlan`, and `ComposeReconciliationPlan` are the contract.
`ComposeReconciliationPlanner` owns service selection, dependency ordering, names, normalized
fingerprints, and observed-container classification. The supervisor's `PlanAsync` is read-only:
it obtains real inventory/inspect and local image evidence, then uses that same planner and image
decision logic as execution. A planned pull is explicitly conditional: its resulting image ID is
not known until it completes. Preview does not pull, build, stage files, create resources, persist
projects, or start/stop containers. Apply always replans; a previously displayed plan is not a
permission to act on a stale container ID.

Classifications are **unchanged**, **changed**, **missing**, and **incompatible**. Actions distinguish
keep/start/create/recreate/restart/stop/remove/blocked, with value-free reasons and per-service
outcomes. Image work is separately identified as none/build/pull. A blocked preflight does not
claim success for services that were not executed. `Started` excludes kept containers.

The instance identity remains the existing project/service ownership labels and deterministic
`project_service` name (or explicit `container_name`). No replica implementation is introduced.
The centralized name resolver and per-instance plan/observed-ID fields are the extension point
for scaling; compatibility and assistant previews must consume this contract rather than
implement another planner.

Runtime fingerprints are versioned SHA-256 digests of normalized effective options. Mapping
ordering does not create changes; ordered process arguments and meaningful network priority
remain ordered. Source-owned secret/config references include file-content digests, not transient
staging paths. Neither fingerprints nor explanations contain plaintext environment or file values.
App-owned restart settings, profiles and dependency metadata do not require container recreation;
successful apply updates supervision. Bind contents and complete build directories are not watched
or recursively hashed. Use explicit rebuild after editing build-context contents.
Namespace-sharing consumers are recreated when their selected namespace provider is replaced;
stop/start cannot move an existing container into a new provider's namespace.

Containers get configuration and image-identity labels. Labels are comparison evidence, **not**
an authorization mechanism: exact project/service ownership and the observed ID are checked
again before execution. Foreign containers and unusable inspect data fail closed. Owned legacy
containers without a configuration fingerprint undergo a one-time explicit-up migration/recreation,
not a guessed equivalence comparison. A container-name change with a known old instance requires
explicit removal of that instance before apply; it does not silently create a duplicate.

## Selection and readiness

- Whole-project apply selects unprofiled and active-profile services; `*` enables all profiles.
  Explicit targets activate their own profiles and include the eligible required dependency closure,
  not unrelated siblings sharing a profile. Missing or inactive required dependencies fail preflight.
- Optional (`required: false`) unavailable dependencies do not prevent startup. Active optional
  dependencies can participate in ordering/readiness without making their failure fatal.
- Required healthy dependencies use the existing status monitor/watchdog evidence for the exact
  container ID. No independent health command poller is introduced. Required successful-completion
  dependencies must exit zero; unknown exit status, replacement or failure does not count as success.
- Restart targets existing instances and follows reverse `depends_on.restart: true` edges.
  Ordinary dependencies do not cause unrelated services to restart. Readiness applies between
  selected restarted dependencies and their dependents. Restart never creates a missing service.
- Targeted stop/remove affects selected instances only, in reverse dependency order. It can leave
  a dependent running when its dependency is explicitly stopped, like a targeted operator action.
  It never deletes shared project networks or volumes. Whole-project down also handles known applied
  instances excluded by current profiles or removed from the desired service list.

An already-running unchanged service is not stopped or failed merely because its dependency's
update fails. Gates protect actual startup/recreation, not continued operation of unaffected peers.
Graph cycles, duplicate identities and invalid targets fail instead of falling back to arbitrary
startup order.

## Image, storage and failure behavior

The default image policy is local-first `missing`; supported policies also include `always`,
`never`, and `build`. Existing local image IDs are compared with applied image labels. Ordinary
unchanged up does not rebuild or pull. A missing build image, changed build configuration, explicit
rebuild, or build policy prepares an image before replacement (`always`/`never` do not implicitly
build; explicit rebuild takes precedence). `always` pulls before deciding
whether the image identity requires recreation; a failed pull/build never falls through to
destructive teardown. `never` requires a usable local image. Build-context content edits need
explicit rebuild, and registry refresh intervals/full Docker pull-policy behavior are not emulated.
Creation uses the resolved immutable image ID, not a tag that can move during preflight.

Only resources referenced by the selected apply graph are provisioned. Declared external resources
must exist. Errors are not disguised as empty inventory or an already-existing resource. Existing
named volumes are retained, and anonymous/image volume attachments are replayed by inspected
volume identity when recreating. Missing/incomplete mount metadata blocks recreation rather than
silently replacing storage. Explicit new mount sources override old attachments. No operation
automatically renews anonymous volumes. Whole-project down preserves volumes; volume deletion
additionally requires verified project ownership and never deletes external volumes.

Files are validated and staged at fresh MSIX-safe paths before teardown, without overwriting files
mounted by an old container. A staging failure is fatal, not an in-place-bind fallback. The existing
bounded bind-mount race probe remains best effort when the engine cannot run the probe; a confirmed
directory race is retried before teardown, then fails. Native multi-network create/connect/start
and rollback remain capability-gated: Unsupported allows the documented first-network fallback,
Unknown fails with a diagnostic, and a failed native mutation is never retried through legacy run.

Each successful apply persists its applied service snapshot and enrolls supervision immediately.
An untouched sibling retains its old supervision after later failure/cancellation. Failed
replacement stop/remove restores suspended supervision. Explicit targeted stop/remove instead
persists stop intent before issuing the engine request and leaves the target unenrolled even when
cancellation makes the remote outcome uncertain. That intent survives app re-adoption, including
for an `always` policy, until explicit apply/restart. A manual stop arriving after an operation began wins
over that operation's earlier resume token. Failed/cancelled legacy creation is cleaned up only
when a unique per-operation label proves ownership; cleanup failure is surfaced.

Startup re-adoption uses the last applied snapshot rather than pending desired edits. It does not
clear all project policies before inspecting individual instances, replace containers, clear stop
intent, or apply edited service options. Multi-network repair retains its existing compatibility
and rollback checks. App-owned restart/health enforcement still requires the desktop to be running.

## Dev Container lifecycle integration

Compose-backed Dev Containers dispatch container hooks from the primary service's successful
action and returned container ID, not from project-wide success or a reusable name. Create and
recreate schedule `onCreateCommand`, `updateContentCommand`, `postCreateCommand`, and
`postStartCommand`; start/restart schedule only `postStartCommand`. An ordinary unchanged keep
does not run any of these hooks. A successful primary is initialized even if a sibling fails,
and the overall failure is still reported.

Pending commands (including their remote working directory/environment) are snapshotted in the
existing `devcontainers.json` record by container identity immediately after the primary's
successful action, before a later sibling can interrupt the apply. Each acknowledged command is removed
and saved before the next command runs. Reimport retains this progress. An explicit retry on a
kept container resumes only previously scheduled failed/unattempted commands; it does not replay
completed creation commands or schedule edited hooks. A new container identity starts a fresh
creation sequence. Host `initializeCommand`, terminal `postAttachCommand`, and the non-Compose
single-container replacement path keep their existing behavior.

State writes are atomic and failures are surfaced. This is not a transaction with arbitrary shell
side effects: a command that fails/cancels after making changes, or succeeds just before a crash
or failed checkpoint write, may need to run again. Such commands should be retry-safe; their errors
are not hidden. Cancellation between acknowledged hook commands preserves the saved remainder.

## Intentional limits

This is detached desktop orchestration, not the Docker Compose daemon/CLI. It does not implement
attached-log exit semantics, `--abort-on-*`, all `up` flags, automatic orphan deletion, filesystem
watching, registry polling, replica scaling, or atomic project-wide rollback. A failure after
destructive replacement cannot resurrect the old container; successfully completed and untouched
services remain managed, and retry applies only remaining differences. Previously created but
unused preparation resources may remain after a failure. Existing resource declarations are not
destructively reconfigured in place. Legacy saved projects without applied snapshots cannot
recover configuration history that was never recorded.

The strict [parser/file-graph contract](COMPOSE-PARSER.md) is unchanged: the entire local graph
must parse successfully before persistence, comparison, or supervision. There is no global parser
cache, no external-resource import through `extends`, and no new remote graph or dotenv claims.
