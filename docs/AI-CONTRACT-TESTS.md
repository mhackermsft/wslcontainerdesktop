# Assistant contract test foundation

Issue #95 establishes deterministic coverage for existing orchestration and HTTP
adapter contracts. It is a foundation, **not completion of all #95 acceptance
criteria**. The feature layers below must add their own regression coverage as
their contracts become available. Issue #97 extends this foundation with real
argument resolution, approval-bound container plans, and partial-execution tests.
Issue #91 adds the shared evidence/privacy boundary and deterministic regression
coverage described below.

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

## Privacy boundary regressions (#91)

`AiTextSanitizer.Sanitize(text, maxChars = 12000)` is the reusable evidence,
display and audit entry point. It applies complete-input redaction before
head/tail truncation. `Redact(text)` remains the unbounded structured/text
operation. Oversized JSON is replaced with a valid JSON object containing
`truncated: true` and a sanitized preview; malformed structured-looking evidence
is explicitly omitted. Bulk execution results instead retain overall `status`
and structured target identities/statuses, shorten detail strings first, and
report `omittedOutcomes` when even the target rows cannot fit. Neither form is an
execution input.
`SanitizeMessage` copies content and argument JSON while preserving protocol IDs,
roles and tool names. `SanitizeDefinition` preserves the schema/name and sanitizes
the description. Future tool/history/streaming integrations must call these at
their outbound/retention boundary, never overwrite the executor's original values.
Diagnosis preview and provider transports share the 48,000-character
`DiagnosticLimit`. `WrapLogger` supplies a sanitized logger for SDK integrations;
it forwards only sanitized strings, not original structured state, scopes or
exception objects. This wrapper has synthetic sink coverage. Copilot session-store
disablement remains in place; no live SDK session is created by these tests.

`AiTextSanitizerTests` covers nested JSON, sensitive fields with structured values,
environment arrays and name/value pairs (either property order), embedded JSON
and YAML, Kubernetes Secret data, YAML nested/block scalars, quoted assignments,
headers/cookies, URL credentials, connection strings, private keys, invalid/deep
structures, idempotence, bounded valid JSON and secrets crossing truncation
boundaries. Ordinary context, IDs, schemas and explicit detection limitations
are asserted. Source-linked diagnostic preview and error-classification tests
cover section sanitization before joining/truncation and displayable errors.

Terminal-evidence regressions cover ANSI CSI colors, OSC controls terminated by
BEL/ST, incomplete OSC, unmatched/invalid bracket and quote prefixes, nested
JSON, colored YAML and assignments, and truncation boundaries. Recognized
terminal controls are removed only from evidence copies before parsing, including
decoded JSON string values; original execution values and protocol IDs remain
unchanged. Failed embedded-JSON candidates cannot hide later valid structures.
Overlapping candidate scans have a four-times-input-length character budget;
exhaustion explicitly omits unexamined evidence rather than allowing quadratic
scanning or forwarding unexamined secrets. A 100,000-bracket adversarial fixture
asserts this omission, and all three real HTTP serializers capture sanitized
colored Secret tool results without the synthetic canary.
Decoded JSON discriminator values (`kind`, environment `name`/`key`) and
recognized field names are normalized before secret classification, not only
when writing their values. First-pass and idempotence tests include CSI/OSC
discriminators and wrapped logger/scopes, which cannot rely on a second pass.
Original property names are preserved to avoid normalized-key collisions;
protocol IDs, schemas and execution input are not rewritten.

Real orchestration captures serialized activity at the persistence boundary,
provider callback evidence, sanitized approval details, rejection, resolution
and execution errors, text history, and untrusted instruction-like log content.
Real toolset tests cover inspect/log success and failure, approved run environment
values arriving unchanged at the executor, command truncation, and #97 structured
partial/unknown/not-run outcomes with command errors and thrown exceptions.
The HTTP adapter suite captures outbound message bodies for all three adapters,
including echoed call arguments and tool results, while asserting original
execution arguments and unchanged protocol metadata/schema.

Run the existing tests with the combined filter:

```powershell
dotnet test tests\WslContainerDesktop.Tests\WslContainerDesktop.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~AiTextSanitizerTests|FullyQualifiedName~AiProviderContractTests|FullyQualifiedName~AssistantOrchestrationContractTests|FullyQualifiedName~AssistantToolsetContractTests"
```

Fixtures contain synthetic secrets only. Activity capture is the serialized
`IActivityLog.Record` input, not a packaged on-disk integration test. This proves
the assistant writes sanitized event data, not retrospective cleanup, perfect
secret detection, encrypted workload configuration, or SDK transport behavior.
The README and Settings notice describe these limits.

## Structured history and conversation ownership (#96)

`IAiChatProvider.RunTurnAsync` accepts `AiChatRequest`, containing an immutable
`AiChatConfiguration` (provider kind, endpoint, model/deployment) and a copied
message history. It returns `AiChatTurnResult` with final text and a structured
new-turn transcript. Provider transports retain sanitized copies, while tool
callbacks receive the original arguments.

The service independently journals each actual tool callback and its paired
outcome. This journal is authoritative even if a provider disconnects before
returning its transcript or produces an uninformative prose summary. Completed
outcomes survive failed/cancelled turns; actions that never reached execution are
not-run, while incomplete executions remain explicitly unknown, never reported
as rolled back. Failure diagnostics are sanitized. Any failed callback closes
the turn to further actions or a success response, even when a provider swallows
an unexpected exception. There is no automatic action replay. Reusing a call ID within a turn is rejected
before another resolution or execution; this is not general exactly-once
execution across separate model-generated calls.

History is memory-only. The next send clears it when the selected provider,
endpoint, or model/deployment differs; switching back does not restore an older
conversation. In-flight requests retain their captured destination/model.
Reset cancels the active generation and clears pending approvals and history.
Service guards reject late callbacks after resolution, before approval,
before execution, and before committing text/history. An old turn's cleanup
cannot clear a new turn's approval. Overlapping sends are rejected rather than
interleaved; tool callbacks within a turn are serialized.

`AiConversationContext.Prepare` is used before provider requests and tool
resolution. Its application input ceiling is 32,768 accounted UTF-8 JSON bytes
for unknown/custom models, or 65,536 for the exact known IDs `gpt-4o`,
`gpt-4o-mini`, `llama3.1`, and `qwen2.5`. Accounting includes messages, tool
schemas, escaping, and a fixed plus per-item protocol reserve. These are
conservative application ceilings, **not negotiated model context-window
claims or an exact tokenizer**. Smaller server contexts may reject a request;
no fallback to another model/provider occurs. #88 can replace the policy with
configuration-keyed observed limits without bypassing `Prepare`.

Old complete user turns are evicted together with all calls and outcomes,
retaining the trusted system prompt and an explicit truncation notice. The
notice is also shown in the final service response. An active turn or tool
schema set that cannot fit fails closed instead of silently dropping live
outcomes to continue inference. Retention can evict an oversized completed
turn, also with the explicit notice. Shared per-evidence sanitization remains
in effect before accounting/truncation. No model-generated summary replaces
the surviving structured evidence.

`AssistantHistoryContractTests` uses the real service, scripted provider races,
all three captured HTTP adapters, and the production Copilot bridge to cover evidence round trips, failed-turn
retention, local-to-cloud changes, switching back, configuration capture before
asynchronous definition lookup, reset/cancellation and late callbacks, stale
approval cleanup, overlapping sends, duplicate call IDs, partial cancellation
outcomes, Unicode/schema accounting, paired eviction, and visible truncation.
Include `FullyQualifiedName~AssistantHistoryContractTests` in the focused filter.

`AiProviderContractTests` also checks immutable configuration and credential
capture across tool continuations, returned transcript round trips, per-request
aggregate budgets, oversized schemas, and stopping remaining calls on
cancellation. `GitHubCopilotProviderContractTests` source-links the production
`CopilotChatTurnRunner` bridge with a fake session delegate: no SDK session,
sign-in, process, or credential store is created. The real service also runs over
that bridge for history round trips and model isolation. It tracks actual
callbacks/outcomes and stops the session after callback failure rather than
accepting an SDK-swallowed error as success.

Copilot's SDK owns its internal inference loop; the bridge cannot surgically
prune that session. It therefore stops when the tracked session context exceeds
the application ceiling, instead of pretending that pruning a local copy
changed SDK state. A subsequent user turn creates a new session with bounded
retained evidence. SDK-internal wire behavior and unobserved runtime overhead
remain outside deterministic coverage; the app's history and tool evidence
contracts are exercised without claiming live-provider compatibility.

Combined focused command (both configured targets, with no deployment):

```powershell
dotnet test tests\WslContainerDesktop.Tests\WslContainerDesktop.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~AssistantHistoryContractTests|FullyQualifiedName~AssistantOrchestrationContractTests|FullyQualifiedName~AssistantToolsetContractTests|FullyQualifiedName~AiProviderContractTests|FullyQualifiedName~AiTextSanitizerTests|FullyQualifiedName~GitHubCopilotProviderContractTests"
```

## Remaining feature-layer acceptance

These are **unmet criteria**, not skipped tests or assertions that unsafe
behavior is desirable. Scripted resolver tests remain orchestration-boundary
coverage; the separate real-toolset suite above establishes argument validation
and inventory safety.
Serialized activity capture is a test sink, not the production on-disk store.

| Layer | Regression coverage still required |
| --- | --- |
| #88 capabilities | Independent Unknown/chat/tool/JSON/streaming/context support, configuration-keyed observations, unsupported JSON, chat-only models, loading versus failures. A diagnosis JSON-mode serialization test is not capability negotiation. |
| #89 streaming | Fragment assembly, complete validation before action, progress ordering, disconnect recovery, inference versus approval timeouts, cancellation/reset generations, partial outcomes and no replay. Current adapters return final strings. |
| #90 runtime ownership | Fake inventory/process-backed local setup: ownership/name collisions, GPU versus other failures, safe fallback, partial creation, racing replacement, cancellation, cleanup failures and retained model data. No runtime lifecycle is invoked here. |
| #92 Foundry Local | Deterministic dedicated adapter/runtime tests using the shared contracts, plus explicitly opted-in packaged and hardware runs. No Foundry dependency or model is acquired by this foundation. |
| Copilot SDK adapter | The production chat bridge now has fake-session history/budget/cancellation/failure coverage, including real-service round trips. SDK-internal transport, actual model events, sign-in and opaque runtime overhead still require an explicitly authorized live smoke run. |

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
