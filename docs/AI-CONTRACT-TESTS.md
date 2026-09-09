# Assistant contract test foundation

Issue #95 establishes deterministic coverage for existing orchestration and HTTP
adapter contracts. It is a foundation, **not completion of all #95 acceptance
criteria**. The feature layers below must add their own regression coverage as
their contracts become available. Issue #97 extends this foundation with real
argument resolution, approval-bound container plans, and partial-execution tests.

## Running the deterministic suite

From the repository root on Windows with the .NET 10 SDK:

```powershell
dotnet test tests\WslContainerDesktop.Tests\WslContainerDesktop.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~AssistantOrchestrationContractTests|FullyQualifiedName~AiProviderContractTests"

# Real toolset and orchestration together (Windows source-linked target):
dotnet test tests\WslContainerDesktop.Tests\WslContainerDesktop.Tests.csproj -c Debug -p:Platform=x64 -f net10.0-windows10.0.26100.0 --no-restore --filter "FullyQualifiedName~AssistantToolsetContractTests|FullyQualifiedName~AssistantOrchestrationContractTests"
```

Use the repository's existing xUnit runner. If assets are missing, first audit
publication dates for the exact restore graph under the seven-day dependency-age
policy, then restore. No additional test packages are needed. Source links compile
the actual service and adapter implementations without loading the WinUI
executable or activating its MSIX package. Tests run for both configured target
frameworks, except real toolset tests run only on the Windows target already used
by the real Compose supervisor. They require no credentials, server, WSL engine, running workload,
model, or model download.

## Reusable boundaries and fixtures

`IAssistantToolset` exposes the existing `GetDefinitionsAsync(CancellationToken)`
and `ResolveAsync(AiToolCall, CancellationToken)` contracts. The production
`AssistantToolset` implements it; DI resolves the same singleton instance. The
unchanged `AssistantResolvedToolCall` record lives in its own source file.
`AiHttpClient(HttpMessageHandler)` enables in-memory transport while preserving
the normal five-minute timeout and default constructor.

`AiContractHarness` supplies strict settings via the existing `NetworkTestProxy`,
scripted provider turns with captured history, scripted resolution and execution,
per-tool auto-approval, recorded activity and its serialized representation,
synthetic credentials, and a queue-based HTTP handler that captures requests
before disposal. Unconfigured calls throw; there is no network fallback.
Task-completion signals control approval and cancellation timing without sleeps.
Timeouts in tests are deadlock guards, not scheduling assumptions.
An optional toolset factory allows the same harness to run the actual
`AssistantToolset` instead of scripted resolution/execution. Its inventory and
service interfaces use strict `NetworkTestProxy` fakes. Source-linked template,
registry and Kubernetes dependencies compile their real contracts; no real
registry, credential store, process runner, Compose deployment, or provider is invoked.

The real `ContainerAssistantService` and `AssistantActionGate` are exercised for:

- Default approval and risk classification for every mutation category, exact
  request details, unknown approval IDs, duplicate approval, rejection and late
  approval after rejection.
- Read-only access, named per-tool auto-approval, and isolation from other tools
  in the same category.
- Cancellation/reset while waiting for approval; no subsequent execution.
- Resolver and execution failures without automatic retry, and a completed
  mutation followed by a provider failure without replay on the next turn.
- Sequential text history, snapshot list isolation, completed-conversation reset,
  and disabled AI.

The same HTTP test scenarios run against **OpenAI-compatible, Azure OpenAI, and
Ollama** adapters: multiple calls/results in one turn, schemas, arguments, wire
identifiers, routing/authentication, typed HTTP failures, redacted/bounded failure
details, cancellation during transport, failure after a completed action,
stopping remaining calls after a tool failure, malformed response JSON, the
eight-iteration limit, and diagnosis JSON serialization/parsing. OpenAI also has
keyless endpoint and URI normalization cases. Synthetic credential values are
asserted absent from request bodies and URLs, not from required auth headers.

## Exact action plan regressions (#97)

`AssistantToolsetContractTests` exercises the production resolver and executor:

- Malformed/empty/non-object JSON, unknown and duplicate fields, required
  nonblank strings, nulls and wrong types fail with `InvalidOperationException`
  before inventory or mutation. Run-option string arrays reject non-string and
  blank entries rather than silently dropping them; all supported run fields
  are checked for faithful capture and execution. Environment/label entries
  require unique nonblank keys and `KEY=VALUE` syntax; CPU limits must be
  positive decimals. Empty namespaces are accepted only for `delete_resource`.
- Bulk intent requires `scope:"all"` with no filters, or nonblank name filters
  without scope. Empty objects, invalid scope, blank filters and non-boolean
  `onlyRunning` fail closed. Log-tail bounds (1–1000) and replica bounds (0–100)
  are tested with valid endpoints and invalid numeric/types.
- Exact IDs, safe unique hexadecimal ID prefixes (at least 12 characters), or
  unique names resolve to captured immutable IDs; approval details include
  names and IDs. Short/ambiguous prefixes and ID/name collisions across
  different containers are rejected. Prefixes are resolved only before approval;
  revalidation and mutation use the frozen inventory ID. Mutable inventory objects do
  not mutate the approved identity snapshot (name/image/creation/known flag).
  Missing, replaced, ambiguous or changed targets are skipped.
- Bulk execution re-lists immediately before every target. New matching
  containers are ignored; running-only targets that stop are skipped. Explicit
  removal including stopped containers remains supported. An empty snapshot
  does not expand after approval.
- Normal per-target command failures continue without retry and report honest
  failed/partial results. Inventory failures stop later mutations and preserve
  completed and unattempted evidence. Structured JSON reports status and outcomes.
- Cancellation before the first mutation throws; cancellation after partial
  execution retains succeeded and not-run outcomes. Cancellation during a
  mutation reports unknown rather than falsely claiming rollback or failure,
  including cancellation originating independently of the caller token.
- Definition probing propagates caller cancellation; invalid Compose with no
  services fails before approval, including when the tool is auto-approved.
- Real-toolset approval, rejection, late approval, cancellation/reset, and
  auto-approved malformed calls run through the shared orchestration harness.

These tests are deterministic safety-boundary tests, not an atomic transaction
guarantee. Inventory can still change between the final check and the engine
call. Uncertain outcomes require inspection and fresh approval, not a retry.

## Deliberate gaps and feature-layer acceptance

These are **unmet criteria**, not skipped tests or assertions that unsafe
behavior is desirable. Scripted resolver tests remain orchestration-boundary
coverage; the separate real-toolset suite above establishes argument validation
and inventory safety.
Serialized activity capture is a test sink, not the production on-disk store.

| Layer | Regression coverage still required |
| --- | --- |
| #91 privacy | Outbound inspect/log/environment/YAML/tool/error data and persisted activity redaction, original execution values, nested structures and truncation boundaries. Existing exception-detail redaction and synthetic auth transport tests do not establish a universal privacy boundary. |
| #96 history | Structured multi-turn tool evidence, call/result pairing, provider isolation/switches, in-flight model/endpoint/configuration snapshots, failed-turn retention policy, late completions and stale approvals after reset, overlapping turns, context budgets and truncation. Current coverage only proves sequential text history and reset while approval is pending. |
| #88 capabilities | Independent Unknown/chat/tool/JSON/streaming/context support, configuration-keyed observations, unsupported JSON, chat-only models, loading versus failures. A diagnosis JSON-mode serialization test is not capability negotiation. |
| #89 streaming | Fragment assembly, complete validation before action, progress ordering, disconnect recovery, inference versus approval timeouts, cancellation/reset generations, partial outcomes and no replay. Current adapters return final strings. |
| #90 runtime ownership | Fake inventory/process-backed local setup: ownership/name collisions, GPU versus other failures, safe fallback, partial creation, racing replacement, cancellation, cleanup failures and retained model data. No runtime lifecycle is invoked here. |
| #92 Foundry Local | Deterministic dedicated adapter/runtime tests using the shared contracts, plus explicitly opted-in packaged and hardware runs. No Foundry dependency or model is acquired by this foundation. |
| Copilot SDK adapter | Provider-specific SDK tool/history/error behavior needs a transport/session seam; the shared HTTP tests do not exercise the SDK or sign-in. |

Do not treat the absence of automatic retries in these scenarios as general
exactly-once execution or duplicate-model-call detection. Those guarantees need
explicit production contracts and additional tests.

## Opt-in real-provider and runtime smoke coverage

There is intentionally no automatically executable smoke test or implicit
runtime setup in the normal suite. Real-provider compatibility remains unverified
until a separately authorized run records its results.

Prerequisites for a manual smoke run:

1. Explicit permission to contact the chosen provider and, separately, to deploy
   the packaged app or change disposable workloads. Coordinate shared MSIX
   registration across worktrees.
2. A deliberately configured endpoint and model/deployment ID; a suitable tool
   and structured-output capability observation, not just successful connectivity.
   Use a disposable synthetic scenario. Never store real credentials or workload
   evidence in fixtures, logs, or PRs.
3. For local hardware, Windows 11 x64, a preinstalled runtime and explicitly
   acquired/cached licensed model. Before acquiring anything, audit pinned SDK,
   transitive/native dependencies, execution providers and model artifacts under
   the seven-day policy. Missing assets must stop the test, not trigger downloads.
4. For Foundry Local, record the actual endpoint and loaded model ID, runtime
   version, hardware, package identity and asset versions. No fixed port or
   universal GPU/NPU assumption. Test cold/warm starts, offline cached inference,
   missing assets, cancellation, unload, and native loading in the signed x64
   MSIX only when that integration is available.

Record tested capabilities, expected approvals and actual mutations, partial
outcomes, cancellation behavior and retained model data. Keep runtime observations
separate from deterministic adapter results; do not silently switch to cloud
inference when a local test fails.
