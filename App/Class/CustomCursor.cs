// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// <summary>
//     Create a customed cursor.
// </summary>
// <history>
//     2008 created by Truong Do (ductdo).
//     2009-... modified by Truong Do (TruongDo).
//     2023- Included in PowerToys.
// </history>
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using MouseWithoutBorders.Core;

// Disable the warning to preserve original code
#pragma warning disable CA1716
namespace MouseWithoutBorders.Class
#pragma warning restore CA1716
{
    internal struct IconInfo
    {
        // Suppress warning to match COM names
        #pragma warning disable SA1307
        internal bool fIcon;
        internal int xHotspot;
        internal int yHotspot;
        internal IntPtr hbmMask;
        internal IntPtr hbmColor;
        #pragma warning restore SA1307
    }

    internal class CustomCursor
    {
        private CustomCursor()
        {
        }

        internal static Cursor CreateCursor(Bitmap bmp, int xHotSpot, int yHotSpot)
        {
            IconInfo tmp = default;
            tmp.xHotspot = xHotSpot;
            tmp.yHotspot = yHotSpot;
            tmp.fIcon = false;

            // GetIconInfo(bmp.GetHicon(), ref tmp);
            tmp.hbmColor = bmp.GetHbitmap();
            tmp.hbmMask = bmp.GetHbitmap();
            return new Cursor(NativeMethods.CreateIconIndirect(ref tmp));
        }

        internal static Cursor CreateDotCursor()
        {
            Bitmap bitmap = new(32, 32, PixelFormat.Format24bppRgb);
            bitmap.MakeTransparent(Color.Black);
            Graphics g = Graphics.FromImage(bitmap);
            g.DrawLine(Pens.Gray, 0, 0, 1, 1);
            Cursor c = CreateCursor(bitmap, 0, 0);
            bitmap.Dispose();
            return c;
        }

        internal static void RestoreNativeCursor()
        {
            var keys = new NativeMethods.MOUSEKEYS { cbSize = (uint)Marshal.SizeOf<NativeMethods.MOUSEKEYS>() };
            if (!NativeMethods.SystemParametersInfoMouseKeys(0x36, keys.cbSize, ref keys, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            keys.dwFlags |= 1; // MKF_MOUSEKEYSON; preserve the user's speed and Num Lock preferences.
            keys.dwReserved1 = keys.dwReserved2 = 0;
            if (!NativeMethods.SystemParametersInfoMouseKeys(0x37, keys.cbSize, ref keys, 3)) // UPDATEINIFILE | SENDCHANGE
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        internal static void ShowFakeMouseCursor(int x, int y)
        {
            if (x != int.MinValue && y != int.MinValue && !Setting.Values.DrawMouse) return;
            Common.MainForm?.QueueCursorUpdate(new Point(x, y));
        }

        internal static void RenderMouseCursor(Point point)
        {
            var cursor = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
            RenderMouseCursor(point, NativeMethods.GetCursorInfo(ref cursor) && (cursor.flags & 1) != 0);
        }

        internal static void RenderMouseCursor(Point point, bool nativeCursorShowing)
        {
            int x = point.X, y = point.Y;
            if (!Setting.Values.DrawMouse || nativeCursorShowing || x == int.MinValue || y == int.MinValue)
            {
                Common.MouseCursorForm?.Hide();
                return;
            }

            Common.MouseCursorForm ??= new FrmMouseCursor { Owner = Common.MainForm };
            Common.MouseCursorForm.Show();
            // Keep the fallback above later topmost windows without taking focus.
            NativeMethods.SetWindowPos(Common.MouseCursorForm.Handle, NativeMethods.HWND_TOPMOST, x + 1, y + 1, 0, 0, 0x11); // NOSIZE | NOACTIVATE
        }
    }
}
