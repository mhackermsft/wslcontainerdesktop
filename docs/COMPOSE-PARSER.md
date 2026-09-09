# Compose configuration semantics

## Parser decision (issue #82; recorded before dependency change)

Retaining the indentation-based reader would avoid a dependency, but implementing YAML's
quoted escapes, multiline flow values, block scalar indentation/folding/chomping, aliases and
merge keys correctly would effectively mean maintaining a second YAML parser. Its early
comment removal/interpolation also changes YAML structure and loses blank lines.

Use **YamlDotNet 16.3.0** (MIT) for YAML syntax, not Compose semantics. Consume its event stream
into a bounded private value tree: no CLR type activation, no arbitrary tags, no external
resolution. Keep interpolation, Compose normalization and resource-key merges application-owned.
Reject malformed documents, duplicate explicit keys, unsupported tags and alias cycles with
value-free location diagnostics. This is preferable to accepting a misleading partial document.
The persisted `ComposeProject`/`RunContainerOptions` schema does not change.

### Dependency publication audit

Before acquisition, NuGet's authoritative
[v2 package metadata](https://www.nuget.org/api/v2/Packages(Id='YamlDotNet',Version='16.3.0'))
was queried on 2026-09-09. `Published` = **2024-12-23T20:20:17.497+00:00**.
Dependencies = `::net47|::net6.0|::net8.0|::netstandard2.0|::netstandard2.1`:
all groups are empty, including the net8.0 asset selected by both .NET 10 targets.
Thus this addition has **no transitive package acquisitions**, and exceeds the seven-day policy.
The existing test graph is independently checked against the foundation's publication audit;
baseline restoration uses only the already-cached graph after `--no-restore` reports missing assets.

## Semantic reference

The target remains Compose specification commit
`c0c3dba71a73260cf9649e05370dcad6fe29e11c`, especially chapters 12 (interpolation) and 13 (merge),
and the conformance corpus's separately pinned reference CLI v2.39.4.
Expectations originated as spec-derived projections. The publication integration now includes
actual pinned config-only captures for all 21 cases, with explicit observed divergences and
**no runtime certification**.
The documented `+` / `:+` operators and environment precedence also follow Docker's
[interpolation guide](https://docs.docker.com/compose/how-tos/environment-variables/variable-interpolation/).
The importer API's explicit environment overlay is an application facility, not an emulation of
Docker CLI argument/PWD/`--env-file` selection. YAML substitution is value-only after decoding;
single-quoted **dotenv** literal rules must not be confused with YAML scalar decoding.
Review of equivalent mount targets, default port bindings and multiple host addresses also uses
the reference CLI's compose-go v2.9.0
[merge rules](https://github.com/compose-spec/compose-go/blob/v2.9.0/override/merge.go) and
[resource identity rules](https://github.com/compose-spec/compose-go/blob/v2.9.0/override/uncity.go).
The same pinned reference's
[include loader](https://github.com/compose-spec/compose-go/blob/v2.9.0/loader/include.go)
confirms identical duplicate-resource acceptance (deep equality), conflict rejection without
merging, and parent-relative explicit include `project_directory`/`env_file` resolution.

## Implemented pipeline and file ownership

| File / area | Responsibility |
|---|---|
| `Services/ComposeImporter.Yaml.cs` | YamlDotNet event reader, private mapping/sequence/scalar/null tree, YAML merge-key precedence, bounded aliases, value-only interpolation and source locations |
| `Services/ComposeInterpolation.cs` | Balanced nested expansion, six operators, lazy branch evaluation with syntax validation, literal dollars, optional-variable warnings, required-variable errors |
| `Services/ComposeImporter.Merge.cs` | Short/long-form normalization, distinct override/inheritance merging, unique resources, reset/override application, supported-field shape checks |
| `Services/ComposeImporter.Files.cs` | Per-import bounded file graph, independent includes, local/external inheritance, source/project path ownership, required/optional reads and file-resource validation |
| `Services/ComposeImporter.cs` | Environment snapshot and projection to the existing saved model; later env-file precedence; strict simple dotenv assignments; unsupported resource-option warnings |
| `Services/ComposeConfigurationException.cs` | User-facing error contract containing no source values, custom required-error text, parser message or inner exception |
| `Models/RunContainerOptions.cs` | Existing command-string tokenizer now retains explicitly quoted empty arguments; schema unchanged |
| `ViewModels/ComposeViewModel.cs`, `TemplatesViewModel.cs` | Import failure is reported as failure; invalid edited template YAML cannot replace saved configuration or produce a misleading launched status |
| `Dialogs/ImportComposeDialog.cs`, `Views/ComposePage.xaml.cs` | Browse/navigation async work routes through `UiSafe`; file-read logging excludes user-controlled paths |
| Both `.csproj` files | Pin the audited YAML package; app default glob and explicit test links compile the parser partials, including the file graph |
| Conformance worker/projection/tests + `Fixtures/Compose/v1` | Keep the original 14 cases, remove resolved divergence exemptions, add six fixture cases and a failure envelope; projection adds entrypoint and secret/config source-target pairs |
| `ComposeSemanticsTests.cs` | Operator/invalid-input matrices, merge/argv/persistence regressions and resource-bound checks |
| `ComposeFileGraphTests.cs` | Synthetic local file-graph/conflict/scoping matrices; missing/unreadable/malformed/unsupported inputs; Windows lexical paths; graph limits and parser-before-save preservation |
| README, architecture and conformance documentation | Exact/partial/unsupported claims and honest evidence provenance |

### Merge contract

Each file is decoded, interpolated and normalized before cross-file merging. Environment, labels
and build arguments normalize mixed list/map forms to mappings; named networks/dependencies
normalize list forms; scalar DNS/env-file/tmpfs forms normalize to sequences. Top-level
network/volume labels also normalize before merging.

Mappings merge recursively and ordinary sequences append in source order (duplicates are not
arbitrarily removed). Ports use the tuple **host IP, target, published, protocol**; volumes,
secrets and configs use **target**. Short and long forms share identities. Matching resources
merge their mappings while preserving unrelated entries and base ordering. Numeric port ranges
are expanded within limits; IPv6 brackets and numeric/default TCP spelling do not create false
duplicate identities. Omitted host IP matches explicit `0.0.0.0`; short secret/config targets
match their effective absolute destinations. `extra_hosts` keeps distinct hostname/address pairs,
including multiple addresses per hostname across mixed list/mapping forms.
Command, entrypoint and healthcheck.test always replace. Explicit null
command/entrypoint and empty lists/strings remain distinct in the saved model.

`!reset` removes the tagged mapping entry, and `!override` bypasses normal merging. Tags on
document roots or sequence items are explicitly rejected rather than silently interpreted as
ordinary values. YAML `<<` is a separate operation: explicit keys win regardless of placement,
and earlier mappings in a merge sequence win over later ones. Duplicate *explicit* keys fail.
Pending `!reset`/`!override` markers survive intervening override layers until inheritance is
resolved; an unrelated overlay must not resurrect inherited commands, environment or ports.
Tagged collections also retain their replacement intent when later override layers add entries.

### File-graph and inheritance contract (#83)

The main input loads the first present recognized sibling override. Included projects do **not**
auto-discover sibling overrides: short include paths load exactly that file, while long
`path: [base, override, ...]` lists apply ordinary override merging in the declared order.
Repeating a file within that path list is another merge layer, not a recursive include cycle.
Includes are resolved independently before their resources are copied into the parent; conflicting
services, networks, volumes, secrets or configs reject rather than merge. Identical duplicate
resource definitions are accepted idempotently, including shared diamond-include
leaves; a repeated noncyclic inclusion is not itself a conflict. Each included project
resolves its own local inheritance namespace; an included service cannot satisfy a parent's
local `extends` reference. Unknown top-level options in included files retain warnings prefixed
with their logical source context rather than silently disappearing.

Include paths are relative to the parent project directory. The included project defaults to the
first included file's directory; explicit `project_directory` is relative to the **parent**, not
the included file. All path-list layers use the included project's directory. Nested include
resolution uses that same project ownership. Relative binds, build contexts, service env files
and config/secret source files are rebased while decoding their owning document.
An omitted build context stays deferred through override/inheritance merging: child build arguments
or a Dockerfile alone must not replace an inherited explicit context with `.`. An inherited empty
`build: {}` defaults to its external source directory. Private default-context metadata is
materialized only after inheritance/tags and before included resources are copied.

Included interpolation defaults come from that project's `.env`, or its explicitly listed
include `env_file` inputs (resolved relative to the **parent** project). Later explicit files
override earlier ones. The parent's complete interpolation snapshot wins over all child defaults,
including empty values. Child-only entries flow down to nested includes but never back into
the parent or across siblings. Explicit include env files replace default child `.env` discovery.

`extends` accepts a mapping with `service` and optional local `file`. An external service's relative
paths are rebased to **its source file directory**, including successive inheritance hops.
It uses the caller's interpolation snapshot, not its own sibling `.env`. External top-level
resources are not imported; required secret/config references must be declared in the resulting
project. Inheritance errors, missing target services and local/external/mixed cycles fail rather
than dropping the service. Lexically normalized absolute file identities detect `./`, `..` and,
on Windows, case aliases. This is lexical detection, not a claim of filesystem/reparse-point
canonicalization.

Inheritance is **not** override merging: volume/device entries replace by target instead of
merging old entry fields into the child. Spec-listed unique sequences (including ports, configs
and secrets) remove identical entries; DNS, DNS search, env-file and tmpfs lists retain duplicate
entries in order. Different entries sharing a config/secret target are not override-style
target-key merges. Mapping-valued inheritance fields use their field-specific rules.
Disabling an inherited healthcheck is rejected unless the base already disables it.

Required file inputs must exist and be readable; only `env_file.required: false` permits a
missing service env file. Missing default `.env`/main sibling override files are optional;
present unreadable or malformed text is never silently skipped. Include env files are required.
Referenced text is decoded strictly, and malformed YAML/dotenv reports a logical source/key
breadcrumb. File-backed secrets/configs are checked for readability without interpreting or
retaining their bytes, so binary payloads are valid. Explicit external definitions need no local
file and cannot also specify `file`.

Remote required inputs, unknown include/extends/env-file options, drive-relative paths and
relative required files without a source directory reject. Windows absolute drive and UNC bind
sources are normalized lexically, without reading them. This does not certify mount availability.
File errors distinguish missing/unreadable/malformed/unsupported inputs using logical source
ordinals and keys, never OS exception text, custom paths or file contents. Resource ordinals restart
within each kind (`services`, `networks`, `volumes`, `secrets`, `configs`); source assignment traverses
each bounded-tree node once. Required host-file paths are lexically canonicalized before reading,
whereas absolute Linux engine bind/build paths are retained without host rebasing or probing.

### Environment and diagnostics contract

The importer snapshots **explicit entries > process environment > sibling `.env`**, case-sensitively;
empty is a present value. No process environment is modified. Container environment precedence is
separate: later service env files override earlier ones and inline environment entries override
all files, including empty and bare/null entries. Service env files are not interpolation sources.

Expansion happens once on decoded values, never YAML mapping keys or substituted environment
text. List `KEY=VALUE` syntax is expanded before key/value normalization. `$$` becomes a literal
dollar; nested fallback/alternative operands are evaluated only when selected, but malformed
syntax is rejected even in unused branches. Required failures name the validated variable and
source line/column and suggest environment sources. Custom error operands and values are
deliberately omitted. Optional unset substitutions become empty with warnings. Referenced-file
errors are identified as such, without exposing a user-controlled file path.

The parser performs **no engine calls, package activation or persistence**. Both Compose view-model
import paths catch errors before `_store.Save` or supervision. Template configuration saving is
after successful validation/import; false from `ImportAndUpAsync` means no project was imported
(it is not a new runtime success guarantee). Assistant deployment and dev-container Compose
paths also parse before their save/supervisor steps; configuration exceptions propagate to
their existing error boundaries.

### Limits and deliberately partial areas

- One YAML document per file; string scalar keys; no arbitrary tagged/CLR objects. Syntax bounds:
  2,000,000 input characters, 64 nesting levels, 100,000 parsed/expanded nodes, 2,000,000 characters
  per expanded value and 8,000,000 expanded scalar characters per document. Normalization permits
  at most 100,000 expanded resources/document and 10,001 ports/range.
- Per-import graph bounds: 256 decoded documents and 64 include/inheritance levels; referenced
  text inputs are limited to 8 MB. Limits reject with source context rather than returning a
  truncated project. Inputs use filesystem reads; there are no engine calls or HTTP/Git fetches.
- This is not full Compose schema validation or complete YAML scalar-schema coercion. Supported
  model fields use string-based conversion; arbitrary YAML types such as timestamps/binary/sets
  are rejected. Invalid supported-field shapes reject instead of silently dropping services.
- Actual pinned CLI captures expose three additional differences, without changing the parser:
  nested required operands in unused default branches reject in the CLI but are lazy in the app;
  the CLI de-duplicates merged DNS; equivalent short/numeric-long published ports remain duplicated
  in the captured CLI but merge by normalized tuple in the app. See the isolated negative-reference
  fixture and `override-unique` divergence records; these are not equivalence claims.
- Dotenv remains a simple line-based `KEY=VALUE` reader with basic outer-quote removal and
  whole-line comments. Invalid assignments, whitespace-containing keys and unmatched outer
  quotes reject. It does not implement full dotenv multiline/escape/inline-comment/interpolation
  semantics or bare unsets. Service env-file `format` and non-file secret/config sources reject.
  CLI `--env-file`, PWD selection and global project-directory override emulation are not implemented;
  the long include `project_directory` option is supported.
- Advanced long-form port/mount/secret/config options warn; long mount types other than bind/volume
  and unsupported short mount modes reject. No new engine flags were introduced.
- Argv construction retains empty tokens, mixed quotes, newlines and Windows paths without adding
  shell escaping to user data. Clearing image command/entrypoint defaults at engine runtime is
  still not certified. No saved model schema migration was introduced.
- Reconciliation (#85), replicas (#84), preview (#87) and engine behavior/capability tri-state
  are unchanged. WSLC 2.9.9.0 is still the supported baseline; no runtime certification is claimed.

## Maintenance invariants

1. `ReadComposeYaml(text, environment, warnings)` is pure except for safe diagnostics. File reads
   state their purpose/requiredness; required or malformed inputs must propagate, never become null.
2. `MergeMappings` carries structural context so coincidentally named labels/environment keys
   cannot trigger resource or command exceptions. Keep inheritance-specific semantics separate.
3. Preserve **decode → per-file interpolate → normalize/rebase → graph/override merge →
   resolve inheritance → apply tags → validate → project**. Do not interpolate twice or erase
   pending reset/override markers before inheritance.
4. File identities, environments, sources, cycle tracking and bounds are per import, not global
   caches; graph loading must not introduce any engine, UI, activation or persistence dependency.
5. The corpus stays version 1 with all 20 spec-derived cases. `appError` means rejection, not a
   successful config snapshot; `forbiddenDiagnostics` checks redaction. Resolved include and
   missing-env-file gaps have no remaining divergence exemption.

## Validation record for #82 (before the file-graph layer)

- Began with `dotnet test ... --no-restore`; both targets reported **NETSDK1004** (missing assets).
- Restored the baseline only from the existing local package cache, then compared all resolved
  versions against the foundation's authoritative `test-dependency-publications.json` audit.
- After recording the parser decision and querying NuGet publication metadata, restored the
  changed test manifest. The actual graph has **17 libraries**: the audited 16-library baseline
  plus YamlDotNet 16.3.0. The Windows download dependency
  `Microsoft.Windows.SDK.NET.Ref/10.0.26100.57` also matches the baseline publication audit
  (published 2024-11-12 UTC). Both targets select `lib/net8.0/YamlDotNet.dll`, with no transitives.
- Focused validation command (repository root):

  ```powershell
  dotnet test tests\WslContainerDesktop.Tests\WslContainerDesktop.Tests.csproj `
    -c Debug -p:Platform=x64 --no-restore `
    --filter 'FullyQualifiedName~Compose|FullyQualifiedName~NativeHealth|FullyQualifiedName~ContainerConfigImporter' `
    --logger 'console;verbosity=minimal'
  ```

  **net10.0: 220 passed; net10.0-windows10.0.26100.0: 251 passed; zero failures,
  skips or compiler warnings.** This covers all 20 corpus cases plus focused parser, native-health,
  network-model/orchestrator, standalone import/argument construction and Windows supervisor regressions.
- `git diff --check` passed.
- UI call ordering and all `ImportAndUpAsync` callers were inspected. The test project does not
  compile the WinUI views/view-models; no packaged app build/run/deploy or UI smoke was performed,
  as required. No Docker, WSL, model/workload or engine operations were executed.

### Publication integration, 2026-09-10

Rebased on the reviewed conformance foundation and retained its real captures, UTF8-LF hashes,
config dollar decoding and unexecuted consent-gated runtime harness. Regenerated 21 cases using
the hash-verified authorized standalone v2.39.4 binary, with only `version`/`config` operations.
The negative nested-required case is isolated from the successful nested-expression fixture;
DNS and mixed-port differences retain exact existing app expectations in explicit divergence
records. No production parser semantics changed during this integration.

The narrow reference projection additionally decodes dollar escapes in environment keys and
maps omitted reset mount lists to empty lists, without making other missing selected keys valid.
An inherited AI provider contract test was adapted to the current `AiChatRequest` signature.
Targeted `ComposeConformanceTests`, `ComposeSemanticsTests` and that three-provider contract test
passed **106 tests on each target**, zero warnings, using `--no-restore -p:Platform=x64
-p:CopilotSkipCliDownload=true`. No runtime harness, engine workload or packaged deployment ran.
## File-graph validation (#83)

The new focused graph tests use synthetic filesystem inputs under uniquely owned test-output
directories inside the checkout, always cleaned up after each case. Sharing violations use
locked local files; Windows drive/UNC bind tests are lexical only and never probe a live share.
The 20-case corpus is preserved, with include scoping now expected to succeed and missing
required env files expected to reject. Resource-merge fixtures declare explicit synthetic
external secrets/configs instead of relying on invalid empty definitions.
Additional regressions cover pending tags with inheritance across main/include override layers,
malformed included resource lists, and required env files containing unsupported bare keys.
Their diagnostics assert source/key ordinals while excluding resource names, paths and contents.

Both `ComposeViewModel.ImportProjectFromYamlAsync` and `ImportAndUpAsync` parse inside their
error boundary before namespacing, `_store.Save`, refresh or supervision; a parser failure returns
without replacing a saved project. The regression uses the existing `NetworkTestProxy` store
double to verify parser-before-save ordering and unchanged serialized prior state. It does not
compile or drive the WinUI view-model, and therefore is not a UI integration test.

The initial `--no-restore` run reported missing assets. Restoration then used only the existing
local package cache, with all 17 resolved libraries compared to the recorded authoritative
publication audit (including YamlDotNet above). The Windows SDK download reference remains the
audited `10.0.26100.57`; there were no new versions or dependency acquisitions.

The combined focused command shown above passed **348 tests on net10.0** and **379 tests on
net10.0-windows10.0.26100.0**, with zero failures, skips or compiler warnings. This includes
all 20 corpus cases, 128 new file-graph cases, existing Compose coverage, native-health and
standalone container-import regressions. The app's default compile glob includes the new
partial file; no UI source changed. `git diff --check` passed. No application build/deployment,
engine/workload/provider calls or reference CLI captures were performed.
