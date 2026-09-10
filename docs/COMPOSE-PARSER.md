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

## Implemented pipeline and file ownership

| File / area | Responsibility |
|---|---|
| `Services/ComposeImporter.Yaml.cs` | YamlDotNet event reader, private mapping/sequence/scalar/null tree, YAML merge-key precedence, bounded aliases, value-only interpolation and source locations |
| `Services/ComposeInterpolation.cs` | Balanced nested expansion, six operators, lazy branch evaluation with syntax validation, literal dollars, optional-variable warnings, required-variable errors |
| `Services/ComposeImporter.Merge.cs` | Short/long-form normalization, structural Compose merging, unique resources, reset/override application, supported-field shape checks |
| `Services/ComposeImporter.cs` | File/environment orchestration and projection to the existing saved model; later env-file precedence; no catch-and-ignore of malformed referenced YAML; unsupported resource-option warnings |
| `Services/ComposeConfigurationException.cs` | User-facing error contract containing no source values, custom required-error text, parser message or inner exception |
| `Models/RunContainerOptions.cs` | Existing command-string tokenizer now retains explicitly quoted empty arguments; schema unchanged |
| `ViewModels/ComposeViewModel.cs`, `TemplatesViewModel.cs` | Import failure is reported as failure; invalid edited template YAML cannot replace saved configuration or produce a misleading launched status |
| `Dialogs/ImportComposeDialog.cs`, `Views/ComposePage.xaml.cs` | Browse/navigation async work routes through `UiSafe`; file-read logging excludes user-controlled paths |
| Both `.csproj` files | Pin the audited YAML package; test project links all four new production source files |
| Conformance worker/projection/tests + `Fixtures/Compose/v1` | Keep the original 14 cases, remove resolved divergence exemptions, add six fixture cases and a failure envelope; projection adds entrypoint and secret/config source-target pairs |
| `ComposeSemanticsTests.cs` | Operator/invalid-input matrices, merge/argv/persistence regressions and resource-bound checks |
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
- This is not full Compose schema validation or complete YAML scalar-schema coercion. Supported
  model fields use string-based conversion; arbitrary YAML types such as timestamps/binary/sets
  are rejected. Invalid supported-field shapes reject instead of silently dropping services.
- Dotenv remains a simple line-based `KEY=VALUE` reader with basic outer-quote removal. It does
  not implement the full dotenv multiline/escape/comment/interpolation grammar or bare unsets.
  CLI `--env-file`, PWD selection and project-directory overrides are not implemented.
- Include/extends required/missing-file, independent-project/conflict, recursive path ownership
  and extends-specific deduplication rules remain #83 work. A present malformed referenced file
  now fails, but a missing required `env_file` still has an explicit conformance diagnostic gap.
- Actual pinned CLI captures expose three additional differences, without changing the parser:
  nested required operands in unused default branches reject in the CLI but are lazy in the app;
  the CLI de-duplicates merged DNS; equivalent short/numeric-long published ports remain duplicated
  in the captured CLI but merge by normalized tuple in the app. See the isolated negative-reference
  fixture and `override-unique` divergence records; these are not equivalence claims.
- Advanced long-form port/mount/secret/config options warn; long mount types other than bind/volume
  and unsupported short mount modes reject. No new engine flags were introduced.
- Argv construction retains empty tokens, mixed quotes, newlines and Windows paths without adding
  shell escaping to user data. Clearing image command/entrypoint defaults at engine runtime is
  still not certified. No saved model schema migration was introduced.
- Reconciliation (#85), replicas (#84), preview (#87) and engine behavior/capability tri-state
  are unchanged. WSLC 2.9.9.0 is still the supported baseline; no runtime certification is claimed.

## Reusable contracts for #83

1. `ReadComposeYaml(text, environment, warnings)` returns the private normalized value tree.
   It is pure except for appending safe diagnostics. Required/malformed errors must propagate;
   never wrap this call in a generic "return null" catch.
2. `LoadComposeRoot` currently returns null only for an absent optional file and throws for
   present unreadable/malformed files. #83 should make file purpose/requiredness and path ownership
   explicit, rather than weakening the parser's failure behavior.
3. `MergeMappings` carries a structural context (`service`, `service.healthcheck`, etc.), so a
   coincidentally named label/environment key cannot trigger command/resource exceptions.
   Extends currently reuses it; implement extends-specific deduplication separately instead of
   changing ordinary override sequence append behavior.
4. Preserve the order **decode → per-file interpolate → normalize → compose file graph/merge →
   resolve inheritance → apply tags → validate → project**. Do not interpolate merged values twice
   or lose pending reset/override markers before resolving inheritance.
5. The caller supplies an environment snapshot and diagnostic list throughout file loading.
   A future file-graph context can own these alongside base directories/cycle tracking without
   involving any engine or UI objects.
6. The corpus still has version 1 and honest spec-derived provenance. `appError` is a rejection
   assertion, not a successful config snapshot; `forbiddenDiagnostics` checks redaction.
   Missing-env-file/include divergences remain available to replace with #83 expectations.

## Validation record

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
