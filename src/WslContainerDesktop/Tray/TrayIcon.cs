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

using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Tray;

/// <summary>Requests a start/stop of a container from the tray quick-actions menu.</summary>
public sealed class TrayContainerAction
{
    public required string ContainerId { get; init; }

    /// <summary>True to start the container; false to stop it.</summary>
    public required bool Start { get; init; }
}

/// <summary>
/// A Win32 system-tray icon backed by a hidden message window. Renders a colored
/// status dot (green = healthy) and shows a right-click menu with Open, live running
/// count, per-container quick start/stop actions, a notifications mute toggle, and Quit.
/// Must be created and disposed on the UI thread (its window shares the app message pump).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int TrayIconId = 1;
    private const int IdOpen = 100;
    private const int IdQuit = 101;
    private const int IdStatus = 102;
    private const int IdMute = 103;

    // Dynamic per-container menu items are assigned ids starting here.
    private const int ContainerActionIdBase = 1000;

    // Cap the number of containers listed so the menu stays manageable.
    private const int MaxContainerMenuItems = 15;

    private const string WindowClassName = "WslContainerDesktopTrayWindow";

    private readonly StatusIconFactory _iconFactory = new();
    private readonly NativeMethods.WndProc _wndProcDelegate;
    private readonly uint _taskbarCreatedMessage;

    private readonly Dictionary<int, TrayContainerAction> _containerActions = new();

    private nint _hwnd;
    private bool _iconAdded;
    private bool _disposed;

    private EngineHealth _health = EngineHealth.Unknown;
    private string _tooltip = "WSL Container Desktop";
    private string _statusText = "Status: unknown";
    private int _runningCount;
    private IReadOnlyList<ContainerInfo> _containers = Array.Empty<ContainerInfo>();
    private bool _notificationsEnabled = true;

    public event Action? OpenRequested;
    public event Action? QuitRequested;

    /// <summary>Raised when the user starts/stops a container from the tray menu.</summary>
    public event Action<TrayContainerAction>? ContainerActionRequested;

    /// <summary>Raised when the user toggles the notifications mute item in the tray menu.</summary>
    public event Action? MuteToggleRequested;

    public TrayIcon()
    {
        _wndProcDelegate = WndProc;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
    }

    public void Initialize()
    {
        var hInstance = NativeMethods.GetModuleHandleW(null);

        var wc = new NativeMethods.WNDCLASS
        {
            lpfnWndProc = _wndProcDelegate,
            hInstance = hInstance,
            lpszClassName = WindowClassName,
        };

        NativeMethods.RegisterClassW(ref wc);

        _hwnd = NativeMethods.CreateWindowExW(
            0,
            WindowClassName,
            "WslContainerDesktopTray",
            0,
            0,
            0,
            0,
            0,
            nint.Zero,
            nint.Zero,
            hInstance,
            nint.Zero);

        AddOrUpdateIcon(NativeMethods.NIM_ADD);

        var version = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uVersion = NativeMethods.NOTIFYICON_VERSION_4,
        };
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETVERSION, ref version);
    }

    /// <summary>Updates the tray icon color, tooltip, status line, and quick-action menu data.</summary>
    public void UpdateStatus(
        EngineHealth health,
        string tooltip,
        string statusText,
        int runningCount,
        IReadOnlyList<ContainerInfo> containers,
        bool notificationsEnabled)
    {
        _health = health;
        _tooltip = tooltip;
        _statusText = statusText;
        _runningCount = runningCount;
        _containers = containers ?? Array.Empty<ContainerInfo>();
        _notificationsEnabled = notificationsEnabled;

        if (_iconAdded)
        {
            AddOrUpdateIcon(NativeMethods.NIM_MODIFY);
        }
    }

    /// <summary>
    /// Immediately updates the cached notifications-enabled flag so the tray menu's "Mute
    /// notifications" checkmark reflects a toggle before the next status poll rebuilds it.
    /// </summary>
    public void SetNotificationsEnabled(bool notificationsEnabled) =>
        _notificationsEnabled = notificationsEnabled;

    /// <summary>Shows a balloon/toast notification from the tray icon (best effort).</summary>
    public void ShowNotification(string title, string message)
    {
        if (!_iconAdded)
        {
            return;
        }

        // szInfoTitle is a 64-char field and szInfo a 256-char field; truncate to stay within them.
        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_INFO,
            szTip = _tooltip,
            szInfo = Truncate(message, 255),
            szInfoTitle = Truncate(title, 63),
            dwInfoFlags = NativeMethods.NIIF_WARNING,
        };

        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
    }

    private void AddOrUpdateIcon(int message)
    {
        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP,
            uCallbackMessage = NativeMethods.WM_TRAYICON,
            hIcon = _iconFactory.GetIcon(_health),
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        if (NativeMethods.Shell_NotifyIconW(message, ref data))
        {
            _iconAdded = true;
        }
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // Explorer restarted; re-add our icon.
            _iconAdded = false;
            AddOrUpdateIcon(NativeMethods.NIM_ADD);
            return nint.Zero;
        }

        switch (msg)
        {
            case NativeMethods.WM_TRAYICON:
                HandleTrayMessage((int)(lParam & 0xFFFF));
                return nint.Zero;

            case NativeMethods.WM_COMMAND:
                HandleCommand((int)(wParam & 0xFFFF));
                return nint.Zero;

            case NativeMethods.WM_DESTROY:
                return nint.Zero;
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void HandleTrayMessage(int eventMessage)
    {
        switch (eventMessage)
        {
            case NativeMethods.WM_LBUTTONUP:
            case NativeMethods.WM_LBUTTONDBLCLK:
                OpenRequested?.Invoke();
                break;

            case NativeMethods.WM_RBUTTONUP:
            case NativeMethods.WM_CONTEXTMENU:
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == nint.Zero)
        {
            return;
        }

        _containerActions.Clear();

        try
        {
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, IdOpen, "Open WSL Container Desktop");
            NativeMethods.SetMenuDefaultItem(menu, IdOpen, 0);
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);

            NativeMethods.AppendMenuW(
                menu,
                NativeMethods.MF_STRING | NativeMethods.MF_GRAYED | NativeMethods.MF_DISABLED,
                IdStatus,
                _statusText);

            AppendContainerItems(menu);

            NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);

            // Notifications mute toggle. Checked means notifications are muted.
            var muteFlags = NativeMethods.MF_STRING | (!_notificationsEnabled ? NativeMethods.MF_CHECKED : 0);
            NativeMethods.AppendMenuW(menu, muteFlags, IdMute, "Mute notifications");

            NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, IdQuit, "Quit");

            NativeMethods.GetCursorPos(out var pt);
            NativeMethods.SetForegroundWindow(_hwnd);

            var cmd = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD,
                pt.X,
                pt.Y,
                _hwnd,
                nint.Zero);

            NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, nint.Zero, nint.Zero);

            if (cmd != 0)
            {
                HandleCommand((int)cmd);
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    /// <summary>
    /// Adds the running-count header and per-container quick start/stop actions. Running
    /// containers are listed first (each offering Stop); stopped ones offer Start.
    /// </summary>
    private void AppendContainerItems(nint menu)
    {
        if (_containers.Count == 0)
        {
            return;
        }

        NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);
        NativeMethods.AppendMenuW(
            menu,
            NativeMethods.MF_STRING | NativeMethods.MF_GRAYED | NativeMethods.MF_DISABLED,
            IdStatus,
            $"Running containers: {_runningCount}");

        var ordered = _containers
            .OrderByDescending(c => c.State == ContainerState.Running)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxContainerMenuItems)
            .ToList();

        var nextId = ContainerActionIdBase;
        foreach (var container in ordered)
        {
            var isRunning = container.State == ContainerState.Running;
            var name = string.IsNullOrWhiteSpace(container.Name) ? container.ShortId : container.Name;
            var label = $"{(isRunning ? "Stop" : "Start")}  {Truncate(name, 40)}";

            _containerActions[nextId] = new TrayContainerAction
            {
                ContainerId = container.Id,
                Start = !isRunning,
            };

            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (nuint)nextId, label);
            nextId++;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private void HandleCommand(int id)
    {
        switch (id)
        {
            case IdOpen:
                OpenRequested?.Invoke();
                return;
            case IdQuit:
                QuitRequested?.Invoke();
                return;
            case IdMute:
                MuteToggleRequested?.Invoke();
                return;
        }

        if (_containerActions.TryGetValue(id, out var action))
        {
            ContainerActionRequested?.Invoke(action);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_iconAdded)
        {
            var data = new NativeMethods.NOTIFYICONDATA
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = TrayIconId,
            };
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
            _iconAdded = false;
        }

        if (_hwnd != nint.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }

        _iconFactory.Dispose();
        _disposed = true;
    }
}
