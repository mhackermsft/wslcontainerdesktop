# Foundry Local integration: scope and compatibility

**Revised product scope, 2026-09-10:** connect to a Microsoft Foundry Local
instance running on this PC, plus app-guided, explicitly confirmed installation
of the standalone runtime and an initial model. This supersedes the earlier
in-process WinML/native-broker proposal. The app will not bundle a Foundry SDK,
host native inference itself, or treat a new broker as an acceptance requirement.

Issue #92 is **not complete** until usable provenance-gated setup and the
runtime/signed-package acceptance checks below have been delivered. Working
model setup is a product requirement, not something a smoke test can replace.
Deterministic HTTP fixtures establish adapter
behavior, not compatibility with a particular installed Foundry release, model,
execution provider or device.

## Implemented REST slice

Settings offers **Foundry Local (local REST)** with separately persisted endpoint
and model fields. The endpoint must include an explicit port and be loopback,
optionally ending in `/v1`. It has no default. The dedicated HTTP transport
disables redirects, proxies, cookies and integrated credentials; `localhost` is
resolved to the loopback IP rather than following DNS. No saved OpenAI key is
used. A correctly configured loopback host must still be trusted by its operator.

**Refresh metadata** reads documented server status, catalog, cached-model and
loaded-model routes. It shows actual IDs plus reported version, size, license,
device and execution-provider hints. Missing fields remain unknown; an
unrecognized or failing route is not a successful empty inventory. Neither
refresh nor construction of the settings view model starts inference or load.
The catalog view is bounded and sanitized, not a hardware recommendation engine.

Diagnosis and assistant turns require an exact cached, loaded ONNX catalog model.
Capability tests independently observe chat, tool calls, tool-result acceptance,
JSON and streaming; an advertised tool flag cannot establish positive support.
The provider checks current configuration and observed runtime/model identity
before requests and tool callbacks. Responses must identify the requested model.
History, bounded stream parsing, original action approval, cancellation and
redaction use the shared assistant contracts.

**In-app model loading is blocked**, even for cached data: loading can cause an
external runtime to acquire an execution provider, and this integration cannot
authoritatively verify that preparation. Prepare and load assets externally only
under an independently audited, authorized procedure. A warning or checkbox
cannot waive the acquisition policy.

**Unload model** deliberately requests only the selected model's documented
non-forced unload operation, then reads actual state. It does not override the
runtime's TTL, stop the shared host, unload other models or delete model files.
If memory release is not observed, the result does not claim success. Cancellation
or timeout leaves state uncertain until refreshed. Runtime changes invalidate
capability observations before and after the operation.

**Memory ownership policy:** the external host owns its idle TTL and allocation
policy. The app never keeps a model alive, automatically unloads models on exit,
or promises an immediate release following cancellation. The standalone
runtime owns its model TTL; setup does not grant ownership of pre-existing
processes or permission to stop/uninstall them.

## Investigation record (2026-09-09)

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

### Opt-in automated HTTP checks

`FoundryLocalRuntimeOptInTests` is excluded by default. It has two independent
gates, each requiring Windows and nonempty `WSLC_FOUNDRY_LOCAL_ENDPOINT` and
`WSLC_FOUNDRY_LOCAL_MODEL` environment variables:

- `WSLC_FOUNDRY_LOCAL_METADATA_TESTS=1` permits only metadata reads.
- `WSLC_FOUNDRY_LOCAL_INFERENCE_TESTS=1` permits synthetic capability probes and
  diagnosis against an already cached, loaded model.

Set a gate only after obtaining permission for its operations and satisfying
the prepared-host prerequisites. The inference gate must not be enabled merely
to discover a usable endpoint/model. Neither test installs or starts a host,
loads/unloads models, deploys MSIX, or invokes application tools. Both gates
were explicitly disabled during deterministic validation for this change.
These tests are HTTP integration checks, not packaged or hardware acceptance.

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

**Implemented but not enabled:** `FoundryLocalInstaller` verifies prepared local
MSIX/APPX sizes and SHA-256 hashes and holds the files open through a separate
runtime-only confirmation and fixed `Add-AppxPackage` process invocation.
It refuses known existing Foundry installations, checks the selected dependency
and installed identity, invalidates capabilities around attempted deployment,
and reports cancellation/failure without promising rollback. This is a
prepared-package adapter, **not a downloader or working setup feature**.
The production artifact allowlist is empty; no installation button is exposed,
and the adapter cannot install from user-entered provenance records.

**Still unimplemented:** the authorized download/staging flow, a complete
approved installation graph, hardware-aware initial-model choices and exact
model/EP audit, and version-proven download/load/start orchestration with
partial-cache recovery. Those need implementation after the prerequisites are
established; neither adapter tests nor a runtime smoke replaces that work.
The standalone MSIX is per-user; this still installs Foundry on the PC and is
not itself a blocker merely because it is not an all-users installer.

The documented standalone candidate is **CLI 0.10.3**, package version
**0.10.3.0**, not SDK 2.0.1. Its release notes say it embeds SDK 1.2.4.
Read-only research identified:

| Artifact | Evidence | Limitation |
| --- | --- | --- |
| `foundry-0.10.3-win-x64-winml.msix` | Microsoft release asset, published 2026-08-07T21:38:48Z; 29,982,055 bytes; SHA-256 `86A01C52265BD9C9167C1F8A04F34621A2A63EC5C8276166C2EECC8C6A56553F`. Microsoft winget manifest independently supplies the same hash. | No installer was downloaded or installed here. Artifact metadata does not prove that first-run EP downloads are absent. |
| `Microsoft.VCLibs.Desktop.14` | Foundry winget manifest requires at least `14.0.33728.0`; per-user x64 runtime package. | A minimum version is not an approved future dependency version. Do not let winget acquire an unpinned newer prerequisite silently. |
| Initial model | REST catalog reports names, versions, size, license and device hints. | No approved exact model variant/file manifest with authoritative publication dates and hashes is available. No sample alias is an approved default. |
| Dynamic EPs | CLI docs describe first-run acquisition and automatic plugin updates. | Exact prospective binaries, dependencies, dates and integrity evidence must be verified before acquisition. A cached model does not prove EP readiness. |

The installer meets a conservative 2026-09-03T00:00:00Z publication cutoff, but
this is **not approval of the entire installation/model/EP graph**.
The VCLibs winget manifest identifies a nested x64 APPX inside
`DesktopAppInstaller_Dependencies.zip` from winget-cli release `v1.9.25180`,
SHA-256 `EEBA62F08531C8669B3E5FB895CE8800AE66D798739CDA2B4AEF2A1D80A5F3C5`.
The official release asset metadata dates that ZIP to
2024-11-01T16:37:27Z (approximately 47.9 MB), so its distribution age passes.
The nested package's license/identity and the complete installation dependency
closure still need verification before new prerequisite acquisition, rather than
approving a mutable future dependency by minimum version.

The CLI's license permits installation/use but has separate proprietary terms,
third-party notices, model licenses and telemetry disclosures. Do not bundle or
redistribute its installer in the app. Linking to Microsoft's installer and
orchestrating a user's separately approved installation is different from
shipping Foundry object code inside this GPLv3 app; do not describe it as MIT.
Local inference is not a promise that the standalone runtime emits no telemetry.

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
