# Local Compose service scaling

Scaling is desktop-managed local orchestration, not Swarm, a native WSLC Compose command,
or a new daemon. It extends the [shared reconciliation plan](COMPOSE-RECONCILIATION.md).

## Configuration and precedence

The imported service supports `scale: N` and `deploy: { replicas: N }`. Omission means one;
zero means no desired instances. Counts must be nonnegative 32-bit integers. If both keys are
present they must agree after interpolation, includes, extends and override merging. Invalid
input rejects the entire import before persistence or supervision. `deploy.mode: replicated`
is accepted; global, replicated-job and global-job modes are rejected. Other Swarm deployment
fields are not implemented.

```yaml
name: example
services:
  worker:
    image: example.invalid/worker:1
    scale: 3
    restart: unless-stopped
```

This is a syntax example only; the synthetic image is not intended to be pulled.

Effective counts use operation-request overrides first, saved UI overrides second, and imported
service defaults last. Request overrides are ephemeral. The UI optionally saves operator counts
after successful read-only preview and confirmation, before final apply preparation. Saved intent
is separate from applied instance snapshots and survives later apply failure.
Partial execution can therefore leave desired and actual counts different:
retry reconciles the remaining difference, not a fabricated all-or-nothing result. Reimport
preserves saved operator counts for still-defined services. Restart uses applied configuration,
not pending scale changes. A selected lifecycle plan is limited to **1,024 instance entries**,
including retained and surplus entries. This bounds expansion before allocation; it is a local
planning limit, not a parser count limit.

## Identity and preservation

Instance one retains the existing `project_service` name (or explicit `container_name`).
Additional instances use `project_service_2`, `project_service_3`, and so on. This intentional
legacy compatibility differs from Docker Compose's name separator and numbering convention.
Project/service ownership labels remain unchanged; `com.wsldesktop.instance` identifies the
one-based instance index. Legacy missing instance labels are accepted only for instance one.
Labels alone never authorize mutation: ownership and canonical observed container ID are
rechecked at execution.

Applied snapshots remain keyed by the original service name for instance one; additional keys
are `service#2`, `service#3`, etc. Counts are not part of a retained instance's runtime
fingerprint. Scaling up creates missing instances without reconnecting or replacing unchanged
ones; scaling down removes surplus instances and retains lower indices. Named volumes and bind
mounts are shared as declared, not cloned. New anonymous volumes belong to each new instance;
recreation preserves the inspected mounts of that particular instance. Scale-down does not
delete shared volumes.

Every new instance uses the service's required networks with both the shared service alias and
its unique container-name alias, plus declared aliases. An unchanged legacy first instance is not
recreated solely to retrofit its ordinal label or unique alias; missing configuration fingerprints
still use #85's explicit-up migration. Shared service aliases are discovery,
not a promised load balancer, traffic policy or Swarm VIP. Fixed published host ports, explicit
container names, static endpoint addresses and unsupported namespace-sharing modes cannot
be multiplied safely; incompatible configurations fail preflight before destructive work.
The supported multi-replica port subset is container-only port declarations; published mappings,
including `published: 0` and published ranges, are conservatively rejected rather than assuming
WSLC allocation behavior.
Capability detection remains tri-state; Unsupported multi-network capability allows the existing
primary-only fallback with a warning. Unknown blocks rather than guessing, and failed native
mutations are never retried with a different backend.

## Dependencies, supervision and failures

Readiness is evaluated for each required dependency instance. Healthy dependencies must all be
healthy for their exact observed IDs. Completed-successfully dependencies must all have exited
zero; a failed or unknown result does not release a dependent. A required zero-instance
dependency cannot satisfy a startup gate. Existing unchanged running dependents are not torn
down merely because a dependency update or a new replica fails.

Each successfully adopted or applied instance is enrolled independently in health and restart
supervision. Persisted manual-stop intent suppresses automatic restart across app re-adoption.
Explicit apply/restart resumes only its intended instances; a newer manual-stop intent wins.
Confirmed successful instances remain managed after a later network attachment failure or
cancellation. Cleanup is limited to confirmed owned instances; uncertain or failed cleanup is
reported rather than claimed as a successful scale-down. Lifecycle results expose `IsCancelled`,
which always makes `AllSucceeded` false, even with no completed outcomes. Unattempted entries
do not count as started or successful. Cancellation while waiting for the lifecycle gate throws
without releasing another operation's gate; cancellation during execution returns partial
per-instance outcomes. Assistant/template and dev-container callers report cancellation rather
than claiming deployment.

## Reference and deliberate differences

The pinned specification's
[`scale`](https://github.com/compose-spec/compose-spec/blob/c0c3dba71a73260cf9649e05370dcad6fe29e11c/05-services.md#scale)
requires agreement with `deploy.replicas`. The pinned Docker Compose **v2.39.4**
[`convergence.go`](https://github.com/docker/compose/blob/v2.39.4/pkg/compose/convergence.go)
rejects explicit container names above one replica and checks all instances for health.
Its `isServiceCompleted` returns the first exited container's result; this app deliberately
requires **all** dependency replicas to complete successfully, avoiding premature release
when another instance is still running or fails.

These are source-derived behavior comparisons and offline fake-engine tests, not captured CLI
output or certification against live WSLC. There is no Swarm placement, scheduling, replicated
jobs, rolling-update/rollback policy, ingress routing, service VIP, autonomous replica replacement
daemon or atomic project-wide rollback. Supervision requires the desktop application to remain
open. The [strict parser/file-graph subset](COMPOSE-PARSER.md) remains unchanged.

## Integration contract

Consumers use `ComposeProjectSupervisor.PlanAsync` and the existing `ComposeOperationRequest`,
not an independent replica planner. `Replicas` supplies request overrides. Each
`ComposeServicePlan` exposes the logical `Service`, `InstanceIndex`, `InstanceKey`,
`DesiredReplicas`, optional `StorageWarning`, canonical observed `ContainerId`, fingerprint,
image decision and lifecycle action. `ComposeServiceResult` exposes the same logical service
and instance identity, action, actual result ID, detail and warning. `ComposeUpResult.Plan`
describes the executed plan; `IsCancelled`, `AllSucceeded`, `Started` and per-instance outcomes
must be interpreted together. `Started` excludes kept instances.

`ComposeAppliedService.InstanceIndex` and the applied dictionary key are persisted;
the computed applied `InstanceKey` is not. Internal `ComposeService.RuntimeInstanceIndex` is
nonserialized trusted expansion metadata. Imported instance labels cannot select an ordinal.
Read-only status counting does not expand desired counts or authorize mutation, so an oversized
configuration remains visible and editable even when lifecycle planning rejects its size.
