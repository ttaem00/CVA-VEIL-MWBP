using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class VirtualDesktopTests
{
    [TestMethod]
    public void AbsoluteInputUsesTheWholeDesktop()
    {
        foreach (int coordinate in new[] { 0, 16384, 32768, 65535 })
        {
            var input = InputSimulation.AbsoluteMouseInput(coordinate, 65535 - coordinate);
            Assert.AreEqual(coordinate, input.dx);
            Assert.AreEqual(65535 - coordinate, input.dy);
            Assert.AreEqual(0xC001, input.dwFlags, "MOVE | ABSOLUTE | VIRTUALDESK must reach SendInput.");
        }
    }

    [TestMethod]
    public void ActualDisplayEnumerationIncludesEveryActiveMonitor()
    {
        var previousDesktop = MachineStuff.desktopBounds;
        var previousPrimary = MachineStuff.primaryScreenBounds;
        int previousWidth = Common.screenWidth, previousHeight = Common.screenHeight;
        var previousPoints = WinAPI.SensitivePoints.ToArray();
        try
        {
            WinAPI.GetScreenConfig();
            var expected = Screen.AllScreens.Select(s => s.Bounds).Aggregate(System.Drawing.Rectangle.Union);
            var actual = MachineStuff.DesktopBounds;
            Assert.AreEqual(expected, System.Drawing.Rectangle.FromLTRB(actual.Left, actual.Top, actual.Right, actual.Bottom));
            Console.WriteLine($"Active displays: {Screen.AllScreens.Length}; virtual desktop: {expected}");
        }
        finally
        {
            MachineStuff.desktopBounds = previousDesktop;
            MachineStuff.primaryScreenBounds = previousPrimary;
            Common.screenWidth = previousWidth; Common.screenHeight = previousHeight;
            lock (WinAPI.SensitivePoints) { WinAPI.SensitivePoints.Clear(); WinAPI.SensitivePoints.AddRange(previousPoints); }
        }
    }
}
