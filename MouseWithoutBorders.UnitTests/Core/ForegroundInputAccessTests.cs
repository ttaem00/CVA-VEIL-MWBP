using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class ForegroundInputAccessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void MeasuresRepeatedForegroundClassificationWithoutInjectingInput()
    {
        for (int i = 0; i < 100; i++) ForegroundInputAccess.RequiresBroker();
        const int samples = 10000;
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < samples; i++) ForegroundInputAccess.RequiresBroker();
        timer.Stop();
        TestContext.WriteLine($"Read-only foreground classification: {samples} samples; average {timer.Elapsed.TotalMicroseconds / samples:F3} us. This is not network or broker injection latency.");
    }

    [TestMethod]
    public void MeasuresRepeatedPointerTargetClassificationWithoutInjectingInput()
    {
        // Only presses and wheels pay this; movement never calls it.
        for (int i = 0; i < 100; i++) ForegroundInputAccess.PointerTargetRequiresBroker(5, 5);
        const int samples = 10000;
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < samples; i++) ForegroundInputAccess.PointerTargetRequiresBroker(5, 5);
        timer.Stop();
        TestContext.WriteLine($"Read-only pointer-target classification: {samples} samples; average {timer.Elapsed.TotalMicroseconds / samples:F3} us. This is not network or broker injection latency.");
    }

    [TestMethod]
    public void OnlyPressesAndWheelsLookUpTheWindowUnderThePointer()
    {
        NativeMethods.INPUT64 Mouse(int flags) { NativeMethods.INPUT64 input = default; input.type = 0; input.mi.dwFlags = flags; return input; }
        const int absolute = 0x0001 | 0x8000 | 0x4000;
        foreach (int flags in new[] { 0x0002, 0x0008, 0x0020, 0x0080, 0x0800, 0x1000 })
        {
            Assert.IsTrue(InputSimulation.PressesOrScrolls(Mouse(flags)), flags.ToString("X"));
            Assert.IsTrue(InputSimulation.PressesOrScrolls(Mouse(flags | absolute)), flags.ToString("X"));
        }
        // Movement stays on the direct path, and a release follows wherever its press went.
        foreach (int flags in new[] { 0, 0x0001, absolute, 0x0004, 0x0010, 0x0040, 0x0100, 0x0004 | absolute })
            Assert.IsFalse(InputSimulation.PressesOrScrolls(Mouse(flags)), flags.ToString("X"));
        NativeMethods.INPUT64 key = default; key.type = 1; key.ki.dwFlags = 0x0002;
        Assert.IsFalse(InputSimulation.PressesOrScrolls(key), "Keyboard input goes to the foreground window.");
    }

    [TestMethod]
    public void APressIsLookedUpWhereItsPacketSaysItLands()
    {
        NativeMethods.INPUT64 input = default; input.type = 0;
        input.mi.dwFlags = 0x0001 | 0x8000 | 0x4000 | 0x0002;
        var desktop = new MyRectangle { Left = -2560, Top = -1440, Right = 2560, Bottom = 1440 };
        input.mi.dx = 0; input.mi.dy = 0;
        Assert.IsTrue(InputSimulation.TryPointerTarget(input, desktop, out int x, out int y));
        Assert.AreEqual((-2560, -1440), (x, y));
        input.mi.dx = 65535; input.mi.dy = 65535;
        Assert.IsTrue(InputSimulation.TryPointerTarget(input, desktop, out x, out y));
        Assert.AreEqual((2560, 1440), (x, y));
        input.mi.dx = 32768; input.mi.dy = 32768;
        Assert.IsTrue(InputSimulation.TryPointerTarget(input, desktop, out x, out y));
        Assert.IsTrue(Math.Abs(x) <= 1 && Math.Abs(y) <= 1, $"{x},{y}");
        // Without an absolute position, or without a known desktop, nothing is looked up.
        input.mi.dwFlags = 0x0002;
        Assert.IsFalse(InputSimulation.TryPointerTarget(input, desktop, out _, out _));
        input.mi.dwFlags = 0x0001 | 0x8000 | 0x0002;
        Assert.IsFalse(InputSimulation.TryPointerTarget(input, null!, out _, out _));
        Assert.IsFalse(InputSimulation.TryPointerTarget(input, new MyRectangle(), out _, out _));
    }

    [TestMethod]
    public void ReadsCurrentProcessMandatoryIntegrityFromWindowsToken()
    {
        using var process = Process.GetCurrentProcess();
        int? integrity = ForegroundInputAccess.ReadProcessIntegrity(process.Handle);
        Assert.IsNotNull(integrity, "The process's own queryable token must be readable.");
        Assert.IsTrue(integrity >= 0x1000 && integrity <= 0x5000, "Expected a Windows mandatory integrity RID.");
    }

    [TestMethod]
    public void AnUnqueryableProcessRemainsUnknown()
    {
        Assert.IsNull(ForegroundInputAccess.ReadProcessIntegrity(IntPtr.Zero));
    }
}
