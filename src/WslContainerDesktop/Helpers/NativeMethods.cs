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

using System.Runtime.InteropServices;

namespace WslContainerDesktop.Helpers;

/// <summary>Win32 P/Invoke declarations used by the tray icon and window helpers.</summary>
internal static class NativeMethods
{
    public const int WM_APP = 0x8000;
    public const int WM_COMMAND = 0x0111;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_CONTEXTMENU = 0x007B;
    public const int WM_NULL = 0x0000;
    public const int WM_DESTROY = 0x0002;
    public const int WM_QUERYENDSESSION = 0x0011;
    public const int WM_ENDSESSION = 0x0016;

    // Tray callback message (WM_APP + 1).
    public const int WM_TRAYICON = WM_APP + 1;

    // Shell_NotifyIcon messages/flags.
    public const int NIM_ADD = 0x00000000;
    public const int NIM_MODIFY = 0x00000001;
    public const int NIM_DELETE = 0x00000002;
    public const int NIM_SETVERSION = 0x00000004;

    public const int NIF_MESSAGE = 0x00000001;
    public const int NIF_ICON = 0x00000002;
    public const int NIF_TIP = 0x00000004;
    public const int NIF_INFO = 0x00000010;
    public const int NIF_SHOWTIP = 0x00000080;

    // Balloon/toast info flags (dwInfoFlags).
    public const int NIIF_NONE = 0x00000000;
    public const int NIIF_INFO = 0x00000001;
    public const int NIIF_WARNING = 0x00000002;
    public const int NIIF_ERROR = 0x00000003;

    public const int NOTIFYICON_VERSION_4 = 4;

    // TrackPopupMenu flags.
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_NONOTIFY = 0x0080;

    // Menu flags.
    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint MF_GRAYED = 0x00000001;
    public const uint MF_DISABLED = 0x00000002;
    public const uint MF_CHECKED = 0x00000008;
    public const uint MF_DEFAULT = 0x00001000;

    // ShowWindow.
    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_RESTORE = 9;
    public const int SW_SHOWNORMAL = 1;

    public const int CW_USEDEFAULT = unchecked((int)0x80000000);

    /// <summary>Callback signature for the hidden Win32 tray window procedure.</summary>
    public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>Win32 window-class registration data used to create the hidden tray window.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASS
    {
        /// <summary>Window-class style flags; unused by the tray window.</summary>
        public uint style;
        /// <summary>Callback that receives native messages for windows of this class.</summary>
        public WndProc lpfnWndProc;
        /// <summary>Extra bytes reserved after the window-class structure; unused here.</summary>
        public int cbClsExtra;
        /// <summary>Extra bytes reserved after each window instance; unused here.</summary>
        public int cbWndExtra;
        /// <summary>Module handle that owns the registered window class.</summary>
        public nint hInstance;
        /// <summary>Class icon handle; unused because the tray icon supplies its own icon.</summary>
        public nint hIcon;
        /// <summary>Class cursor handle; unused for the hidden tray window.</summary>
        public nint hCursor;
        /// <summary>Background brush handle; unused because the window is hidden.</summary>
        public nint hbrBackground;
        /// <summary>Optional menu resource name; null because the tray menu is built dynamically.</summary>
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        /// <summary>Unique Win32 class name for the hidden tray window.</summary>
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
    }

    /// <summary>Win32 notification-area icon data passed to <c>Shell_NotifyIconW</c>.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        /// <summary>Size of the structure, required by <c>Shell_NotifyIconW</c>.</summary>
        public int cbSize;
        /// <summary>Window that receives callback messages for the tray icon.</summary>
        public nint hWnd;
        /// <summary>Application-defined identifier for the tray icon.</summary>
        public int uID;
        /// <summary>Flags describing which fields are valid for this call.</summary>
        public int uFlags;
        /// <summary>Message id sent back to the hidden window for tray events.</summary>
        public int uCallbackMessage;
        /// <summary>Native icon handle displayed in the notification area.</summary>
        public nint hIcon;
        /// <summary>Tooltip text shown when hovering the tray icon.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        /// <summary>Optional icon state flags; unused by the app.</summary>
        public int dwState;
        /// <summary>Mask for state changes; unused by the app.</summary>
        public int dwStateMask;
        /// <summary>Balloon notification body text.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        /// <summary>Notification icon protocol version requested by the app.</summary>
        public int uVersion;
        /// <summary>Balloon notification title text.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        /// <summary>Balloon notification icon/severity flags.</summary>
        public int dwInfoFlags;
        /// <summary>Optional persistent icon GUID; unused because the app uses <c>uID</c>.</summary>
        public Guid guidItem;
        /// <summary>Optional custom balloon icon handle; unused by the app.</summary>
        public nint hBalloonIcon;
    }

    /// <summary>Screen coordinates returned by Win32 mouse-position APIs.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        /// <summary>Horizontal screen coordinate in physical pixels.</summary>
        public int X;
        /// <summary>Vertical screen coordinate in physical pixels.</summary>
        public int Y;
    }

    /// <summary>Registers the hidden tray window class with <c>user32.dll</c>.</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    /// <summary>Creates the hidden message window that receives tray callbacks.</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern nint CreateWindowExW(
        int dwExStyle,
        [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
        [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    /// <summary>Delegates unhandled window messages to the default Win32 window procedure.</summary>
    [DllImport("user32.dll")]
    public static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>Destroys a native window handle created for the tray icon.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(nint hWnd);

    /// <summary>Gets the current module handle for Win32 class registration.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);

    /// <summary>Adds, updates, or removes the app's notification-area icon.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATA lpData);

    /// <summary>Creates a native popup menu used by the tray icon.</summary>
    [DllImport("user32.dll")]
    public static extern nint CreatePopupMenu();

    /// <summary>Destroys a native popup menu after it closes.</summary>
    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(nint hMenu);

    /// <summary>Appends an item or separator to a native tray popup menu.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, [MarshalAs(UnmanagedType.LPWStr)] string? lpNewItem);

    /// <summary>Marks the tray menu's Open item as the default action.</summary>
    [DllImport("user32.dll")]
    public static extern bool SetMenuDefaultItem(nint hMenu, uint uItem, uint fByPos);

    /// <summary>Shows the tray popup menu and returns the command the user chose.</summary>
    [DllImport("user32.dll")]
    public static extern uint TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hwnd, nint lptpm);

    /// <summary>Reads the cursor location so the tray menu opens under the pointer.</summary>
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>Asks Windows to foreground the app window.</summary>
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint hWnd);

    /// <summary>Posts a native message without waiting for it to be processed.</summary>
    [DllImport("user32.dll")]
    public static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>Looks up the broadcast message Explorer sends after the taskbar restarts.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW([MarshalAs(UnmanagedType.LPWStr)] string lpString);

    /// <summary>Releases a native icon handle created by GDI+.</summary>
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(nint hIcon);

    /// <summary>Changes the visibility or restored state of a native window.</summary>
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);

    /// <summary>Returns whether a native window is currently visible.</summary>
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hWnd);

    /// <summary>Reads per-monitor DPI so logical sizes can be converted to pixels.</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint hWnd);

    /// <summary>Gets the native handle of the current foreground window.</summary>
    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    /// <summary>Finds the UI thread that owns a native window.</summary>
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    /// <summary>Returns the current native thread id.</summary>
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>Temporarily joins input queues so foreground activation is allowed.</summary>
    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>Raises a native window in the Z order.</summary>
    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(nint hWnd);

    /// <summary>Returns whether a native window is minimized.</summary>
    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint hWnd);

    // RegisterApplicationRestart flags: restart only after servicing, never after a crash, hang or reboot.
    public const uint RESTART_NO_CRASH = 1;
    public const uint RESTART_NO_HANG = 2;
    public const uint RESTART_NO_REBOOT = 8;

    /// <summary>Asks Windows to relaunch this process after it is shut down for servicing (an MSIX update).</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegisterApplicationRestart(string? pwzCommandline, uint dwFlags);

    /// <summary>Removes the servicing restart registration when it is no longer needed.</summary>
    [DllImport("kernel32.dll")]
    public static extern int UnregisterApplicationRestart();
}
