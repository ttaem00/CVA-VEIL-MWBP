using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class PointerCaptureTests
{
    private static DATA Move(int x, int y, uint des = 202) => new()
    { Type = PackageType.Mouse, Des = (ID)des, Md = new() { dwFlags = WM.WM_MOUSEMOVE, X = x, Y = y } };

    [TestMethod]
    public void ADistanceSurvivesTheWireFormInBothDirections()
    {
        foreach (int distance in new[] { 0, 1, -1, 7, -7, 999, -999, PointerCapture.MaxDistance, -PointerCapture.MaxDistance })
        {
            int wire = PointerCapture.Encode(distance);
            Assert.IsTrue(Math.Abs(wire) >= Event.MOVE_MOUSE_RELATIVE, distance.ToString());
            Assert.AreEqual(distance, PointerCapture.Decode(wire), distance.ToString());
        }
    }

    [TestMethod]
    public void OnlyAMovementWithBothAxesMarkedIsADistance()
    {
        Assert.IsTrue(PointerCapture.IsRelativeMove(Move(PointerCapture.Encode(3), PointerCapture.Encode(-4))));
        Assert.IsTrue(PointerCapture.IsRelativeMove(Move(PointerCapture.Encode(0), PointerCapture.Encode(0))));
        // Positions, half-marked packets and values beyond any real mouse movement are not distances.
        Assert.IsFalse(PointerCapture.IsRelativeMove(Move(100, 200)));
        Assert.IsFalse(PointerCapture.IsRelativeMove(Move(65535, 65535)));
        Assert.IsFalse(PointerCapture.IsRelativeMove(Move(PointerCapture.Encode(3), 200)));
        Assert.IsFalse(PointerCapture.IsRelativeMove(Move(Event.XY_BY_PIXEL + 10, Event.XY_BY_PIXEL + 10)));
        var press = Move(Event.MOVE_MOUSE_RELATIVE, Event.MOVE_MOUSE_RELATIVE);
        press.Md.dwFlags = WM.WM_LBUTTONDOWN;
        Assert.IsFalse(PointerCapture.IsRelativeMove(press), "A press where the pointer is carries no distance.");
        var key = new DATA { Type = PackageType.Keyboard, Des = (ID)202 };
        Assert.IsFalse(PointerCapture.IsRelativeMove(key));
    }

    [TestMethod]
    public void UnsentDistancesAddUpAndNeverReplaceEachOther()
    {
        Assert.IsTrue(PointerCapture.TryMerge(Move(PointerCapture.Encode(5), PointerCapture.Encode(-2)),
            Move(PointerCapture.Encode(-8), PointerCapture.Encode(-3)), out var merged));
        Assert.AreEqual(-3, PointerCapture.Decode(merged.Md.X));
        Assert.AreEqual(-5, PointerCapture.Decode(merged.Md.Y));
        Assert.IsTrue(PointerCapture.IsRelativeMove(merged), "Opposite movements that cancel must still be a distance, not a position.");
        Assert.IsTrue(PointerCapture.TryMerge(Move(PointerCapture.Encode(4), PointerCapture.Encode(4)),
            Move(PointerCapture.Encode(-4), PointerCapture.Encode(-4)), out merged));
        Assert.AreEqual(0, PointerCapture.Decode(merged.Md.X));
        Assert.IsTrue(PointerCapture.IsRelativeMove(merged));
        // A position and a distance, two peers, or a sum beyond the wire range stay separate packets.
        Assert.IsFalse(PointerCapture.TryMerge(Move(10, 10), Move(PointerCapture.Encode(1), PointerCapture.Encode(1)), out _));
        Assert.IsFalse(PointerCapture.TryMerge(Move(PointerCapture.Encode(1), PointerCapture.Encode(1)), Move(10, 10), out _));
        Assert.IsFalse(PointerCapture.TryMerge(Move(PointerCapture.Encode(1), PointerCapture.Encode(1), 202),
            Move(PointerCapture.Encode(1), PointerCapture.Encode(1), 303), out _));
        Assert.IsFalse(PointerCapture.TryMerge(Move(PointerCapture.Encode(PointerCapture.MaxDistance), PointerCapture.Encode(0)),
            Move(PointerCapture.Encode(1), PointerCapture.Encode(0)), out _));
    }

    [TestMethod]
    public void AHiddenPointerIsRepeatedAndAVisibleOneIsSaidOnce()
    {
        // (hidden, reportedHidden, sameConnection, now, nextRepeat, acknowledged)
        Assert.IsTrue(PointerCapture.ShouldReport(true, false, true, 0, 0, false), "Newly hidden.");
        Assert.IsFalse(PointerCapture.ShouldReport(true, true, true, 500, 1000, false), "Still hidden, repeat not due.");
        Assert.IsTrue(PointerCapture.ShouldReport(true, true, true, 1000, 1000, false), "Still hidden and positions keep arriving: say it again.");
        Assert.IsFalse(PointerCapture.ShouldReport(true, true, true, 9000, 1000, true), "Distances are arriving: the controller knows, so nothing is repeated.");
        Assert.IsTrue(PointerCapture.ShouldReport(false, true, true, 0, 1000, true), "Shown again.");
        Assert.IsFalse(PointerCapture.ShouldReport(false, false, true, 5000, 0, false), "Visible and already said: nothing is sent for ordinary use.");
        Assert.IsTrue(PointerCapture.ShouldReport(true, true, false, 0, 1000, true), "A new connection has not heard that the pointer is hidden.");
        Assert.IsTrue(PointerCapture.ShouldReport(false, true, false, 0, 1000, false), "A new connection must not be left believing the pointer is hidden.");
        Assert.IsFalse(PointerCapture.ShouldReport(false, false, false, 0, 0, false), "A new connection needs no notice about a visible pointer.");
    }

    [TestMethod]
    public void APressWhereThePointerIsCarriesNoMovement()
    {
        var press = InputSimulation.AtPointer(InputSimulation.MouseInput(new MOUSEDATA { dwFlags = WM.WM_LBUTTONDOWN, X = Event.MOVE_MOUSE_RELATIVE, Y = Event.MOVE_MOUSE_RELATIVE }));
        Assert.AreEqual((int)NativeMethods.MOUSEEVENTF.LEFTDOWN, press.dwFlags);
        Assert.AreEqual(0, press.dx); Assert.AreEqual(0, press.dy);
        var wheel = InputSimulation.AtPointer(InputSimulation.MouseInput(new MOUSEDATA { dwFlags = WM.WM_MOUSEWHEEL, WheelDelta = -120, X = 5, Y = 5 }));
        Assert.AreEqual((int)NativeMethods.MOUSEEVENTF.WHEEL, wheel.dwFlags);
        Assert.AreEqual(-120, wheel.mouseData);
        // A plain movement has nothing left to apply.
        Assert.AreEqual(0, InputSimulation.AtPointer(InputSimulation.MouseInput(new MOUSEDATA { dwFlags = WM.WM_MOUSEMOVE, X = 5, Y = 5 })).dwFlags);
    }

    [TestMethod]
    public void OnlyThePeerThatReportedAHiddenPointerIsTreatedAsCaptured()
    {
        PointerCapture.ResetForTest();
        try
        {
            Assert.IsFalse(PointerCapture.IsCaptured(202));
            Assert.IsNull(PointerCapture.TakeResume(202));
            // A notice that names no connection, or is addressed elsewhere, changes nothing.
            var notice = new DATA { Type = PackageType.PointerCapture, Des = Common.MachineID };
            notice.Md.dwFlags = 1;
            PointerCapture.Receive(notice, null!);
            Assert.IsFalse(PointerCapture.IsCaptured(202));
        }
        finally { PointerCapture.ResetForTest(); }
    }
}
