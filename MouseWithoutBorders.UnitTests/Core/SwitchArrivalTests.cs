using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class SwitchArrivalTests
{
    [TestMethod]
    [DataRow(WM.WM_LBUTTONDOWN, (int)NativeMethods.MOUSEEVENTF.LEFTDOWN)]
    [DataRow(WM.WM_LBUTTONUP, (int)NativeMethods.MOUSEEVENTF.LEFTUP)]
    [DataRow(WM.WM_RBUTTONDOWN, (int)NativeMethods.MOUSEEVENTF.RIGHTDOWN)]
    [DataRow(WM.WM_MOUSEWHEEL, (int)NativeMethods.MOUSEEVENTF.WHEEL)]
    public void FirstButtonOrWheelCarriesArrivalInTheSameNativeInput(int message, int action)
    {
        var input = InputSimulation.MouseInput(new MOUSEDATA { X = 12000, Y = 25000, dwFlags = message, WheelDelta = 120 });
        Assert.AreEqual(12000, input.dx);
        Assert.AreEqual(25000, input.dy);
        Assert.AreEqual(120, input.mouseData);
        Assert.AreEqual((int)(NativeMethods.MOUSEEVENTF.MOVE | NativeMethods.MOUSEEVENTF.ABSOLUTE |
            NativeMethods.MOUSEEVENTF.VIRTUALDESK) | action, input.dwFlags);
    }
}
