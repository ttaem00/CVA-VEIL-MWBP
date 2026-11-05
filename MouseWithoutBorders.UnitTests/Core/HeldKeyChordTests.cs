using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class HeldKeyChordTests
{
    [TestMethod]
    public void ActionChordRequiresExactKeysAndDistinguishesRepeatFromANewPress()
    {
        var chord = new HeldKeyChord();
        Assert.IsTrue(chord.Update((int)Keys.CapsLock, true));
        Assert.IsTrue(chord.MatchesExact([(int)Keys.CapsLock]));
        Assert.IsFalse(chord.Update((int)Keys.CapsLock, true));
        chord.Update((int)Keys.RControlKey, true);
        Assert.IsFalse(chord.MatchesExact([(int)Keys.CapsLock]));
        Assert.IsTrue(chord.MatchesExact([(int)Keys.ControlKey, (int)Keys.CapsLock]));
        Assert.IsFalse(chord.MatchesExact([(int)Keys.LControlKey, (int)Keys.CapsLock]));
        chord.Update((int)Keys.CapsLock, false);
        Assert.IsTrue(chord.MatchesExact([(int)Keys.ControlKey]), "A modifier-only assignment is supported too.");
        Assert.IsFalse(chord.MatchesExact([]));
    }

    [TestMethod]
    public void UnrelatedKeyEventsDoNotReleaseHeldModifier()
    {
        var chord = new HeldKeyChord();
        chord.Update((int)Keys.RControlKey, true);
        chord.Update((int)Keys.A, true);
        chord.Update((int)Keys.A, false);
        Assert.IsTrue(chord.IsActive(EasyMouseOption.Ctrl, []));
        chord.Update((int)Keys.RControlKey, false);
        Assert.IsFalse(chord.IsActive(EasyMouseOption.Ctrl, []));
    }

    [TestMethod]
    public void EitherSideModifierRemainsHeldUntilBothSidesAreReleased()
    {
        var chord = new HeldKeyChord();
        chord.Update((int)Keys.LShiftKey, true);
        chord.Update((int)Keys.RShiftKey, true);
        chord.Update((int)Keys.LShiftKey, false);
        Assert.IsTrue(chord.IsActive(EasyMouseOption.Shift, []));
        chord.Clear();
        Assert.IsFalse(chord.IsActive(EasyMouseOption.Shift, []));
    }

    [TestMethod]
    public void CustomChordRequiresAllKeysAndRespectsSideSpecificKeys()
    {
        int[] keys = [(int)Keys.RControlKey, (int)Keys.Menu, (int)Keys.F2];
        var chord = new HeldKeyChord();
        chord.Update((int)Keys.LControlKey, true);
        chord.Update((int)Keys.RMenu, true);
        chord.Update((int)Keys.F2, true);
        Assert.IsFalse(chord.IsActive(EasyMouseOption.Custom, keys));
        chord.Update((int)Keys.RControlKey, true);
        Assert.IsTrue(chord.IsActive(EasyMouseOption.Custom, keys));
        chord.Update((int)Keys.RMenu, false);
        Assert.IsFalse(chord.IsActive(EasyMouseOption.Custom, keys));
    }

    [TestMethod]
    public void EmptyCustomChordNeverEnablesSwitching()
    {
        var chord = new HeldKeyChord();
        Assert.IsFalse(chord.IsActive(EasyMouseOption.Custom, []));
        int[] keys = [256, (int)Keys.F4];
        Assert.AreEqual("Win+F4", HeldKeyChord.Format(keys));
        chord.Update((int)Keys.RWin, true);
        chord.Update((int)Keys.F4, true);
        Assert.IsTrue(chord.IsActive(EasyMouseOption.Custom, keys));
        Assert.IsFalse(chord.IsActive(EasyMouseOption.Disable, keys));
    }

    [TestMethod]
    public void CapsLockHoldActivatesAndReleaseStopsSwitching()
    {
        int[] keys = [(int)Keys.CapsLock];
        var chord = new HeldKeyChord();
        Assert.AreEqual("Caps Lock", HeldKeyChord.Format(keys));
        chord.Update((int)Keys.CapsLock, true);
        Assert.IsTrue(chord.IsActive(EasyMouseOption.Custom, keys));
        chord.Update((int)Keys.CapsLock, false);
        Assert.IsFalse(chord.IsActive(EasyMouseOption.Custom, keys));
    }
}
