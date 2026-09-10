# Compose compatibility review (#87)

This is a read-only review of the existing local Compose planner, not a second planner or
Docker/WSLC runtime certification. The parser, file graph, reconciliation and scaling contracts
remain in [parser](COMPOSE-PARSER.md), [reconciliation](COMPOSE-RECONCILIATION.md) and
[scaling](COMPOSE-SCALING.md). No new package, engine minimum or Compose daemon is added.

## What the review shows

`ComposeCompatibilityPreview` contains display-only `ComposeCompatibilitySetting` rows:
logical service, setting, effective value, disposition, explanation, source, and tri-state
capability evidence where relevant. The shared plan selects active services and dependency closure
and expands replicas. Rows include instance actions/names, image work, profiles and dependencies,
mounts, published ports, networks/aliases/addresses, CPU/memory/shared-memory/GPU limits, process
override presence, DNS/hostname, tmpfs/ulimits/stop settings, file-backed secrets/configs,
health ownership and app restart supervision.
Selected resource declarations also show external ownership, driver, subnet/gateway/IP range,
and counts of private driver options/labels. Existing resources are retained rather than
silently recreated to apply creation-only options.

| Disposition | Meaning |
|---|---|
| Supported | Applied through the selected backend, or a documented existing/no-op action |
| Approximated | Local/app-managed behavior or a conditional/destructive action needing attention |
| Ignored | Retained desired setting that will not be applied by this backend |
| Blocked | Incompatible, unresolved or unavailable evidence; no “ignore and continue” path |

Native multi-network support selects create/connect/start. `Unsupported` permits the documented
primary-network-only legacy run, with secondary endpoints explicitly ignored and desired endpoints
retained. `Unknown` is never treated as Unsupported. Failed native mutation never falls back.
Health rows distinguish engine probes from application probes; auto-heal and restart remain
application-owned and require the desktop to stay open. Native health is not a native restart policy.
Restart/stop/down review uses applied configuration, not pending desired edits; deployment-only
import warnings do not block stopping existing workloads.

Recreate/remove rows warn about loss of the writable container layer, retained mounted volumes
and absence of project-wide rollback. A pull can resolve to recreation only after obtaining an
immutable image ID; that conditional work is shown before approval.

Missing external resources, incompatible ownership/drivers, unusable inventory, unsafe storage,
invalid configuration and unresolved-variable warnings block. A resource row may describe the
existing retained resource while a separate blocker explains why it cannot satisfy the request.
Settings dropped by the parser are represented by its preserved warnings, not guessed values.
Warnings now survive snapshot/save/reload, including explicit import-only saves. Older saved
projects without diagnostics require reimport to recover them.

Source locations are honest: importer diagnostics retain their logical breadcrumb/line when
available. Ordinary resolved-model rows identify their model key and explicitly state that the
original line is unavailable. No new source map is fabricated.

### Published bindings

Preflight compares selected instance bindings and observed running WSLC containers, preserving
protocol and host-address distinctions. A kept instance does not conflict with its own observed
ID. Another running instance remains a conflict even if scheduled for removal: removal ordering
does not guarantee release before startup. Stop it and review again. Unknown port metadata fails
closed when a published binding needs validation. Old/manual published-range strings must be
expanded to numeric mappings; the importer already normalizes supported range syntax. Host-side
processes outside WSLC are not inspected. Multi-replica published ports retain #84's stricter
unsupported policy, including published zero.

## Approval API

The API is in-process and supervisor-instance scoped:

```csharp
var token = await supervisor.PrepareReviewAsync(project, request, cancellationToken);
// Only token.Preview (and optional token.ExpiresAt) belongs in presentation/audit.
var confirmed = await presenter.ConfirmAsync(token.Preview, cancellationToken);
var outcome = await supervisor.ApplyReviewedAsync(
    token, confirmed, saveReplicaOverrides: false, ct: cancellationToken);
```

### Assistant integration (#94)

`deploy_compose` (`yaml`, optional `projectName`) and Compose `deploy_template`
(`idOrName`) prepare this same review during tool resolution. There is no second planner,
provider-visible executable token, confirmation argument, or `UpAsync` call after assistant
approval. `ContainerAssistantService` requires **one explicit approval** of the complete
redacted consequences, even if an older saved per-tool preference says auto-approve.
The permission settings no longer offer Compose auto-approval; template auto-approval
applies only to single-container templates.

`AssistantResolvedToolCall.RequiresExplicitApproval`, `BlockedResult`, and `DeclineAsync`
are trusted application hooks, not model arguments. Blocked preparation returns safe
diagnostics without presenting an approval that could bypass them. Rejection, cancellation,
reset and late gate failures retire the local token. Execution calls `ApplyReviewedAsync`
on that exact token; the supervisor revalidates under its lifecycle gate. Template content,
identity and defaults are also compared with the captured catalog snapshot before apply.
The same importer re-resolves the original YAML and its file/interpolation graph before apply;
changed resolved input is stale and newly unreadable input is blocked. This check never
replaces the approved snapshot with new values. It is not an atomic filesystem lock or a
recursive build-context/content watch; shared storage/image preparation guarantees still apply.
Neither parsing nor review saves project/template defaults. The assistant never persists
new template defaults; supervisor applied-state persistence remains authoritative.

The complete display must fit the shared 12,000-character assistant evidence budget.
Oversized consequences fail closed and direct the user to the Compose page for a full
review rather than authorizing a truncated preview. The shared 1,024-selected-instance
planner cap is unchanged; the assistant display budget can impose a smaller practical limit.

Provider results are JSON with `status`, `kind` (the `ComposeReviewOutcomeKind` name),
`allSucceeded` (true **only** for Applied), `message`, safe `blockers`, `outcomes`,
`retainedResources`, and `retentionNotice`. Each outcome carries `instance`
(the shared `InstanceKey`, e.g. `web` / `web#2`), service/index, action, status, safe detail
and warning. Status distinguishes started, reused, skipped, failed and cancelled; scaling
removals are reported as removed, not started. The public shared service result adds
`Outcome` to distinguish an absent no-op or cancellation from reuse/dependency failure.

`ComposeReviewOutcome.RetainedResources` contains safe kind/name/state/detail records:
selected networks and named volumes retained after success, mounts not removed by the
operation, and **unverified** resource/container candidates after partial execution.
These are not claims of live inventory certification. Refresh before cleanup; no automatic
destructive retry, volume deletion, or project-wide rollback is implied. When evidence is
bounded, the central sanitizer preserves kind/success metadata and reports `omittedOutcomes`
and `omittedResources` rather than implying the remaining list is complete.

Only safe public outcomes cross the assistant boundary; internal `Execution`, `ToUpResult`,
raw engine diagnostics, token snapshots and comparison evidence never do. The central
`AiTextSanitizer.SanitizeMessage` withholds generated Compose YAML from echoed arguments,
including arbitrary environment/build values and YAML aliases, while preserving call IDs
and tool names. The original YAML/options remain only in the validated execution path;
the shared projection masks known private configuration in preview/result copies.

1. `PrepareReviewAsync` snapshots desired configuration and request service/count collections.
   Under the lifecycle gate it invalidates capability cache, resolves the shared plan, reads saved
   state, capabilities, inventory, selected resources, local images and preservation evidence,
   and creates an opaque `ComposeReviewToken`. It performs no pull/build, file staging, resource
   provisioning, container lifecycle, project save or supervision enrollment. Cancellation throws
   without releasing another caller's gate.
2. The token exposes only `Preview` and immutable `ExpiresAt`. Its constructor, owner, request,
   raw plan, snapshots, comparison hash and consumption state are not public. Do not serialize a
   token for later authorization, construct one from provider text, or accept an edited preview
   as approval evidence. Display rows cannot edit/bypass the stored request.
3. **Expiry:** exactly ten minutes after preparation completes. Validation and application check
   expiry again after potentially slow evidence reads. No rolling extension. A new supervisor/app
   session requires a fresh token even before expiry.
4. `ValidateReviewAsync` is optional, read-only and **non-consuming**. Results are `Valid`,
   `Blocked`, `Stale`, `Cancelled`, `AlreadyUsed`, `ForeignToken`, or `Expired`. Valid is advisory,
   never authorization: apply independently checks again.
5. `ApplyReviewedAsync` atomically consumes a token from this supervisor before accepting or
   declining it. Even refusal, cancellation, expiry, blocker or stale evidence makes it unusable.
   A foreign supervisor returns `ForeignToken` without consuming the original owner's token.
   Concurrent attempts authorize at most one apply.
6. Confirmed apply reacquires the lifecycle gate, invalidates capabilities and recomputes the
   shared plan plus evidence. Newly unusable evidence is `Blocked`; changed usable evidence is
   `Stale`. Either requires a new review. Evidence includes saved project/intent, requested
   selection/counts, resolved plan and image decisions/IDs, capability support/executable/version,
   observed inventory, selected resource inspections and preserved anonymous-volume identities.
   Unrelated inventory changes may conservatively invalidate a review. Displayed values and
   timestamps alone cannot authorize execution.
7. Only after validation may `saveReplicaOverrides: true` persist Up request counts. Desired
   unsupported settings remain intact. Execution uses the freshly reviewed selection/backend;
   pulls/builds are the explicitly reviewed conditional image work. Existing identity/ownership
   checks still guard individual operations. The app gate cannot atomically lock external WSLC
   clients, host files or image registries. Build-context and ordinary bind-file contents are not
   recursively watched; explicitly rebuild for changes.
8. `ComposeReviewOutcome.Kind` is `Applied`, `PartialFailure`, `Blocked`, `Stale`, `Cancelled`,
   `AlreadyUsed`, `ForeignToken`, or `Expired`. Only `Applied` makes `AllSucceeded` true.
   `Message` and per-instance `Services` are safe summaries, not raw engine diagnostics; IDs are
   omitted and action/success/instance indices retained. Cancellation/failure after work starts
   may leave partial resources/workloads and explicitly saved desired counts. Refresh actual
   state; do not report rollback, infer an empty inventory, retry the token, or automatically
   select a legacy backend. Unexpected exceptions produce a value-free warning and explicit
   partial/uncertain-state notification, not a success-shaped fallback.

#94 should retain tokens in trusted local approval state and pass only the safe projection to
its approval UI/provider/audit. It should call this API directly rather than adding an independent
planner or calling `UpAsync` after approval (which would prepare/show another review). Per-tool
auto-approval is not authority to ignore blockers. This change does **not** implement #94's full
AI approval workflow, durable token transport, provider changes or a new audit subsystem.

## UI and caller integration

- Compose import → Up and existing-project Up use `UpAsync`, whose injected
  `IComposeReviewPresenter` shows the resolved dialog before `ApplyReviewedAsync`. Import-only
  is a separate explicit save choice; cancelling deployment review does not perform that save.
- The service-selection/replica operation dialog uses Prepare/Apply directly, including optional
  saved counts. Its blocked primary button cannot be enabled by acknowledging warnings.
- Template Launch and edited Compose-template launch use the same path. Cancel/failure does not
  update template defaults or claim successful launch.
- Existing assistant Compose/template tools also encounter the review presenter, regardless of
  their prior generic tool confirmation. Their Compose summaries are context-redacted.
- Compose-backed dev-container start/rebuild resolves a cloned project and build flags before
  review. Dynamic features and host `initializeCommand` are explicitly blocked because they can
  alter the reviewed inputs; they are neither executed before approval nor silently skipped.
  Use externally prepared inputs without those declarations. Single-container dev-container
  behavior is unchanged. This is an intentional restriction of the formerly supported
  Compose-backed feature/initialize path: initialize commands can mutate arbitrary host inputs,
  and feature resolution can download content/build a derived image before its resolved image
  exists. Approving that preparation first would no longer make cancellation of the subsequent
  resolved review mutation-free. A separate, explicitly approved preparation workflow is not
  implemented here. Ordinary Compose image/build inputs remain supported.
- `ComposePreviewDialog` uses immutable one-time bindings, accessible named rows/buttons and
  Cancel as the default. Missing/busy UI fails closed. Cancellation completes even while a
  dispatched dialog callback is pending.

## Privacy and verification

`ComposePreviewProjection` wraps existing `AiTextSanitizer.Redact`; it does not change that shared
helper. Environment/build-argument values, custom labels, user/process/probe values, file-secret
source paths, URI credentials and Windows user path segments are withheld/context-masked.
No raw YAML, source-file contents, private evidence hash or execution object is exposed on the
public review handle/outcome. Arbitrary engine exception text is not an audit field and is never
logged by the review failure handler. The raw `PlanAsync` contract and internal execution state
remain trusted orchestration inputs, **not provider/audit payloads**.

Offline fake-engine tests cover native/legacy/Unknown backends and health, active selection and
replicas, invalid/unresolved inputs, resource/port conflicts, recreation and mount drift, capability/
image/inventory/settings drift, immutable inputs, cancellation, expiry, single use, foreign tokens,
no-presenter behavior, failure privacy and immutable VM disposition/source bindings.
Validation uses the existing x64 test targets and full x64 application build. No packaged launch,
deployment, live workload, Docker reference capture or manual UI smoke result is implied.
