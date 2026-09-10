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
`AssistantResolvedToolCall` record lives in its own source file; #94 adds trusted
explicit-approval, blocked-result and decline hooks without changing model argument schemas.
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
resolution. Its default application input ceiling is 32,768 accounted UTF-8 JSON
bytes for **every** model. #88 removed the model-name exceptions. A fresh,
configuration-keyed, explicitly byte-accounted observation can only lower this
ceiling; token-window metadata is retained separately and never converted into
bytes. Accounting includes messages, tool
schemas, escaping, and a fixed plus per-item protocol reserve. These are
conservative application ceilings, **not negotiated model context-window
claims or an exact tokenizer**. Smaller server contexts may reject a request;
no fallback to another model/provider occurs. Configuration/runtime invalidation
and observation expiry discard observed byte limits without bypassing `Prepare`.

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

## Independent capability observations (#88)

`IAiCapabilityService.GetAsync(AiChatConfiguration, bool probe = false,
CancellationToken ct = default)` performs metadata-only observation unless
explicitly asked to probe. `GetCached(configuration)` is a fail-closed read for
consumers; `Invalidate()` clears evidence/context limits and cancels an in-flight
observation. `IAiCapabilityObserver` exposes `Kind`, `ReadMetadataAsync(configuration,
ct)` and `ProbeAsync(metadata, ct)` for provider/runtime integrations.

`AiCapabilitySnapshot` is immutable and memory-only:

- Chat, Tools, StructuredJson and Streaming each carry `AiSupport`
  (Unknown/Supported/Unsupported) and `AiObservationSource`.
- Context has its own support/source, nullable `ContextTokens`, and nullable
  `InputByteCeiling` measured in the application's accounted UTF-8 JSON bytes.
  No token-to-byte conversion or name-based budget boost is permitted.
- Endpoint, Authentication, Runtime, Model, Download and Load are independent
  enums with explicit Unknown values. A generic 404 does not establish a missing
  model; a generic 503/timeout does not establish loading or unsupported features.
  `Endpoint.InvalidConfiguration` reports malformed, relative, non-HTTP(S), or
  embedded-credential endpoints with fixed correction guidance and no transport.
- Runtime/model identity hashes are opaque, not displayable evidence. Only
  app-owned status vocabulary reaches `StatusText`/`NextStep`. Raw response bodies,
  SDK errors, credentials and provider evidence are not retained in observations.
  `Configuration` remains an execution destination, not a safe display/log string.

The service holds one active configuration and credential fingerprint. Switching
provider/endpoint/model, credential changes, explicit invalidation and observed
runtime/model-identity changes discard prior evidence; switching back cannot
resurrect a previous conversation or capability entry. Metadata has a one-minute
cache. Explicit generations are coalesced/cached for ten minutes when chat is
ready, or one minute otherwise. There is **no background generation or retry
loop**. Metadata refresh can revalidate identity without generating again.
Refreshing metadata never renews the original generation-proof expiry: expired
probe-derived fields are withdrawn independently of still-fresh metadata.
Runtime versions/revisions are used where recognized metadata supplies them
(Ollama runtime version/model digest; Copilot CLI file identity); generic
OpenAI/Azure endpoints do not provide a portable runtime identity protocol, so
unknown identities remain unknown and observations expire. External runtime
changes can remain unseen within the metadata freshness window.

HTTP metadata uses OpenAI model inventory without inferring feature support from
names or arbitrary extension fields. Ollama's recognized `/api/show` capabilities
and context-token metadata are combined with installed/loaded inventory. A tags
listing does not warm or download a model. Azure deployment-list authorization
differs from inference authorization, so it is not guessed from a model list.
Copilot metadata lists configured-model availability without starting an inference
session; failed CLI/entitlement checks are not automatically mislabeled as auth.

Explicit HTTP probes send at most three synthetic, non-streaming requests with
64 output tokens each: plain chat, a single `capability_ack` function schema, and
JSON mode. Responses are bounded to 256 KiB. Only a validated acknowledgement
proves tool support; refusal prose or ignored schemas remain Unknown. Recognized
unsupported-feature codes apply only to the named feature. JSON rejection keeps
successful chat independent. No callback reaches the app toolset. Copilot uses
the source-linked bridge for synthetic chat/acknowledgement; its SDK has no
negotiated JSON option here, so JSON and streaming remain Unknown. Its generation
bound is the shared deadline/context guard, not an asserted SDK token limit.

Metadata checks have a ten-second deadline; explicit checks have a ninety-second
total deadline and a cancel command in Settings. Cancellation/invalidated late
results do not publish. Loading and timeout feedback explains waiting and the
short cooldown, rather than retrying or downloading. Saving credentials and
configuration changes are independent from a successful capability check.

The action-capable assistant requires positive chat **and** tool observations,
before fetching definitions, after their async lookup, before resolving a callback, and again before
execution after approval. Chat-only diagnosis is separate: HTTP providers omit
unproven/unsupported JSON options and continue to request/parse a JSON answer
through the ordinary prompt; malformed output still fails, without replay.
Configuration changes during an async diagnosis observation stop the send rather
than forwarding previewed evidence to the newly selected destination.
Empty-tool chat omits tool options. Existing `AiChatConfiguration(Kind, Endpoint,
Model)`, `AiChatRequest(Configuration, History)`, `AiChatTurnResult(FinalText,
Messages)` and `IAiChatProvider` callbacks are unchanged. Journal, linked
cancellation, generation, approval, original executor and opaque Copilot context
guards remain in force.

`AiCapabilityContractTests` covers passive metadata, chat-only/tool-capable
observations, JSON and tool rejection, ignored/malformed schemas, auth/missing
model/loading/generic failures, no automatic downloads, immutable probe
credentials, coalescing/cooldown/expiry, configuration/credential/runtime/model
revision invalidation, cancelled late metadata/generation, observed byte limits,
diagnosis/chat options and destination races, pre-definition and post-approval action guards, and the
production Copilot synthetic probe seam. Existing orchestration fixtures now
explicitly provide positive synthetic capability evidence; production has no
permissive default.
Additional regressions cover proof expiry after metadata refresh (including the
exact expiry boundary), invalid endpoints without transport, and a malformed
Copilot acknowledgement followed by an SDK-swallowed error and another callback.
That failed check cannot subsequently grant tool support.

## Streaming and execution progress (#89)

`AiChatRequest.Progress` is an optional synchronous `Action<AiChatProgress>`;
`IContainerAssistant.SendAsync(string, Action<AiChatProgress>, CancellationToken)`
scopes it to one turn. Existing configuration, history, structured final result,
and the authoritative service journal remain unchanged. A future Foundry Local
adapter can use the same request callback and `AiStreamingText` without adding a
provider-specific UI contract.

Providers publish only Loading, Generating, and TextDelta. The service alone
publishes ToolRequested, AwaitingApproval, ExecutingTool, ToolResult, Completed,
Failed, and Cancelled. Provider narration cannot attest to execution. TextDelta
appends narration; ToolResult replaces evidence for its call ID, including an
updated partial/unknown result when a turn fails. Progress callbacks are ordered
by delivery. Display call IDs are turn-local opaque correlations, not raw
provider identifiers; redaction cannot collapse two distinct tools into one row.
Callbacks are synchronous; the UI dispatches updates with a captured turn generation and
rechecks that generation inside the queued action. Reset/new-chat discards late
progress and results. Progress is a bounded UI preview, not additional provider
history or an audit transcript.

### Incremental text and privacy

HTTP transport uses SSE for OpenAI/Azure and NDJSON for Ollama. Incoming model
fragments stay inside a bounded per-message accumulator. Complete plain ASCII
prose sentences/lines can be displayed **before the response finishes**. This is
not unrestricted token streaming: on encountering structured, quoted, markdown
code, URL, or other non-prose syntax, incremental release stops for the rest of
that message. Complete-input sanitization releases the remainder only after a
validated completion. If sanitization rewrites the already published prefix,
the remainder is withheld and the final answer replaces the preview rather than
appending inconsistent text. Cumulative state spans fragment boundaries; raw
fragments are never individually redacted and then displayed.

`AiStreamingText` bounds input at 128 Ki characters and displayed narration at
12,000 characters. The conservative early-release vocabulary is ASCII letters,
digits, whitespace, comma, period, exclamation mark, and question mark. This
intentionally defers non-ASCII and richly formatted responses until completion.
It inherits #91's explicit detection limits: arbitrary unlabelled or obfuscated
secrets are not recognizable. It is not a new perfect-secret-detection claim.

The HTTP parser separately caps each response at 2 MiB, each line at 128 KiB,
text/argument accumulation at 64 Ki characters, and each batch at 32 calls.
Identifiers accept ASCII letters/digits, underscore and hyphen. OpenAI/Azure
require a consistent stop/tool-calls finish reason and an SSE `[DONE]` marker;
Ollama requires `done:true` and complete JSON-object tool arguments. Standard
nullable unused delta fields are treated as absent, never as new IDs/arguments.
Unsupported or malformed wire shapes fail closed rather than guessing.
An explicitly cached `Streaming.Unsupported` selects bounded nonstream JSON
before the first request, retaining that choice across tool continuations.
Unknown/Supported attempts streaming without creating a new capability claim.
There is no retry/fallback after a failed request. Legacy callers that omit
Progress retain their existing nonstream transport behavior.

### Failure, approval and execution ownership

Tool arguments are accumulated completely and validated as JSON objects before
resolution, approval or execution; partial JSON cannot trigger a tool. The
service independently rejects blank/oversized identifiers, oversized arguments,
duplicate JSON properties and duplicate call IDs. Callbacks serialize and latch
failure, cancel the provider turn, and reject subsequent callbacks even when a
provider swallows the first exception. A completed stream is not replayed after
an error, and a disconnected stream never triggers reconnect/retry of mutations.

Inference deadlines apply to generation, not the time a person spends deciding
an approval. HTTP generation has a five-minute deadline; Copilot generation has
a three-minute deadline paused while an app callback awaits approval or executes.
Approval waits have no automatic inference deadline and remain cancellable.
Actual tool execution has a separate cooperative ten-minute deadline, linked to
caller/provider cancellation. A non-cooperative tool may outlive cancellation;
the app does not pretend it forcibly stopped or rolled back effects. Completed,
partial, not-run, and unknown outcomes remain service evidence on interrupted
turns. Reset intentionally discards the old conversation; it does not undo its
workloads. No cancellation automatically authorizes a fresh attempt.

Capability checks remain independent. Successful transport is not manufactured
streaming metadata, and no model-name inference can enable tools. The positive
chat/tool gate and proof expiry still apply before resolution and after approval.

`AssistantProgressContractTests` covers ordered approval/execution/results,
cancelled approval, failed turns retaining sanitized partial outcomes, malformed
and duplicate argument JSON before resolution, reset/late progress, provider
attempts to forge tool-result progress, and swallowed callback failures.
It also covers reentrant reset/cancel from progress callbacks, late partial
outcomes after cancellation, distinct safe display IDs, and a manually advanced
tool deadline that does not start while approval is pending.
`AiStreamingTextTests` checks early sentence delivery and every split point
through synthetic JSON name/value credentials, YAML blocks/late Secret type,
private keys, headers, quoted assignments and multiline bearer/basic tokens,
plus size limits and terminal-state rejection.

Copilot consumes SDK `AssistantMessageDeltaEvent` text through per-message
accumulators. Completed message IDs/content must correlate with all received
deltas; duplicate/interleaved/incomplete message completion fails closed. Only
complete sanitized messages enter the tracked SDK history budget. The SDK's
`ToolInvocation` binding supplies actual opaque call IDs and original JSON;
missing binding context cannot fabricate a fresh executable call. Event delivery
is serialized, bridge failures latch, and the source-linked runner tests cover
incremental prose, held structured secrets, cancellation/reset, duplicate IDs,
malformed arguments, swallowed failures and inference-clock pause/resumption.
`AiHttpStreamingTests` drives one-byte fragmented UTF-8/SSE/NDJSON transports,
pauses before terminal to prove early prose delivery, and covers complete-batch
validation, index ordering, nullable metadata, malformed/truncated/oversized
payloads, duplicate/replayed calls, disconnects, cancellation, disposal, error
body privacy, explicit nonstream selection and no fallback/retry.

```powershell
dotnet test tests\WslContainerDesktop.Tests\WslContainerDesktop.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~Assistant|FullyQualifiedName~AiTextSanitizer|FullyQualifiedName~AiStreamingText|FullyQualifiedName~AiHttpStreaming|FullyQualifiedName~AiCapability|FullyQualifiedName~AiProvider|FullyQualifiedName~GitHubCopilot"
```

All coverage uses in-memory fixtures and the existing xUnit runner. No live
inference, model download, real workload, SDK sign-in, app deployment, or
packaged UI smoke run is part of this deterministic validation.

Run all related contracts (both configured frameworks, no deployment):

```powershell
dotnet test tests\WslContainerDesktop.Tests\WslContainerDesktop.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~AiCapabilityContractTests|FullyQualifiedName~AssistantHistoryContractTests|FullyQualifiedName~AssistantOrchestrationContractTests|FullyQualifiedName~AssistantToolsetContractTests|FullyQualifiedName~AiProviderContractTests|FullyQualifiedName~AiTextSanitizerTests|FullyQualifiedName~GitHubCopilotProviderContractTests"
```

## Local runtime ownership (#90)

`LocalAiSetupServiceTests` source-links the real lifecycle service against strict
in-memory `IWslcService`, WSLC capability and AI capability fakes. No engine,
container, image/model download or packaged application is used. The lifecycle
does not publish inference progress or alter the assistant journal/history.

The setup/removal semaphore serializes this service's operations. Inventory names
are discovery hints only: inspect must supply a full immutable container ID,
the `com.wslcontainerdesktop.managed=local-ai` label and a valid operation token.
The container also identifies its model volume's operation token. Mount metadata,
volume labels, creation timestamp and mountpoint must agree; unknown/malformed
metadata is a conflict. Setup additionally requires the exact loopback-only
published API port. Older containers/volumes without these proofs remain intact;
there is no silent name-based ownership migration.
An older running Ollama can still be configured as an external Ollama provider;
that does not grant this lifecycle service ownership or removal permission.

New setup selects an already cached full image ID and enforces `--pull never`.
Shared `IWslcCapabilitiesService` observations of **create** help govern `--pull`
and `--gpus`; version numbers and run-help guesses are not evidence. Only
definitive absence of the GPU flag selects CPU before creation. Unknown GPU
support blocks. GPU creation/start failures, image/configuration/engine errors,
cancellation and uncertain outcomes never trigger a CPU retry. GPU access
requested and container running are not proof of acceleration or model readiness.
The container is created first and its real mount is verified before start.

Failure cleanup has a separate ten-second cancellation budget. Only the current
operation's ownership-verified immutable container ID can be removed; an existing
or racing unrelated runtime cannot be cleaned up. Failed/uncertain cleanup is
reported explicitly, including a possibly late creation after cancellation.
Images and model data are never automatically cleaned up. Callers receive
`LocalAiSetupResult` and `LocalAiRemovalResult`, with separate runtime/data states;
`LocalRuntimeResourceState` is backend-neutral for future native runtime use.
No container labels, mounts or GPU policy are presented as Foundry contracts.

**Product limitation:** automatic model-volume deletion remains unavailable.
`RemoveVolumeAsync` accepts a mutable name, not an immutable handle or atomic
compare-and-delete. Inspect-then-delete cannot eliminate a replacement race.
A requested deletion therefore returns an explicit partial/retained-data result,
even when runtime removal succeeds; the UI must not claim models were removed.
Users can inspect ownership and users before a separate deliberate Volumes action.

Preparation must be explicit and age-audited: pin the image/model identity and
verify authoritative publication is at least seven days old before acquisition.
Missing cached images give preparation/tagging guidance, not an implicit pull.
Mutable names and build timestamps are not publication evidence. Settings does
not download or warm a default model during setup. The separate model-pull
confirmation is user attestation of an audit, not automated publication/digest
verification; do not treat it as a provenance verifier. Capability evidence is
invalidated before owned runtime/model mutations and again on completion/failure;
HTTP metadata remains the separate source for actual runtime/model readiness.

```powershell
dotnet test tests\WslContainerDesktop.Tests\WslContainerDesktop.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~LocalAiSetupServiceTests|FullyQualifiedName~WslcCapabilitiesServiceTests|FullyQualifiedName~AiCapability|FullyQualifiedName~Assistant|FullyQualifiedName~AiProvider|FullyQualifiedName~AiHttpStreaming"
```

Covered cases include owned reuse/start, same-name collisions, missing labels,
short/mismatched immutable IDs, unknown metadata, GPU tri-state selection and
unrelated mutation failures, cached-only arguments, partial creation, replacement
races, cancellation, cleanup failure, serialization, separate runtime/data
outcomes and pre-mutation capability invalidation. Hardware GPU behavior and real
WSLC inspect variants still require explicitly authorized disposable smoke runs.

### Follow-on integration rules

- #89 may add recognized streaming observations and consume this snapshot; it
  must not infer streaming from successful non-streaming chat or bypass the journal.
- #92 should implement the observer seam using authoritative native runtime
  identities and separate download/load states, calling `Invalidate()` **before**
  owned transitions/replacement. Metadata methods must never download or load a
  model. A capability probe is not permission to deploy workloads.
- No streaming transport, runtime lifecycle manager or Foundry adapter/model
  acquisition is implemented by #88. Real-provider/hardware behavior remains
  subject to the explicit opt-in smoke policy below.

## Current engine evidence and saved-project tools (#93)

`AssistantObservationContractTests` uses the real toolset and assistant service with strict
in-memory engine/capability/health sources. It covers native/legacy/unknown capability states,
changed evidence within a turn and refreshed next-turn prompts, unavailable capability/cluster
evidence, positive cluster exposure, native observation age/staleness, changed-engine refusal,
app observation identity/generation/age checks, and zero probe/inventory calls for health reads.
The real shared volume resolver covers stopped-container users, complete empty inventory,
Exact/Partial/Estimated/Unknown/Unused results, failed inventories and engine changes during
scans. Read-only schemas reject extra fields before I/O. Raw probe errors, commands and local
paths are withheld.

`AssistantComposeContractTests` additionally exercises all four saved-project tools through
the real approval gate/supervisor: forced approval despite auto-approval, no pre-approval writes,
shared Stopped/Removed outcomes, retained volumes, manual-stop suppression, exact-name selection,
schema rejection of scope/confirmation/deletion flags, changed saved-target refusal, single-use
tokens, tri-state backend evidence and decline. Generated/template coverage below continues to
exercise the same extracted review binder and outcome formatter.

The health seam does not test DispatcherQueue scheduling or actual watchdog TCP/command probes.
Its native display freshness window is conservatively 15 seconds; cached Absent/Disabled
observations may therefore display stale before the poller's five-minute absence cache expires.
Volume scans are not atomic and do not close an inspect/delete race. No provider or engine
response is permission; source evidence and backend revalidation remain independent of the AI
positive capability gate and journal.

Run the combined offline contracts without deployment:

```powershell
dotnet test tests\WslContainerDesktop.Tests\WslContainerDesktop.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~Assistant|FullyQualifiedName~Ai|FullyQualifiedName~Compose|FullyQualifiedName~VolumeUsageResolver|FullyQualifiedName~WslcCapabilitiesService"
```

Remaining #95/#86 integration scope is unchanged: packaged approval/progress/accessibility
presentation and actual StatusMonitor/watchdog dispatch, live WSLC help/inspect schema variants,
real provider/SDK behavior, and disposable native/legacy lifecycle/volume runs require separate
explicit authorization. #92 Foundry remains a separate draft and is not included in this branch.

## Shared Compose consequence approval (#94)

`AssistantComposeContractTests` runs the real toolset, assistant gate/journal, importer,
shared review/planner and supervisor against the existing strict engine/store doubles.
Generated YAML and Compose templates use the same assertions. Coverage includes complete
ports/mounts/warnings/backend/ownership previews, forced explicit approval despite saved
auto-approval, no second dialog or pre-approval mutation, unknown capabilities and
resource/ownership blockers, stale inventory/capabilities/templates and changed/missing
included source files, token expiry/reuse,
rejection/cancellation/reset, unchanged applied-instance reuse and reviewed replacement,
replica-level partial cancellation, failed/dependency-skipped startup, failed native cleanup
without legacy retry, and retained-resource uncertainty. Parse/preflight failures preserve
saved project state and template defaults.

Sensitive environment/probe values remain in original execution options but are absent from
approval details, provider evidence/history and serialized activity. Raw generated YAML is
withheld centrally from echoed tool arguments, including arbitrary-key values. Oversized
approval details fail closed; bounded outcome JSON preserves failure kind/success flags and
explicit omitted outcome/resource counts. Schemas reject model-supplied confirmation fields.

Run the existing combined suites (both configured target frameworks):

```powershell
dotnet test tests\WslContainerDesktop.Tests\WslContainerDesktop.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~Assistant|FullyQualifiedName~Ai|FullyQualifiedName~Compose"
```

This is deterministic contract evidence, not live provider, WSLC, packaged UI, bind-file,
or GPU certification. The two installed-engine smoke cases remain explicitly opted out.
No provider, workload, model download, deployment or new dependency is needed.

## Remaining feature-layer acceptance

These are **unmet criteria**, not skipped tests or assertions that unsafe
behavior is desirable. Scripted resolver tests remain orchestration-boundary
coverage; the separate real-toolset suite above establishes argument validation
and inventory safety.
Serialized activity capture is a test sink, not the production on-disk store.

| Layer | Regression coverage still required |
| --- | --- |
| #88 live compatibility | Deterministic observation/consumer contracts are covered above. Actual provider metadata conventions, SDK transport/entitlement failures and hardware cold starts still require explicitly authorized smoke runs; unknown metadata is not filled with guesses. |
| #89 streaming | Fragment assembly, complete validation before action, progress ordering, disconnect recovery, inference versus approval timeouts, cancellation/reset generations, partial outcomes and no replay. Current adapters return final strings. |
| #90 runtime ownership | Deterministic real-lifecycle/fake-engine coverage is described above. Automatic model-volume deletion is deliberately unavailable without atomic immutable targeting. Real-engine/GPU compatibility remains unverified; no workload is manipulated by the suite. |
| #92 Foundry Local | Deterministic dedicated adapter/runtime tests using the shared contracts, plus explicitly opted-in packaged and hardware runs. No Foundry dependency or model is acquired by this foundation. |
| #94 Compose live integration | Deterministic shared-plan/approval/outcome coverage is complete above. Real provider/tool rendering, actual engine resource retention and packaged UI review remain unverified until separately authorized smoke runs. |
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
