// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System;

namespace MouseWithoutBorders.Class;

// Hook callbacks may enqueue packets, but must never perform transport I/O.
internal readonly struct InputHookDispatch : IDisposable
{
    [ThreadStatic] private static bool active;
    private readonly bool previous;
    internal InputHookDispatch(bool begin) { previous = active; active |= begin; }
    internal static bool Active => active;
    public void Dispose() => active = previous;
}
