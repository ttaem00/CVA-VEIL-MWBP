using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class RelativeMouseTests
{
    private static DATA Move(int x, int y, uint des = 202) => new()
    { Type = PackageType.Mouse, Des = (ID)des, Md = new() { dwFlags = WM.WM_MOUSEMOVE, X = x, Y = y } };

    [TestMethod]
    public void ADistanceSurvivesTheWireFormInBothDirections()
    {
        foreach (int distance in new[] { 0, 1, -1, 7, -7, 999, -999, RelativeMouse.MaxDistance, -RelativeMouse.MaxDistance })
        {
            int wire = RelativeMouse.Encode(distance);
            Assert.IsTrue(Math.Abs(wire) >= Event.MOVE_MOUSE_RELATIVE, distance.ToString());
            Assert.AreEqual(distance, RelativeMouse.Decode(wire), distance.ToString());
        }
    }

    [TestMethod]
    public void OnlyAMovementWithBothAxesMarkedIsADistance()
    {
        Assert.IsTrue(RelativeMouse.IsRelativeMove(Move(RelativeMouse.Encode(3), RelativeMouse.Encode(-4))));
        Assert.IsTrue(RelativeMouse.IsRelativeMove(Move(RelativeMouse.Encode(0), RelativeMouse.Encode(0))));
        // Positions, half-marked packets and values beyond any real mouse movement are not distances.
        Assert.IsFalse(RelativeMouse.IsRelativeMove(Move(100, 200)));
        Assert.IsFalse(RelativeMouse.IsRelativeMove(Move(65535, 65535)));
        Assert.IsFalse(RelativeMouse.IsRelativeMove(Move(RelativeMouse.Encode(3), 200)));
        Assert.IsFalse(RelativeMouse.IsRelativeMove(Move(Event.XY_BY_PIXEL + 10, Event.XY_BY_PIXEL + 10)));
        var press = Move(Event.MOVE_MOUSE_RELATIVE, Event.MOVE_MOUSE_RELATIVE);
        press.Md.dwFlags = WM.WM_LBUTTONDOWN;
        Assert.IsFalse(RelativeMouse.IsRelativeMove(press), "A press where the pointer is carries no distance.");
        var key = new DATA { Type = PackageType.Keyboard, Des = (ID)202 };
        Assert.IsFalse(RelativeMouse.IsRelativeMove(key));
    }

    [TestMethod]
    public void UnsentDistancesAddUpAndNeverReplaceEachOther()
    {
        Assert.IsTrue(RelativeMouse.TryMerge(Move(RelativeMouse.Encode(5), RelativeMouse.Encode(-2)),
            Move(RelativeMouse.Encode(-8), RelativeMouse.Encode(-3)), out var merged));
        Assert.AreEqual(-3, RelativeMouse.Decode(merged.Md.X));
        Assert.AreEqual(-5, RelativeMouse.Decode(merged.Md.Y));
        Assert.IsTrue(RelativeMouse.IsRelativeMove(merged), "Opposite movements that cancel must still be a distance, not a position.");
        Assert.IsTrue(RelativeMouse.TryMerge(Move(RelativeMouse.Encode(4), RelativeMouse.Encode(4)),
            Move(RelativeMouse.Encode(-4), RelativeMouse.Encode(-4)), out merged));
        Assert.AreEqual(0, RelativeMouse.Decode(merged.Md.X));
        Assert.IsTrue(RelativeMouse.IsRelativeMove(merged));
        // A position and a distance, two peers, or a sum beyond the wire range stay separate packets.
        Assert.IsFalse(RelativeMouse.TryMerge(Move(10, 10), Move(RelativeMouse.Encode(1), RelativeMouse.Encode(1)), out _));
        Assert.IsFalse(RelativeMouse.TryMerge(Move(RelativeMouse.Encode(1), RelativeMouse.Encode(1)), Move(10, 10), out _));
        Assert.IsFalse(RelativeMouse.TryMerge(Move(RelativeMouse.Encode(1), RelativeMouse.Encode(1), 202),
            Move(RelativeMouse.Encode(1), RelativeMouse.Encode(1), 303), out _));
        Assert.IsFalse(RelativeMouse.TryMerge(Move(RelativeMouse.Encode(RelativeMouse.MaxDistance), RelativeMouse.Encode(0)),
            Move(RelativeMouse.Encode(1), RelativeMouse.Encode(0)), out _));
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
}
