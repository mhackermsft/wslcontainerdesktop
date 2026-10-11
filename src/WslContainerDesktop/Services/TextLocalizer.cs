// WSL Container Desktop - a WinUI 3 manager for WSL containers.
// Copyright (C) 2026 Michael Hacker
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using Microsoft.Extensions.Logging;
using Microsoft.Windows.ApplicationModel.Resources;

namespace WslContainerDesktop.Services;

/// <summary>Looks up UI strings for the active language. Used from view models, services and code-behind.</summary>
public interface ITextLocalizer
{
    /// <summary>
    /// Returns the string for <paramref name="key"/> in the active language, formatted with
    /// <paramref name="args"/> when any are supplied. A key with no resource falls back to the key
    /// itself so the gap is visible on screen rather than rendering as empty text.
    /// </summary>
    string Get(string key, params object?[] args);

    /// <summary>
    /// Switches the language used by subsequent <see cref="Get"/> calls. Pass a nonempty effective
    /// language returned by <see cref="AppLanguage.Resolve"/>. Set before constructing view models.
    /// </summary>
    void SetLanguage(string tag);

    /// <summary>The nonempty effective language shared with MRT and the shell.</summary>
    string LanguageTag { get; }
}

/// <summary>
/// Reads strings from the app's PRI through MRT, honouring an explicit language override.
/// </summary>
/// <remarks>
/// The language is a property of the resource <em>context</em>, not of the map, so overriding it means
/// carrying our own <see cref="ResourceContext"/> rather than relying on the thread's ambient one.
/// Results are cached per language because a status bar can ask for the same key hundreds of times a
/// second.
/// </remarks>
public sealed class TextLocalizer : ITextLocalizer
{
    /// <summary>Resource map produced by compiling <c>Strings/&lt;tag&gt;/Resources.resw</c>.</summary>
    private const string StringsMapName = "Resources";

    private readonly ILogger<TextLocalizer> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedMissing = new(StringComparer.Ordinal);

    /// <summary>Resource manager for the app's own resources.pri.</summary>
    private readonly ResourceManager _resourceManager = new();

    /// <summary>
    /// Context pinned to the resolved language. Rebuilt under the lookup lock because
    /// <see cref="ResourceContext"/> is not thread-safe.
    /// </summary>
    private ResourceContext? _overrideContext;
    private string _languageTag = "en-US";
    public string LanguageTag { get { lock (_sync) return _languageTag; } }

    public TextLocalizer(ILogger<TextLocalizer> logger) => _logger = logger;

    /// <inheritdoc/>
    public void SetLanguage(string tag)
    {
        var normalized = AppLanguage.Normalize(tag);
        if (normalized.Length == 0)
            throw new ArgumentException("Resolve the system preference before passing a language to the resource context.", nameof(tag));
        lock (_sync)
        {
            if (_overrideContext is not null && _languageTag == normalized) return;
            _overrideContext = CreateContext(_resourceManager, normalized);
            _languageTag = normalized;
            _cache.Clear();
        }

        _logger.LogInformation(
            "UI language set to {Language}.",
            normalized);
    }

    /// <inheritdoc/>
    public string Get(string key, params object?[] args)
    {
        string resolved;
        lock (_sync)
        {
            if (!_cache.TryGetValue(key, out resolved!))
                _cache[key] = resolved = Resolve(key);
        }

        if (args is null || args.Length == 0)
        {
            return resolved;
        }

        try
        {
            return string.Format(System.Globalization.CultureInfo.CurrentUICulture, resolved, args);
        }
        catch (FormatException)
        {
            // A bad placeholder in one locale must not take down a status update.
            _logger.LogWarning("Malformed format string for resource key {Key}: {Value}", key, resolved);
            return resolved;
        }
    }

    private string Resolve(string key)
    {
        // Dotted keys are property identifiers that the PRI compiler split into subtrees, so they are
        // addressed with a slash — the same convention x:Uid uses.
        var path = key.Replace('.', '/');

        try
        {
            var map = _resourceManager.MainResourceMap.GetSubtree(StringsMapName);
            var value = (_overrideContext is null ? map.GetValue(path) : map.GetValue(path, _overrideContext)).ValueAsString;

            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Resource lookup failed for {Key}.", key);
        }

        // Report each gap once: a status bar re-asking for a missing key every frame would otherwise
        // drown the log, and translators want the list of untranslated keys, not its volume.
        if (_reportedMissing.Add(key))
        {
            _logger.LogWarning("Missing string resource {Key}; displaying the key itself.", key);
        }

        return key;
    }

    private static ResourceContext CreateContext(ResourceManager manager, string tag)
    {
        // The plain Microsoft.Windows.ApplicationModel.Resources projection (no .Core namespace here)
        // exposes neither Languages on ResourceContext nor a parameterless constructor. The supported
        // way to override the language is to create a context through the manager and set the
        // Language qualifier on its QualifierValues map.
        var context = manager.CreateResourceContext();
        context.QualifierValues["Language"] = tag;
        return context;
    }
}
