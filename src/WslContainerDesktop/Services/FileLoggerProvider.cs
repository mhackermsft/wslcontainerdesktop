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

using System.Text;
using Microsoft.Extensions.Logging;

namespace WslContainerDesktop.Services;

/// <summary>
/// A tiny dependency-free <see cref="ILoggerProvider"/> that appends log lines to a daily-rotated
/// file under <c>%LOCALAPPDATA%\WslContainerDesktop\logs</c>. Deliberately minimal (no external
/// logging framework) — it exists so the app's previously-invisible failures leave a breadcrumb
/// that makes field diagnosis possible. All writes are serialized and best-effort; a logging
/// failure never propagates to the app.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly long _maxBytes;
    private readonly int _retainDays;
    private bool _prunedOnce;

    public FileLoggerProvider(LogLevel minLevel = LogLevel.Information, long maxBytes = 5 * 1024 * 1024, int retainDays = 14)
    {
        _minLevel = minLevel;
        _maxBytes = maxBytes;
        _retainDays = retainDays;
        _directory = ResolveLogDirectory();
    }

    /// <summary>Absolute path of the log directory, surfaced so the UI can offer "open logs folder".</summary>
    public string Directory => _directory;

    /// <summary>
    /// Resolves a real, non-virtualized log directory. Under MSIX,
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> yields a path that the OS
    /// silently redirects for the packaged process but that external tools (Explorer) cannot open;
    /// worse, the value differs depending on how early it is read. Using the WinRT
    /// <c>ApplicationData.Current.LocalCacheFolder</c> gives the concrete container path directly,
    /// so both our writes and the "Open logs folder" button point at the same real location.
    /// Falls back to the environment path when the app has no package identity.
    /// </summary>
    private static string ResolveLogDirectory()
    {
        try
        {
            var localCache = Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path;
            return Path.Combine(localCache, "WslContainerDesktop", "logs");
        }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WslContainerDesktop",
                "logs");
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        // Nothing to dispose; writes are opened/closed per line to stay robust across crashes.
    }

    private void Write(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        if (level < _minLevel || level == LogLevel.None)
        {
            return;
        }

        // Category is usually a full type name; the short name is enough for a local log.
        var shortCategory = category;
        var lastDot = category.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < category.Length - 1)
        {
            shortCategory = category[(lastDot + 1)..];
        }

        var sb = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(LevelTag(level)).Append("] ")
            .Append(shortCategory)
            .Append(": ")
            .Append(message);

        if (exception is not null)
        {
            sb.Append(Environment.NewLine).Append(exception);
        }

        var line = sb.ToString();

        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_directory);
                PruneOldLogsLocked();

                var path = Path.Combine(_directory, $"app-{DateTime.Now:yyyyMMdd}.log");
                RollIfTooLargeLocked(path);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never take the app down; drop the line on any I/O failure.
            }
        }
    }

    private void RollIfTooLargeLocked(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length >= _maxBytes)
            {
                var rolled = Path.Combine(_directory, $"app-{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.Move(path, rolled);
            }
        }
        catch
        {
            // ignore roll failures
        }
    }

    private void PruneOldLogsLocked()
    {
        if (_prunedOnce)
        {
            return;
        }

        _prunedOnce = true;
        try
        {
            var cutoff = DateTime.Now.AddDays(-_retainDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // ignore prune failures
        }
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider._minLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            _provider.Write(logLevel, _category, eventId, message, exception);
        }
    }
}
