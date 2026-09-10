# Foundry Local integration: scope and compatibility

**Revised product scope, 2026-09-10:** connect to a Microsoft Foundry Local
instance running on this PC, plus app-guided, explicitly confirmed installation
of the standalone runtime and an initial model. This supersedes the earlier
in-process WinML/native-broker proposal. The app will not bundle a Foundry SDK,
host native inference itself, or treat a new broker as an acceptance requirement.

The implemented initial-model flow is separate from the remaining signed-app
and representative-hardware acceptance evidence. Working model setup is a
product requirement, not something a smoke test can replace.
Deterministic HTTP fixtures establish adapter
behavior, not compatibility with a particular installed Foundry release, model,
execution provider or device.

## Implemented REST slice

Settings offers **Foundry Local (local REST)** with separately persisted endpoint
and model fields. The endpoint must include an explicit port and be loopback,
optionally ending in `/v1`. It has no default. The dedicated HTTP transport
disables redirects, proxies, cookies and integrated credentials. `localhost`
remains the HTTP authority and TLS certificate hostname, while a dedicated
socket callback connects to the loopback IP without consulting DNS/hosts.
Default certificate validation remains enabled; no certificate-store changes
or TLS bypass are used. No saved OpenAI key is
used. A correctly configured loopback host must still be trusted by its operator.

**Selected standalone 0.10.3 adapter:** actual `/openai/status` returned 404, so
the registered service no longer uses the legacy management endpoints. It binds
CLI `server status --output json` process/start identity around standard
`/v1/models` reads. A stopped status can retain stale URLs/PID/start time; those
fields are discarded when `running` is false. PID/start/endpoint identity, not
changing uptime/log text, binds the observation. Metadata never starts the daemon.
The v1 listing alone does not establish cached/loaded state or tool support:
these remain **unknown** until explicit preparation verifies a CLI load and
synthetic completion on the same process. The exact audited CPU model metadata
establishes its format/size/license; no arbitrary model-name inference is used.
The legacy reference adapter and its
synthetic route tests remain in source but are not selected by application DI.

Diagnosis and assistant turns require an exact cached, loaded ONNX catalog model.
Capability tests independently observe chat, tool calls, tool-result acceptance,
JSON and streaming; an advertised tool flag cannot establish positive support.
The provider checks current configuration and observed runtime/model identity
before requests and tool callbacks. It binds one immutable capability observation
across asynchronous inventory reads, rejects withdrawn/replaced evidence, checks
that AI remains enabled, and rechecks immediately before sending after progress
callbacks. Responses must identify the requested model.
History, bounded stream parsing, original action approval, cancellation and
redaction use the shared assistant contracts.

**Initial-model setup** uses one confirmation for installation if absent, pinned
model staging, owned cache registration, start if stopped, exact CLI load and
one synthetic local completion. A CPU variant is deliberately selected rather
than guessing a GPU/NPU model or depending on optional hardware. Source/staged
receipts and `download.tmp` protect partial preparation; foreign cache entries
are not adopted or overwritten. Completed packages/files remain after failure.

**Unload model** only targets an app-verified model on its observed process.
**Stop shared server** requires a separate confirmation naming the current
process/start/endpoint and warning that every client's models are affected.
Neither is automatic on cancellation or app exit; neither deletes cached files.

**Memory ownership policy:** the external host owns its idle TTL and allocation
policy. The app never keeps a model alive, automatically unloads models on exit,
or promises an immediate release following cancellation. The standalone
runtime owns its model TTL; setup does not grant ownership of pre-existing
processes or permission to stop/uninstall them.

## Verified standalone lifecycle (2026-09-10)

The coordinator's `networked-model-check-0.10.3.json` reported `Error: null` and
`ModelReady: true`. Nine approved files (877,988,985 bytes) under
`<cacheRoot>\WslContainerDesktop-qwen-cpu-v4\v4` plus the exact
`inference_model.json` template were discovered by the shipped scanner. CLI
`model load qwen2.5-0.5b-instruct-generic-cpu:4 --output json` succeeded without
another model cache entry. The reported cache delta was 282,895 bytes of
metadata/index, not a repeat model download; no new EP packages were registered
by that load. The synthetic completion returned `OK` and the canonical `:4`
model ID. Unload and server stop both returned success; stopped state was observed.
Secret-free response bodies are recorded in `networked-lifecycle.json`.

The model-list body has ID `qwen2.5-0.5b-instruct-generic-cpu`, both before and
after load. This is **not load proof**. Only its observed mapping to the pinned
version is normalized; version stripping is not a general alias policy.
The completion contains both `delta` and `message` with empty `tool_calls`;
non-stream parsing uses the complete message and does not duplicate the text.

**Online limitation:** blocked-network `/v1/models` returned HTTP 500 because
catalog requests failed across regions. File reuse is offline, and inference
executes locally, but the current metadata/capability path is not fully offline.

**Vendor-managed EPs:** daemon startup invokes Windows deployment and installed
Microsoft Intel OpenVINO / NVIDIA TRT-RTX packages in the observed exercise.
Executable-level firewall rules did not prevent delegated acquisition.
Setup discloses that Microsoft/Windows select these versions and may use the
network. The app neither chooses, pins nor audits them. The dependency-age audit
applies to the runtime/prerequisite/model artifacts the app selects and stages,
not to vendor/OS-managed runtime servicing. No alternative runtime is substituted.

Signed WSL Container Desktop MSIX deployment, UI automation and representative
GPU/NPU coverage have not been performed. The real standalone CPU exercise is
not a claim that those separate acceptance surfaces passed.

## Historical investigation record (superseded by the lifecycle result above)

The sections below preserve earlier findings, blocked milestones and research
tradeoffs as an audit trail. Earlier statements that model setup or EP policy was
unresolved are historical, not current product gates.

No explicit Foundry endpoint or loaded model ID was supplied for this worktree.
Read-only discovery found no `foundry` executable on PATH, no matching Foundry
process name, and no explicit Foundry endpoint/model environment variable. These
observations do **not** establish that Foundry is absent from the machine: a
custom host can expose the SDK's optional REST server without the CLI.
No ports were scanned and no sample port or model was substituted.

Consequently, the existing OpenAI-compatible adapter has **not** been exercised
against a real Foundry endpoint. No runtime was installed or started, no model or
execution provider was acquired, no inference was run, and no package identity
was deployed as part of this investigation.

**Subsequent authorized registration/help observation (2026-09-10):** the
coordinator registered the pinned Microsoft x64 MSIX 0.10.3.0 for the same user,
preserving installed VCLibs 14.0.33728.0. Both installed executable paths were
outbound-blocked during six successful CLI help/version calls. Owned job trees
ended and both temporary rules were removed. The secret-free captured command
outputs and package hash are in `tests/WslContainerDesktop.Tests/Fixtures/Foundry/0.10.3/help.json`.
This establishes actual package registration and those help contracts, not a
running REST endpoint, model setup, app deployment or inference compatibility.

| Phase 1 question | Evidence and remaining work |
| --- | --- |
| Actual endpoint, model identity and endpoint lifetime | Blocked on an explicitly supplied local base URI, loaded model ID and runtime/host version. Record whether the host is the CLI or an SDK application. A restarted host can change its port. |
| Diagnosis JSON and structured-output options | OpenAI-compatible request shape is documented; actual JSON compliance and `response_format` support require a model-specific probe. Valid prose is not valid diagnosis JSON. |
| Tool calls and tool results | Microsoft documents tool calling, but the REST reference and native tutorial have different levels of detail. Verify modern `tool_calls`, opaque IDs, JSON argument strings and matching `tool` results against the actual host. |
| Streaming | REST documents SSE ending in `[DONE]`. Verify fragmented text, finalized tool arguments, termination and disconnect behavior; do not retry a failed stream as nonstreaming inference. |
| Cancellation and cold start | Measure actual host behavior with cached assets. Cancelling an HTTP request does not prove the host stopped computing or unloaded the model. |
| Offline inference | Disconnect network only in an authorized isolated test environment after preparing all required assets. A cached model alone does not establish that its execution provider is installed. |
| GPU/NPU/CPU support | Requires actual device, driver, runtime, model variant and execution-provider observations. Model names and catalog target hints are not hardware measurements. |
| Signed x64 MSIX | Not exercised. A successful source build is not proof of package registration, native dependency loading, redirected cache paths or offline behavior. |

### Opt-in automated standalone checks

`FoundryLocalRuntimeOptInTests` is excluded by default. It has two independent
gates, each requiring Windows and nonempty `WSLC_FOUNDRY_LOCAL_ENDPOINT`,
`WSLC_FOUNDRY_LOCAL_MODEL`, and `WSLC_FOUNDRY_LOCAL_CLI` environment variables.
The CLI value must be an absolute path to the separately audited `foundry.exe`;
the tests use the production standalone adapter, not the legacy reference:

- `WSLC_FOUNDRY_LOCAL_METADATA_TESTS=1` permits CLI version/status execution and
  HTTP metadata reads, not start/cache-list/model commands.
- `WSLC_FOUNDRY_LOCAL_INITIAL_SETUP_TESTS=1`, together with an explicit
  `WSLC_FOUNDRY_LOCAL_MODEL_STAGING` local directory, permits the real initial
  setup flow against an already installed audited CLI, then synthetic capability
  probes/diagnosis. This includes missing pinned-file downloads, owned cache
  registration and daemon start/load. Windows may acquire vendor-selected EPs.
  It does not install runtime packages, automatically stop the daemon or clean
  up retained files. This broader gate must be separately authorized; it is not
  implied by the old inference-only gate.

Set a gate only after obtaining permission for its operations and satisfying
the prepared-host prerequisites. The initial-setup gate must not be enabled merely
to discover a usable endpoint/model. Neither test installs runtime packages,
deploys the app MSIX, or invokes application tools. Both gates
were explicitly disabled during deterministic validation for this change.
These tests are standalone integration checks, not packaged or hardware acceptance.
The initial-setup check obtains load proof through the same registered-file
load/completion path used by the product; it does not manufacture cached evidence.

```powershell
dotnet test tests\WslContainerDesktop.Tests\WslContainerDesktop.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~FoundryLocalRuntimeOptInTests"
```

## Earlier SDK evaluation (not the selected product architecture)

The initial investigation evaluated `Microsoft.AI.Foundry.Local.WinML` for Windows.
It exposes hardware-aware catalog selection, model cache/load/unload and
execution-provider management without requiring an installed Foundry CLI.
The cross-platform package was `Microsoft.AI.Foundry.Local`. Neither is added by
this REST integration; no Foundry dependency version is implicitly approved.
The later SDK 2.0.1 release unifies packages and changes the native API; it must
not be confused with standalone CLI release 0.10.3. The user's revised direction
selects the separately installed CLI/REST architecture, not either SDK variant.

| Concern | Native WinML SDK inside the app | Separate local REST host |
| --- | --- | --- |
| Packaging | Adds native/transitive assets that must be audited and tested with this app's self-contained Windows App SDK and signed x64 MSIX. | Keeps Foundry native DLLs outside the UI process; the existing managed HTTP adapter needs no new inference SDK. Host provisioning remains separate. |
| Isolation | Native failures and memory pressure can affect the UI process. An owned out-of-process broker is another design option, not implemented here. | A host failure ends the request without loading Foundry binaries in the UI process. A loopback server is still a trust boundary, not a sandbox. |
| Performance | Avoids HTTP serialization/transport overhead and exposes native streaming. No measurements were taken. | Adds loopback HTTP overhead. Model inference cost, cold-start time and throughput remain unmeasured; do not claim a performance advantage. |
| Hardware | SDK can detect hardware and select/register execution providers. Automatic acquisition conflicts with an unaudited initialization path. | Catalog device/provider fields describe intended targets. The app cannot prove driver support, free memory or acceleration merely from these fields. |
| Lifecycle | Explicit app ownership can support cache paths, loading, unloading and shutdown. Singleton lifetime, concurrency and disposal need packaged tests. | Server is externally owned. Do not stop it or unload every model on app exit. Model operations must target only the deliberately selected model. |
| Offline | Requires the runtime, exact model and all selected execution-provider assets already present. | Same requirement, in the host's cache and environment rather than the UI package's cache. |
| API stability | Pin SDK, native and transitive versions; validate the actual API graph. | The REST reference explicitly describes a preview CLI API with potential breaking changes. SDK optional-server management-route parity is not guaranteed. |

The documentation's console project examples use `WindowsPackageType=None`
and discuss a non-self-contained Windows App SDK. Those settings must **not**
be transplanted into this packaged app. Existing package identity and
self-contained deployment remain intact.

Any app-owned setup staging files must use an appropriate writable location,
not the signed app's install directory. For paths supplied to an external process,
use `ApplicationData.Current.LocalCacheFolder.Path`; a literal
`%LOCALAPPDATA%` path can differ from the packaged app's redirected path.
This REST path does not copy a remote `ModelDirPath` into an app-owned path,
claim ownership of that directory, or delete it.

## Standalone installation provenance (2026-09-10)

### Current implementation versus remaining product work

**Available:** Settings can explicitly discover an existing `foundry.exe` from
an absolute local PATH entry, inspect its help before selecting a status command,
validate an unambiguous loopback endpoint, and read REST inventory. A separate
confirmation saves only that endpoint and preserves the exact model selection.
Cancellation, edits, provider switches and newer discovery invalidate the
pending connection. Unknown command/output contracts produce manual-URL guidance.
No automatic model selection or runtime start occurs.

**Prepared-package adapter:** `FoundryLocalInstaller` verifies prepared local
MSIX/APPX sizes and SHA-256 hashes and holds the files open through a separate
runtime-only confirmation and fixed `Add-AppxPackage` process invocation.
It refuses known existing Foundry installations, checks the selected dependency
and installed identity, invalidates capabilities around attempted deployment,
and reports cancellation/failure without promising rollback. This adapter alone
does not implement model setup. The compiled registration allowlist now contains
the exact runtime and prerequisite inspected below; it cannot be populated from
user-entered provenance records. Registration approval does not authorize native
initialization, runtime startup or any model/EP acquisition.

**Implemented runtime-only workflow:** **Install runtime only** performs a
read-only Windows-package/CLI preflight and asks once before network or package
registration. Consent identifies the pinned versions, hashes, licenses, maximum
download bytes, unchanged model selection, initialization limits and retained
data. Setup then downloads only the approved HTTPS runtime/archive, verifies
exact sizes/SHA-256 hashes, extracts only the approved APPX, revalidates the
files and registers packages without launching Foundry. A previously observed
Microsoft x64 VCLibs version at least 14.0.33728.0 is retained rather than
downloaded, downgraded or treated as freshly audited.

Staging uses the real packaged `ApplicationData.Current.LocalCacheFolder.Path`
under `FoundryRuntimeSetup`, never the read-only install directory or a literal
unredirected `%LOCALAPPDATA%` path. Hash-named verified files are rehashed before
offline reuse. Interrupted `.partial` files remain visible in the setup cache,
are never installed/reused, and may be removed manually. A corrupt complete
cache file fails closed instead of being silently replaced. Cancellation or
settings changes stop further app requests; Windows deployment may still
complete, so no uninstall/rollback is promised. All runtime processes, models
and execution providers remain untouched by this registration-only workflow.

**Separate initial-model work:** hardware-aware initial-model choices and exact
model/EP audit, and version-proven download/load/start orchestration with
partial-cache recovery. Those need implementation after the prerequisites are
established; neither adapter tests nor a runtime smoke replaces that work.
The standalone MSIX is per-user; this still installs Foundry on the PC and is
not itself a blocker merely because it is not an all-users installer.

**Implemented model-file staging:** the Settings action downloads only the
pinned generic-CPU Qwen v4 nine-file artifact after one explicit confirmation.
`FoundryLocalModelArtifacts` checks the age gate, exact registry/container origin,
If-Match ETags, dates, content lengths and bounded streamed bodies. SAS credentials
remain in memory; redirects/retries/latest-version refresh are disabled.
Completed files have separate manifest-bound local SHA-256 receipts and are
rehashed before offline reuse. Partial/unreceipted/corrupt data fails closed with
retention/recovery guidance. Files are held under the packaged LocalCache
`FoundryModelStaging` directory, not copied into the external runtime's cache.
Provider/setting changes cancel the operation; no endpoint/model settings,
Foundry process or execution provider are changed. Staging success is explicitly
not cache registration, model load or working one-click initial-model setup.

The documented standalone candidate is **CLI 0.10.3**, package version
**0.10.3.0**, not SDK 2.0.1. Its release notes say it embeds SDK 1.2.4.
Read-only research identified:

| Artifact | Evidence | Limitation |
| --- | --- | --- |
| `foundry-0.10.3-win-x64-winml.msix` | Microsoft release asset, published 2026-08-07T21:38:48Z; 29,982,055 bytes; SHA-256 `86A01C52265BD9C9167C1F8A04F34621A2A63EC5C8276166C2EECC8C6A56553F`. Microsoft winget manifest independently supplies the same hash. | Subsequently registered with separate permission; exact version/help captured under outbound blocking. Model initialization and EP behavior remain unverified. |
| `Microsoft.VCLibs.Desktop.14` | Foundry winget manifest requires at least `14.0.33728.0`; the exact inspected nested x64 APPX is 6,757,465 bytes, SHA-256 `077A3D1A5D0622BD3004DCA85F5E192D6E98EC79B83D4AA06766759EA6C09C3D`. | No future version is approved by that minimum. No unpinned prerequisite acquisition or forced downgrade of a pre-existing framework. |
| Initial model | REST catalog reports names, versions, size, license and device hints. | No approved exact model variant/file manifest with authoritative publication dates and hashes is available. No sample alias is an approved default. |
| Dynamic EPs | CLI docs describe first-run acquisition and automatic plugin updates. | Exact prospective binaries, dependencies, dates and integrity evidence must be verified before acquisition. A cached model does not prove EP readiness. |

The installer meets a conservative 2026-09-03T00:00:00Z publication cutoff.
Its exact inspected bundle is approved for registration, **not the model/EP or
future runtime-initialization acquisition graph**.
The VCLibs winget manifest identifies a nested x64 APPX inside
`DesktopAppInstaller_Dependencies.zip` from winget-cli release `v1.9.25180`,
SHA-256 `EEBA62F08531C8669B3E5FB895CE8800AE66D798739CDA2B4AEF2A1D80A5F3C5`.
The official release asset metadata dates that ZIP to
2024-11-01T16:37:27Z (50,182,148 bytes), so its distribution age passes.
The archive was downloaded only after the source date/hash was revalidated and
under explicit archive-inspection permission. That old immutable bundle proves
that every contained byte, including the nested APPX, existed by the publication
date: a separate release date for each embedded DLL is not required.

### Inspected identities, acquisition closure and runtime limits

The Foundry manifest identifies `Microsoft.FoundryLocal`, version `0.10.3.0`,
architecture `x64`, publisher
`CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US`.
It declares Windows Desktop build 17763 or newer, an execution alias for
`foundry.exe`, full-trust capability and microphone access. It declares no
startup task or service extension. **It does not declare a VCLibs
`PackageDependency`**, despite the winget prerequisite; checking VCLibs through
`Foundry.Dependencies` would therefore incorrectly fail installation verification.

The prerequisite APPX identifies
`Microsoft.VCLibs.140.00.UWPDesktop`, version `14.0.33728.0`, `x64`,
the same Microsoft publisher, and `Framework=true`. Its only declared dependency
is Windows Universal build 10042 or newer. No additional package acquisition is
needed to register these exact packages; Windows remains responsible for
platform compatibility and package-signature validation.

Embedded signature CMS integrity was checked without executing either package.
The Foundry signer is Microsoft Corporation / Microsoft Code Signing PCA 2024,
certificate thumbprint `AB172913A2960A224809EE8A0C371CD47A079B72`.
The APPX signer is Microsoft Corporation / Microsoft Marketplace CA G 022,
thumbprint `379321FD95EF176D725D6D88772A3F87EC359701`.
This is not a claim that the inspector independently validated online trust,
revocation or timestamp chains. Deployment must still validate signatures;
the app must never enable unsigned packages or bypass deployment policy.

The runtime bundle contains these native images, all covered by its pinned
archive hash and August 7 publication:

| Image | PE file version | SHA-256 |
| --- | --- | --- |
| `foundry.exe` | `0.10.3.0` | `53A805895DEBD297BC5FE424BDBF64F551EFBE0C53DDAE397ECAE9F5BCC7DB98` |
| `foundrylocald.exe` | `0.10.3.0` | `A49C23D4AF9F470DA7128C29EEF3FB1C0A64CF8555C2A3500E8683855886F3D1` |
| `Microsoft.AI.Foundry.Local.Core.dll` | `1.2.0` | `8AB7027F4273FD3A7C8BE383BF70CC592032D3E37BDB08BE4C97D1D31979285A` |
| `Microsoft.Windows.AI.MachineLearning.dll` | `2.1.1.9760` | `C8C0372A93D10618DF5F52F1490F8C5C250FE42F369351A384634D07C6D8671D` |
| `onnxruntime.dll` | `1.26.20260520.2.756157b` | `6A4129504501CBD615EFDDC897345EC9557390B408887165AB5FAF9812A54B31` |
| `onnxruntime-genai.dll` | `0.14.1` | `762A76AA622EB2E7B1EE977752E1A30F763669D1037E9BE18D036668E0B1EF27` |
| `onnxruntime_providers_shared.dll` | `1.26.20260520.2.756157b` | `97FC0CCC43386F8769A0AFC43FB1DBA3A066F718CD1FE0E8F540E24E0ECB61A7` |

These versions were read from static PE resources, not by loading or executing
the images. A file resource version need not equal the SDK package version in
release notes (notably the Core DLL reports `1.2.0`); the inspected hashes and
outer bundle identity, not an inferred NuGet version, identify the actual bytes.

Static PE imports reference Windows platform APIs and the VC libraries contained
in the inspected prerequisite. Windows ML also has a **delay-load reference to
`Microsoft.Internal.FrameworkUdk.dll`**, absent from these archives. Its optional
framework-dependent feature path is not proven necessary for registration or
every inference path; equally, the inspection cannot prove it is never needed.
No missing DLL will be downloaded automatically. The bundle does not include
DirectML or hardware-vendor EP packages. Registration success must not be reported
as successful native initialization, accelerator readiness or initial-model setup.
The VCLibs package's presence alone does not prove the runtime's DLL search path
will resolve it; that is a separate authorized runtime-compatibility case.

The CLI's license permits installation/use but has separate proprietary terms,
third-party notices, model licenses and telemetry disclosures. Do not bundle or
redistribute its installer in the app. Linking to Microsoft's installer and
orchestrating a user's separately approved installation is different from
shipping Foundry object code inside this GPLv3 app; do not describe it as MIT.
Local inference is not a promise that the standalone runtime emits no telemetry.
VCLibs installation/use is covered by Microsoft's Visual C++ 2015-2022 Runtime
terms, EULA `Cpp_2015-2022_ENU.1033`; those terms also prohibit redistribution as
a combined offering. Download only from the pinned Microsoft distribution for a
separately consenting user; do not embed either proprietary archive in this repo,
NuGet dependencies or the application's MSIX. Archive inspection does not add
any new executable dependency to the app.

### Supported command boundaries

The current CLI reference documents `foundry --version` and `foundry server
status` for discovery, with the actual local endpoint in server status.
Unknown/ambiguous output must not be replaced by a guessed port. Older
`foundry service ...` commands are not interchangeable with current `server ...`.
The release notes say most commands support `--output json`; exact command
support and returned schema still need version-aware observation.

For CLI 0.10.3 specifically, the tagged release documents `foundry --version`,
`foundry status`, `foundry model load <model>` and `foundry server stop`.
The public tag resolves to commit
`da50cfea8a43d22a63214f8bd9e58949a5177fb0`; its tree contains no CLI command
implementation or status JSON schema. The tagged README links the current Learn
reference instead of a version-locked command contract. Consequently, commands
below are documented candidates, **not evidence of 0.10.3 compatibility**.
Enable a version-specific lifecycle adapter only after obtaining authoritative
tagged help/schema documentation or authorized real help/status fixtures. Do not
represent synthetic parser fixtures as that evidence.

The subsequent real 0.10.3 capture now establishes the `server` command group
and its advertised `status` command. Discovery parses only command-table rows,
not descriptions/examples. The actual status response is **not** captured yet.
`model download [<model>]` accepts an alias, variant ID or model ID, `--force`
and `--output`; `model load [<model>]` has `--output` but no advertised offline
flag. Neither help output proves a version-pinned registry download, a local
directory import, or absence of implicit EP acquisition. Do not substitute the
download command for app-controlled conditional staging of the audited v4 files.

The second real capture, `lifecycle-help.json`, establishes `server start --port 0`
for an OS-assigned port and `--idle-timeout <minutes>`, but no offline flag.
`cache` advertises list/location/cd/clear, not import. Cache-directory configuration
is persistent and may restart the daemon; it is not a harmless per-request option.
Setup must not repoint a pre-existing user's cache or use `--force` to hide a restart.

**Observed startup caveat:** coordinator-owned, network-blocked daemon starts
logged `Downloading and registering EPs`, including the CPU preparation attempt.
This proves an attempted startup step, not successful acquisition. The two
executable outbound blocks do not establish isolation of hypothetical delegated
Windows-service acquisition. Product start remains disabled; selecting CPU does
not bypass the EP policy. No CLI disable-EP control is established.
The same official release lists a non-WinML x64 MSIX (28,896,573 bytes, August 7,
2026, SHA-256 `625932ADD39CE7C73F419CC87979BE6F75655482240DA36899480DC6C246579C`),
but it has not been acquired/audited for dependency closure or package replacement
and is not silently substituted into the allowlist.

The tagged public SDK v2 [local scanner](https://github.com/microsoft/Foundry-Local/blob/da50cfea8a43d22a63214f8bd9e58949a5177fb0/sdk_v2/cpp/src/catalog/local_model_scanner.cc)
recognizes nested leaves containing `genai_config.json`, `inference_model.json`
with a `Name`, and no `download.tmp`. Its
[writer](https://github.com/microsoft/Foundry-Local/blob/da50cfea8a43d22a63214f8bd9e58949a5177fb0/sdk_v2/cpp/src/download/inference_model_writer.cc)
uses the exact model ID (including version) and catalog prompt templates.
That is a concrete cache-layout candidate, **not evidence of binary parity with
the installed CLI's embedded Core**. A real blocked cache observation must
establish that contract before enabling direct registration of staged files.

**Observed cache metadata:** `cache location --output json` returned the small
`{"path":"...","userSet":false}` schema successfully under network blocking.
The adapter accepts this only on observed CLI 0.10.3, rejects unknown/duplicate
fields and unsafe paths, and does not change cache configuration. The recorded
fixture redacts only the Windows username. In contrast, `cache list --output json`
timed out after 30 seconds under the same isolation; its owned job was terminated
and rules removed. Setup must not use `cache list` as a guaranteed local-only
inventory operation or infer an empty cache from that failure.

`foundry server start --port 0`, `foundry model download <model>`, and
`foundry model load <model>` are mutations, available only after a valid setup
plan and explicit confirmation. `0` requests an OS-selected port; subsequent
status discovery must use the actual assigned endpoint. Starting a server is
not proof of a loaded model or chat/tool readiness.

**Do not use `foundry model list` as read-only discovery.** Microsoft's current
CLI documentation states its first invocation downloads execution providers.
`foundry run`/`chat` can also acquire models; they are not capability probes.
The app must not restart, uninstall, replace or adopt a pre-existing runtime
merely because a process name or endpoint responds.

WinGet documents exact version/ID/source/architecture selection,
`--skip-dependencies`, `--no-upgrade` and `--disable-interactivity`. These do not
by themselves establish authoritative artifact provenance. Never suppress hash
failures, enable local-manifest installation policy automatically, or substitute
agreement flags for the app's explicit, fully informed confirmation.
Package and source agreements are separate consent surfaces.

### Initial-model metadata research (no model acquisition)

Read-only queries to the Microsoft Azure model catalog and model-registry APIs
on September 10 returned this concrete candidate; it is **not an approved
default model or an implemented acquisition plan**:

- Asset: `azureml://registries/azureml/models/qwen2.5-0.5b-instruct-generic-cpu/versions/4`.
- Metadata resolution on 2026-09-10 returned the exact public storage container
  `https://amlwlrt4usc01.blob.core.windows.net/azureml-ab8fb672-187c-5c05-b028-d5004b5d5ae3`.
  SAS query credentials are ephemeral, memory-only and excluded from fixtures.
  Conditional acquisition must reject a different origin/container rather than
  trusting any tenant's `blob.core.windows.net` hostname.
- Variant: ONNX, RTN, CPU / `CPUExecutionProvider`; Microsoft optimization of
  Qwen2.5-0.5B-Instruct. Registry model creation: 2025-11-14T07:39:18.7283746Z.
- Reported license: Apache 2.0, with the registry's
  [model license reference](https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct/blob/main/LICENSE).
- Catalog size: 861,939,957 bytes; the actual nine-file blob listing totals
  **877,988,985 bytes**. Confirmation must use the complete file set, not assume
  catalog estimates include tokenizer/configuration files.
- Every listed blob's creation and last-modified timestamp is November 14, 2025;
  each has an ETag. No model file was fetched, including configuration/tokenizer
  files. The registry supplies `blobManifestDigest`, but the canonicalization and
  verification algorithm have not been established; blob MD5 fields are empty
  and no authoritative per-file SHA-256 manifest was returned.

The nine entries are `v4/added_tokens.json`, `v4/genai_config.json`,
`v4/merges.txt`, `v4/model.onnx`, `v4/model.onnx.data`,
`v4/special_tokens_map.json`, `v4/tokenizer.json`, `v4/tokenizer_config.json`
and `v4/vocab.json`. The registry digest observed was
`v25x2bw767hQJBKAYb7dc8TUdUm2ceps83Ku1eskomZ8TPWzUYzV8UpvQFh6teSDZrcuhIHFFTPXwtILv5d4fQ==`.
Do not guess that its encoding means SHA-256, treat an ETag as a content hash,
or persist the temporary signed storage URL returned by the registry.

An independently published per-file checksum is not universally required by
the seven-day policy. A supported alternative is to freeze the complete
version-4 path/size/date/ETag manifest, require HTTPS to the exact official
storage host/container, send `If-Match` for every approved blob, verify the
returned ETag/date/length, and compute local SHA-256 after acquisition for cache
integrity. This is **origin/TLS and conditional-version validation**, not
independent publisher-digest verification. HTTP 412 invalidates the plan; it
must not trigger refreshing pins or downloading a newer version automatically.

Microsoft's Blob REST documentation establishes these conditional-read semantics.
A metadata-only HEAD against `v4/genai_config.json` with the observed quoted
ETag `"0x8DE2350A90F3495"` returned HTTP 200, 1,517 bytes and the November 14,
2025 last-modified date; a mismatched ETag returned HTTP 412. No blob body was
read. This validates the conditional-origin design, not a model download.

The remaining gates are an approved implementation of that bounded acquisition
plan, explicit model-download permission, the selected CLI's exact
version/cache/load contract, and evidence that preparation cannot acquire
unapproved EPs or native components. A CPU catalog label and advertised tool
support do not prove those properties. The production model gate is unchanged;
no model or EP download/start/load is authorized by archive-inspection permission.

### One-click setup release gate

The intended UX prepares a bounded immutable plan, then shows one confirmation
with runtime/model identities, sizes, licenses, network effects and retained-data
policy. Only after all required artifact evidence is available may execution
install, initialize the model and save the discovered endpoint. A failed model
audit must stop before runtime installation, not leave a surprise partial install.
Runtime-only installation, if offered, requires a separately clear user choice.
Cancellation can leave an installed package or partially downloaded files;
report actual/unknown outcomes, retain user assets and never auto-uninstall.

Initial-model and EP provenance gaps are **product setup blockers**. A scripted
adapter test or a user checking an audit box cannot make unavailable model setup
work. Real-machine installation, first-run behavior and signed desktop integration
are separate evidence gaps requiring separately authorized execution.

## Acquisition boundary

Inference can stay on-device. Runtime/model/execution-provider acquisition can
require network access; registry/container operations requested by the
assistant can also use the network. Local inference is not permission to
execute those operations, and there is no silent cloud fallback.

The REST catalog reports version, size, license and target-device information.
It does not establish authoritative publication dates or an immutable,
integrity-verified dependency graph. A model alias, a local filesystem timestamp,
a license checkbox, or user attestation is not an authoritative age audit.
In-app acquisition therefore remains unavailable rather than forwarding an
unaudited `/openai/download` request.

Before acquiring any standalone runtime, prerequisite, model or EP artifact:

1. Identify the exact standalone distribution, prerequisite/native runtime, execution-provider and
   model versions, including hardware-specific variants and runtime-selected
   dependencies. Record immutable identities and authoritative source URLs.
2. Verify authoritative publication dates are at least seven days old at
   acquisition time. An unavailable or ambiguous date blocks acquisition.
3. Record licenses, redistribution obligations, download sizes and integrity
   evidence for that exact graph. Obtain explicit authorization for downloads
   and their storage location; loading must not covertly acquire providers.
4. Implement bounded progress, supported cooperative cancellation, and accurate
   retained/partial-cache outcomes. Cancellation is not deletion or rollback.
5. Exercise the signed package with the pinned prepared assets before declaring
   standalone setup compatibility, offline support or representative hardware support.

No SDK dependency is part of the revised setup architecture. Baseline app/test
dependencies remain separate from the standalone installation artifact audit.

## Authorized runtime and packaged acceptance procedure

This is a release gate, not an automatic setup script. Coordinate shared MSIX
registration with other worktrees. Do not run it without permission.

1. Obtain the actual loopback base URI and exact loaded model ID from the
   operator of a prepared host, its version/identity, and authorization for
   synthetic inference. Record asset audit references and hardware/driver
   details. Do not infer a host's provenance from an HTTP 200 response.
2. Read server status, catalog, cached and loaded-model metadata without issuing
   download, load, unload or generation requests. A missing route is an
   unsupported host contract, not an empty cache. Confirm model identity.
3. Exercise diagnosis with the existing OpenAI-compatible provider against that
   explicit endpoint/model, then the dedicated Foundry provider. Verify strict
   diagnosis JSON, tools, structured JSON and streaming independently.
4. Use synthetic read-only tool calls first. Verify exact result IDs, multiple
   calls, fragmented streaming and cancellation. For mutation coverage, obtain
   separate authorization for a disposable workload and confirm original-plan
   approval, completed/partial/unknown outcomes, and no replay after failure.
5. Confirm provider switches do not transfer history or credentials. Restart the
   host to check endpoint lifetime and invalidate old capability evidence. An
   unavailable local host must not contact any cloud endpoint.
6. With separately authorized prepared-model lifecycle operations, measure cold
   and warm starts, cancellation, TTL/manual memory release, and missing assets.
   Confirm no network acquisition occurs. Never unload unrelated models.
7. In an authorized isolated network environment, exercise cached inference
   offline and document missing execution-provider/model guidance.
8. Build/sign/deploy the x64 MSIX using the existing packaging workflow only
   after deployment approval. Repeat the preceding cases with real package
   identity, restart the app, and inspect external CLI discovery, setup staging
   paths, runtime installation consent and REST behavior.
   Record CPU coverage and each actually supported GPU/NPU configuration
   separately; unavailable hardware is an explicit untested case.

Record results per case, runtime/asset/package versions, actual capabilities,
timings, observed memory release, cancellation outcomes and remaining failures.
Do not label a deterministic fixture run as this runtime acceptance evidence.

## Authoritative references

Reviewed on 2026-09-09 and 2026-09-10:

- [Standalone CLI reference](https://learn.microsoft.com/en-us/azure/foundry-local/reference/reference-cli):
  server/model commands, dynamic endpoints and explicit warning about EP
  acquisition during first-time model listing.
- [CLI 0.10.3 release](https://github.com/microsoft/Foundry-Local/releases/tag/cli-preview-0.10.3)
  and [asset metadata](https://github.com/microsoft/Foundry-Local/releases/expanded_assets/cli-preview-0.10.3):
  installer names, publication dates, hashes and supported-version notes.
- [Foundry winget installer manifest](https://github.com/microsoft/winget-pkgs/blob/master/manifests/m/Microsoft/FoundryLocal/0.10.3.0/Microsoft.FoundryLocal.installer.yaml)
  and [VCLibs prerequisite manifest](https://github.com/microsoft/winget-pkgs/blob/master/manifests/m/Microsoft/VCLibs/Desktop/14/14.0.33728.0/Microsoft.VCLibs.Desktop.14.installer.yaml):
  source artifact hashes and declared prerequisites. These source pages are
  discovery evidence, not permission to trust future edits to mutable branches.
- [Tagged CLI license](https://github.com/microsoft/Foundry-Local/blob/cli-preview-0.10.3/LICENSE)
  and [WinGet install options](https://learn.microsoft.com/en-us/windows/package-manager/winget/install):
  separate installation agreements and guarded command construction.
- [Visual C++ Runtime installation/use terms](https://visualstudio.microsoft.com/license-terms/vs2022-cruntime/):
  the page embeds Microsoft's published Visual C++ 2015-2022 license document.
- [Windows ML self-contained deployment](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/distributing-your-app):
  packaged and unpackaged modes, bundled runtime versus separately acquired EPs.
- [Public catalog query URL](https://api.catalog.azureml.ms/asset-gallery/v1.0/models)
  and [tagged public registry client source](https://github.com/microsoft/Foundry-Local/blob/da50cfea8a43d22a63214f8bd9e58949a5177fb0/sdk_v2/cpp/src/download/model_registry_client.cc):
  used only for metadata research, not evidence that SDK v2 is the CLI 0.10.3
  command implementation.
- [Azure Blob conditional reads](https://learn.microsoft.com/en-us/rest/api/storageservices/specifying-conditional-headers-for-blob-service-operations)
  and [Get Blob](https://learn.microsoft.com/en-us/rest/api/storageservices/get-blob):
  authoritative `If-Match`, HTTP 412 and response ETag/date/size semantics.

- [Current SDK reference](https://learn.microsoft.com/en-us/azure/foundry-local/reference/reference-sdk-current)
  (page date 2026-08-05): Windows package selection, hardware and EP management,
  optional REST hosting, and console-specific project settings.
- [Inference SDK integration](https://learn.microsoft.com/en-us/azure/foundry-local/how-to/how-to-integrate-with-inference-sdks)
  (page date 2026-05-11): actual host URL and model ID, explicit SDK
  download/load/start/unload/stop sequence.
- [Tool-calling assistant tutorial](https://learn.microsoft.com/en-us/azure/foundry-local/tutorials/tutorial-build-tool-calling-assistant)
  (page date 2026-06-18): model-requested tool invocation and results. The app's
  approval and validation rules remain authoritative, not the sample executor.
- [REST API reference](https://learn.microsoft.com/en-us/azure/foundry-local/reference/reference-rest)
  (page date 2026-05-15): preview compatibility warning, inference/SSE, catalog,
  cached/loaded models and explicit lifecycle routes. Despite their HTTP GET
  verb, `/openai/load/...` and `/openai/unload/...` are mutations.
