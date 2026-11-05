// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.Core;

internal enum KeyboardAction { Computer1, Computer2, Computer3, Computer4, NextComputer, AllComputers, LockComputers, Reconnect, ToggleEdgeSwitching }

internal sealed class HeldKeyChord
{
    private const int AnyWin = 256;
    private readonly HashSet<int> held = new();

    internal bool Update(int key, bool down)
    {
        return down ? held.Add(key) : held.Remove(key);
    }

    internal void Clear() => held.Clear();
    internal int[] DownKeys => held.ToArray();

    private static int Modifier(int key) => (Keys)key switch
    {
        Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => (int)Keys.ControlKey,
        Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => (int)Keys.ShiftKey,
        Keys.Menu or Keys.LMenu or Keys.RMenu => (int)Keys.Menu,
        Keys.LWin or Keys.RWin => AnyWin,
        _ => key,
    };

    internal bool MatchesExact(IReadOnlyList<int> keys) => Matches(keys)
        && held.All(key => keys.Contains(key) || keys.Contains(Modifier(key)));

    private bool Contains(int key) => key switch
    {
        (int)Keys.ControlKey => held.Contains((int)Keys.LControlKey) || held.Contains((int)Keys.RControlKey),
        (int)Keys.ShiftKey => held.Contains((int)Keys.LShiftKey) || held.Contains((int)Keys.RShiftKey),
        (int)Keys.Menu => held.Contains((int)Keys.LMenu) || held.Contains((int)Keys.RMenu),
        AnyWin => held.Contains((int)Keys.LWin) || held.Contains((int)Keys.RWin),
        _ => held.Contains(key),
    };

    internal bool IsActive(EasyMouseOption option, IReadOnlyList<int> custom) => option switch
    {
        EasyMouseOption.Ctrl => Contains((int)Keys.ControlKey),
        EasyMouseOption.Shift => Contains((int)Keys.ShiftKey),
        EasyMouseOption.Alt => Contains((int)Keys.Menu),
        EasyMouseOption.Win => Contains(AnyWin),
        EasyMouseOption.Custom => Matches(custom),
        _ => false,
    };

    private bool Matches(IReadOnlyList<int> keys)
    {
        // An empty custom chord must not authorize every screen-edge crossing.
        if (keys.Count == 0) return false;
        foreach (int key in keys)
            if (!Contains(key)) return false;
        return true;
    }

    internal static string Format(IEnumerable<int> keys) => string.Join("+", keys.Select(key => key switch
    {
        (int)Keys.ControlKey => "Ctrl",
        (int)Keys.ShiftKey => "Shift",
        (int)Keys.Menu => "Alt",
        AnyWin => "Win",
        (int)Keys.CapsLock => "Caps Lock",
        _ => ((Keys)key).ToString(),
    }));
}
