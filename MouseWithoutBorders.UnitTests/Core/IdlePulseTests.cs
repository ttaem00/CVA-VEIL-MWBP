using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class IdlePulseTests
{
    [TestMethod]
    public void IdlePulseRefreshesWindowsIdleTimeWithoutCursorMotion()
    {
        var observations = new List<Point>();
        using var ready = new ManualResetEventSlim(); using var stop = new ManualResetEventSlim();
        int armed = 0, outsideMovement = 0;
        Exception? pumpFailure = null;
        int flagsOffset = Marshal.OffsetOf<MouseEvent>(nameof(MouseEvent.Flags)).ToInt32();
        int extraOffset = Marshal.OffsetOf<MouseEvent>(nameof(MouseEvent.Extra)).ToInt32();
        HookCallback callback = (code, message, data) =>
        {
            // Inspect coordinates only for our tagged synthetic movement. Other
            // mouse input passes through without being copied or recorded.
            if (code >= 0 && message == (IntPtr)WM.WM_MOUSEMOVE && Volatile.Read(ref armed) != 0)
            {
                if ((Marshal.ReadInt32(data, flagsOffset) & 1) != 0
                    && Marshal.ReadIntPtr(data, extraOffset) == InputSimulation.InjectedInputTag)
                {
                    var input = Marshal.PtrToStructure<MouseEvent>(data);
                    observations.Add(input.Point);
                }
                else Interlocked.Exchange(ref outsideMovement, 1); // Boolean only; never record physical coordinates.
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        var pump = new System.Threading.Thread(() =>
        {
            IntPtr hook = IntPtr.Zero;
            try
            {
                hook = SetWindowsHookEx(14, callback, NativeMethods.GetModuleHandle(null!), 0);
                if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                ready.Set();
                while (!stop.IsSet)
                {
                    System.Windows.Forms.Application.DoEvents();
                    stop.Wait(1);
                }
            }
            catch (Exception error) { pumpFailure = error; ready.Set(); }
            finally { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); }
        }) { IsBackground = true };
        pump.SetApartmentState(ApartmentState.STA);
        Point before = default, after = default;
        uint idleBefore = 0, idleAfterCall = 0, callStarted = 0, callFinished = 0;
        int previousPaintCount = Common.PaintCount;
        try
        {
            pump.Start();
            Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(3)), "The native observation pump did not start.");
            Assert.IsNull(pumpFailure);
            Volatile.Write(ref armed, 1);
            Assert.IsTrue(NativeMethods.GetCursorPos(ref before));
            idleBefore = LastInputTick();
            Assert.IsTrue(SpinWait.SpinUntil(() => unchecked((uint)Environment.TickCount) != idleBefore, TimeSpan.FromSeconds(1)),
                "The Windows tick must advance before testing a refreshed idle timestamp.");
            Volatile.Write(ref armed, 1);
            callStarted = unchecked((uint)Environment.TickCount);
            typeof(Common).GetMethod("PokeMyself", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            callFinished = unchecked((uint)Environment.TickCount);
            idleAfterCall = LastInputTick();
            Assert.IsTrue(NativeMethods.GetCursorPos(ref after));
        }
        finally
        {
            Volatile.Write(ref armed, 0); stop.Set();
            Assert.IsTrue(pump.Join(TimeSpan.FromSeconds(3)), "The native observation pump did not stop.");
            GC.KeepAlive(callback);
            Common.PaintCount = previousPaintCount;
        }
        Assert.IsNull(pumpFailure);
        if (Volatile.Read(ref outsideMovement) != 0)
            Assert.Inconclusive("External mouse input overlapped the native idle/cursor measurement; this is not a product PASS.");
        Assert.AreNotEqual(idleBefore, idleAfterCall, "The native idle timestamp must actually change.");
        Assert.IsTrue(unchecked(idleAfterCall - callStarted) <= unchecked(callFinished - callStarted),
            $"The refreshed idle timestamp must belong to the PokeMyself call: before={idleBefore}, after={idleAfterCall}, call=[{callStarted},{callFinished}].");
        Assert.AreEqual(before, after);
        // Windows can refresh its idle timer without surfacing a zero-delta MOVE
        // to WH_MOUSE_LL. Any surfaced movement must still remain stationary.
        Assert.IsTrue(observations.Count <= 1, "Keeping the receiving PC awake must not run a cursor-jiggling loop.");
        foreach (var observation in observations)
            Assert.AreEqual(before, observation, "The injected idle pulse must not move the pointer, even temporarily.");
    }

    private static uint LastInputTick()
    {
        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.Tick;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size, Tick; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseEvent
    {
        public Point Point;
        public uint Data, Flags, Tick;
        public IntPtr Extra;
    }

    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int type, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInput input);
}
