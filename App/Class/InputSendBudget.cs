// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using MouseWithoutBorders.Exceptions;

namespace MouseWithoutBorders.Class;

// A hook must return before Windows silently removes it. The budget is shared by
// all frames/recipients in this callback, not restarted for each TCP connection.
internal readonly struct InputSendBudget : IDisposable
{
    [ThreadStatic] private static long deadline;
    private readonly long previous;
    internal InputSendBudget(bool begin)
    {
        previous = deadline;
        if (begin && deadline == 0) deadline = Environment.TickCount64 + 150;
    }
    public void Dispose() => deadline = previous;
    internal static bool Active => deadline != 0;
    private static int Remaining => (int)Math.Clamp(deadline - Environment.TickCount64, 0, 150);
    internal static void CheckBeforeWrite(Socket socket)
    {
        if (Active && Remaining == 0)
        {
            socket?.Dispose();
            throw new ExpectedSocketException("Input callback exhausted its network send budget.");
        }
    }
    internal static bool Enter(object target)
    {
        if (!Active) { Monitor.Enter(target); return true; }
        return Monitor.TryEnter(target, Math.Min(Remaining, 50));
    }
    internal static void Write(Stream stream, Socket socket, byte[] frame)
    {
        if (!Active) { stream.Write(frame, 0, frame.Length); return; }
        int remaining = Remaining;
        if (remaining == 0) throw new ExpectedSocketException("Input callback exhausted its network send budget.");
        using var cancellation = new CancellationTokenSource(remaining);
        try { stream.WriteAsync(frame.AsMemory(), cancellation.Token).AsTask().WaitAsync(cancellation.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            // Cancellation can leave a partial encrypted frame. Never resume this stream.
            socket.Dispose();
            throw new ExpectedSocketException("Input callback network write timed out; retired its connection.");
        }
    }
}
