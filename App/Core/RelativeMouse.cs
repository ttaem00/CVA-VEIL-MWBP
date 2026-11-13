// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.Core;

internal static class RelativeMouse
{
    internal const int MaxDistance = 50000;

    internal static int Encode(int distance) => distance < 0 ? distance - Event.MOVE_MOUSE_RELATIVE : distance + Event.MOVE_MOUSE_RELATIVE;

    internal static int Decode(int value) => value < 0 ? value + Event.MOVE_MOUSE_RELATIVE : value - Event.MOVE_MOUSE_RELATIVE;

    internal static bool IsRelativeMove(DATA packet) => packet.Type == PackageType.Mouse && packet.Md.dwFlags == WM.WM_MOUSEMOVE
        && Math.Abs((long)packet.Md.X) >= Event.MOVE_MOUSE_RELATIVE && Math.Abs((long)packet.Md.Y) >= Event.MOVE_MOUSE_RELATIVE
        && Math.Abs((long)packet.Md.X) <= Event.MOVE_MOUSE_RELATIVE + MaxDistance && Math.Abs((long)packet.Md.Y) <= Event.MOVE_MOUSE_RELATIVE + MaxDistance;

    /// <summary>Adds a later distance to an earlier one that has not been sent yet. Distances must
    /// add up; replacing the earlier one, as is right for positions, would lose movement.</summary>
    internal static bool TryMerge(DATA earlier, DATA later, out DATA merged)
    {
        merged = earlier;
        if (!IsRelativeMove(earlier) || !IsRelativeMove(later) || earlier.Des != later.Des) return false;
        long x = (long)Decode(earlier.Md.X) + Decode(later.Md.X), y = (long)Decode(earlier.Md.Y) + Decode(later.Md.Y);
        if (Math.Abs(x) > MaxDistance || Math.Abs(y) > MaxDistance) return false;
        merged = new DATA(later.Bytes);
        merged.Md.X = Encode((int)x); merged.Md.Y = Encode((int)y);
        return true;
    }
}
