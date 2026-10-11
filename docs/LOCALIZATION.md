# UI localization

The application supports `en-US` and `zh-Hans`. Keep both `Resources.resw` key sets
and format placeholder sets identical. Resource keys must also be unique ignoring
case because Windows PRI resource names are case insensitive. Use full sentences
with numbered placeholders instead of translating fragments of a message.

## XAML and generated display text

For static XAML, give each control a compatible `x:Uid` and matching
`i18n:Localization.Uid` (`xmlns:i18n="using:WslContainerDesktop.Helpers"`). The
attached property updates the existing control when the language changes. Share a
UID only between controls of the same type with compatible resource properties.
Attached-property resource names use WinUI's fully qualified syntax, for example
`ButtonHelp.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip`.

Use `UiText.Get(key, englishFallback, args)` for generated display text. Subscribe to
`UiText.LanguageChanged` to refresh computed labels or one-time bindings, and release
page event subscriptions when navigation leaves a page. `UiText.Translate` projects
complete known application messages; `TranslateLines` handles known guidance lines.
Unknown text is retained. Format templates must include literal letters as well as
placeholders so they cannot match and replace arbitrary external output.

Include tooltips, InfoBar messages, accessibility names, dialogs and collapsed picker
labels in localization changes. Picker items use stable values and observable labels
so updating a translated label cannot clear selection. Language changes refresh
existing controls and cached pages while preserving input and operation state.

## Preserve data and technical values

Keep protocol values separate from labels. Never translate commands, paths, IDs,
resource names, enum values, JSON/YAML, raw logs, user templates, imported descriptions
or assistant conversation content. Translate only the display fields of built-in
templates. Exclude computed localized presentation fields from serialized evidence.
Preserve established technical/product names including WSL, Docker, Compose,
Kubernetes, Ollama, API, JSON, YAML and Dev Container.

## Resolve the saved language preference

An empty stored preference means System default. Resolve it through
`SystemUiLanguages.Get()` and `AppLanguage.Resolve` before using MRT, a resource
context or `FrameworkElement.Language`. Apply a supported, nonempty effective
language before constructing the shell. Never pass the empty stored preference to
those APIs. Read Windows UI preferences independently of `ApplicationLanguages`,
which includes the persisted application override.

## Validate changes

Keep each localization change reviewable with its affected controls, generated text
and resources for both locales. Run the focused checks, full test suite and application
build, then inspect the affected pages in a packaged WinUI build. Compilation and
resource guards do not catch every runtime property, layout or state-retention error.

### Automated checks

Run from the repository root on Windows, using the x64 .NET SDK accepted by
`global.json`:

```powershell
$tests = 'tests\WslContainerDesktop.Tests\WslContainerDesktop.Tests.csproj'
dotnet test $tests -c Debug -p:Platform=x64 --filter 'FullyQualifiedName~I18nResourceTests|FullyQualifiedName~LanguageSwitchTests|FullyQualifiedName~DisplayTranslationTests|FullyQualifiedName~ComposePreviewLocalizationTests'
dotnet test $tests -c Debug -p:Platform=x64
dotnet build src\WslContainerDesktop\WslContainerDesktop.csproj -c Debug -p:Platform=x64
```

The focused checks cover resource parity, duplicate keys, format arguments, XAML
property compatibility and live UID mirrors; language resolution and message
translation; preservation of unknown output and serialized evidence; and localized
Compose preview messages. The full suite runs both configured target frameworks
on Windows. The build command compiles the application without registration or
launch. Report warnings, errors and skipped checks in the validation summary.

### Packaged GUI validation

Use a fresh build registered with MSIX package identity for GUI checks. The existing
`tools/launcher/Build-And-Run.ps1` rebuilds the x64 Debug application, registers its
fresh output and launches it. Registration affects the installed application with
the same identity, including when run from another worktree. Coordinate smoke runs
and use a separate package identity for isolated testing. Use the existing release
workflow for signed installation and distribution packages.

1. Check Settings in English and Simplified Chinese, including its lower sections,
   tooltips and collapsed language/theme labels. Controls should display the selected
   language without resource-property errors.
2. Switch English → System default on Windows with a supported non-English display
   language. The app should follow Windows while retaining theme and picker selections.
   Quit fully, including the tray instance, and relaunch to verify the saved choices.
3. Leave unfinished input, switch languages and revisit cached pages. Text should
   refresh while input, selections and operation state remain intact.
4. Inspect navigation, status messages, the Networks summary and Compose preview
   download messages in both languages. Open representative dialogs without submitting
   changes, then cancel; labels and help should update while technical identifiers
   remain unchanged.
5. Review startup logs for language-resolution or resource-property failures. Exercise
   registry, AI, Kubernetes and Dev Container flows when their accounts and runtimes
   are available.

Include the tested version, automated results and GUI coverage in the change's
validation summary, and state any uncompleted checks explicitly.
