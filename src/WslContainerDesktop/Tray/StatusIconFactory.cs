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

using System.Drawing;
using System.Drawing.Drawing2D;

namespace WslContainerDesktop.Tray;

/// <summary>Overall engine/container health surfaced by the tray icon color.</summary>
public enum EngineHealth
{
    Unknown,
    Healthy,
    Degraded,
    Down,
}

/// <summary>
/// Renders small status-dot icons (green/amber/red/gray) with GDI+ and returns
/// native HICON handles for use with Shell_NotifyIcon. Handles are cached and
/// destroyed on <see cref="Dispose"/>.
/// </summary>
public sealed class StatusIconFactory : IDisposable
{
    private readonly Dictionary<EngineHealth, nint> _cache = new();
    private bool _disposed;

    public nint GetIcon(EngineHealth health)
    {
        if (_cache.TryGetValue(health, out var existing))
        {
            return existing;
        }

        var color = health switch
        {
            EngineHealth.Healthy => Color.FromArgb(45, 200, 95),
            EngineHealth.Degraded => Color.FromArgb(240, 180, 40),
            EngineHealth.Down => Color.FromArgb(230, 70, 70),
            _ => Color.FromArgb(150, 150, 150),
        };

        var handle = CreateDotIcon(color);
        _cache[health] = handle;
        return handle;
    }

    private static nint CreateDotIcon(Color color)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Subtle container/whale silhouette hint: a rounded square backdrop.
            using (var backdrop = new SolidBrush(Color.FromArgb(38, 41, 50)))
            {
                FillRoundedRectangle(g, backdrop, new Rectangle(2, 2, size - 4, size - 4), 7);
            }

            // Status dot.
            var dot = new Rectangle(8, 8, size - 16, size - 16);
            using (var glow = new SolidBrush(Color.FromArgb(70, color)))
            {
                g.FillEllipse(glow, new Rectangle(5, 5, size - 10, size - 10));
            }

            using (var brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, dot);
            }

            using var pen = new Pen(Color.FromArgb(200, Color.White), 1.2f);
            g.DrawEllipse(pen, dot);
        }

        return bitmap.GetHicon();
    }

    private static void FillRoundedRectangle(Graphics g, Brush brush, Rectangle rect, int radius)
    {
        using var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var handle in _cache.Values)
        {
            if (handle != nint.Zero)
            {
                NativeMethods_DestroyIcon(handle);
            }
        }

        _cache.Clear();
        _disposed = true;
    }

    private static void NativeMethods_DestroyIcon(nint handle) =>
        Helpers.NativeMethods.DestroyIcon(handle);
}
