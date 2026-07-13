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

namespace WslContainerDesktop.Tray;

/// <summary>
/// A Win32 system-tray icon backed by a hidden message window. Renders a colored
/// status dot (green = healthy) and shows a right-click menu with Open / status / Quit.
/// Must be created and disposed on the UI thread (its window shares the app message pump).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int TrayIconId = 1;
    private const int IdOpen = 100;
    private const int IdQuit = 101;
    private const int IdStatus = 102;
    private const string WindowClassName = "WslContainerDesktopTrayWindow";

    private readonly StatusIconFactory _iconFactory = new();
    private readonly NativeMethods.WndProc _wndProcDelegate;
    private readonly uint _taskbarCreatedMessage;

    private nint _hwnd;
    private bool _iconAdded;
    private bool _disposed;

    private EngineHealth _health = EngineHealth.Unknown;
    private string _tooltip = "WSL Container Desktop";
    private string _statusText = "Status: unknown";

    public event Action? OpenRequested;
    public event Action? QuitRequested;

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

    /// <summary>Updates the tray icon color, tooltip, and status menu line.</summary>
    public void UpdateStatus(EngineHealth health, string tooltip, string statusText)
    {
        _health = health;
        _tooltip = tooltip;
        _statusText = statusText;

        if (_iconAdded)
        {
            AddOrUpdateIcon(NativeMethods.NIM_MODIFY);
        }
    }

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

    private static string Truncate(string? value, int max)
    {
        value ??= string.Empty;
        return value.Length <= max ? value : value[..max];
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

    private void HandleCommand(int id)
    {
        switch (id)
        {
            case IdOpen:
                OpenRequested?.Invoke();
                break;
            case IdQuit:
                QuitRequested?.Invoke();
                break;
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
