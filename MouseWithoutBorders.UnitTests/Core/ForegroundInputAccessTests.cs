using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
