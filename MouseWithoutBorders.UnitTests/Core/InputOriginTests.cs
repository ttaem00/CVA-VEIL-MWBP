using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class InputOriginTests
{
    [TestMethod]
    public void ChromeRemoteInputStaysLocalWithoutChangingPhysicalKeyOrMouseRouting()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        var held = (HeldKeyChord)typeof(InputHook).GetField("easyMouseKeys", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        int[] original = held.DownKeys;
        var oldPoint = Common.LastPos; var oldDestination = MachineStuff.desMachineID; var oldNext = MachineStuff.newDesMachineID;
        bool oldRecording = InputHook.RecordingKeys; var oldRecorder = InputHook.KeyRecorder;
        ulong oldCount = Event.RealInputEventCount;
        int keys = 0, mice = 0, recorded = 0;
        hook.KeyboardEvent += _ => keys++;
        hook.MouseEvent += (_, _, _) => { mice++; return true; };
        IntPtr keyboard = Marshal.AllocHGlobal(24), mouse = Marshal.AllocHGlobal(32);
        try
        {
            held.Clear(); held.Update(0x14, true); // The physical activation key is already held.
            MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
            Common.LastPos = new(100, 100);
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(keyboard, i, 0);
            for (int i = 0; i < 32; i += 4) Marshal.WriteInt32(mouse, i, 0);
            Marshal.WriteInt32(keyboard, 0, 0x14); Marshal.WriteInt32(keyboard, 8, 0x90);
            Marshal.WriteIntPtr(keyboard, 16, new IntPtr(0x43524430)); // Official Chromium CRD0 source tag.
            var keyCallback = typeof(InputHook).GetMethod("KeyboardHookProc", flags)!;
            Assert.AreEqual(0, keyCallback.Invoke(hook, [0, WM.WM_KEYUP, keyboard]), "Remote key-up must reach GAME rather than being swallowed/forwarded.");
            CollectionAssert.AreEqual(new[] { 0x14 }, held.DownKeys, "Remote key-up cannot release the physical activation chord.");
            InputHook.RecordingKeys = true; InputHook.KeyRecorder = (_, _) => recorded++;
            keyCallback.Invoke(hook, [0, WM.WM_KEYDOWN, keyboard]);
            Assert.AreEqual(0, recorded, "Remote input cannot pollute physical shortcut recording.");
            InputHook.RecordingKeys = false;
            Marshal.WriteInt32(mouse, 0, 2000); Marshal.WriteInt32(mouse, 4, 800);
            Marshal.WriteInt32(mouse, 12, 1); Marshal.WriteIntPtr(mouse, 24, new IntPtr(0x43524430));
            var mouseCallback = typeof(InputHook).GetMethod("MouseHookProc", flags)!;
            foreach (int message in new[] { WM.WM_MOUSEMOVE, WM.WM_LBUTTONDOWN, WM.WM_LBUTTONUP, WM.WM_MOUSEWHEEL })
                Assert.AreEqual(0, mouseCallback.Invoke(hook, [0, message, mouse]), "Remote input must stay on its remote desktop.");
            Assert.AreEqual(0, keys); Assert.AreEqual(0, mice); Assert.AreEqual(oldCount, Event.RealInputEventCount);
            Assert.AreEqual((ID)202, Common.DesMachineID); Assert.AreEqual(new System.Drawing.Point(100, 100), Common.LastPos);
            int dx = 0, dy = 0;
            hook.MouseEvent += (_, x, y) => { dx = x; dy = y; return true; };
            Marshal.WriteInt32(mouse, 0, 1998); Marshal.WriteInt32(mouse, 4, 801); Marshal.WriteInt32(mouse, 12, 0);
            mouseCallback.Invoke(hook, [0, WM.WM_MOUSEMOVE, mouse]);
            Assert.AreEqual(-2, dx); Assert.AreEqual(1, dy, "Remote cursor travel must not become physical mouse delta.");
        }
        finally
        {
            held.Clear(); foreach (int key in original) held.Update(key, true);
            Common.LastPos = oldPoint; MachineStuff.desMachineID = oldDestination; MachineStuff.newDesMachineID = oldNext;
            InputHook.RecordingKeys = oldRecording; InputHook.KeyRecorder = oldRecorder; Event.RealInputEventCount = oldCount;
            Marshal.FreeHGlobal(keyboard); Marshal.FreeHGlobal(mouse);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NewerMwbWarpSupersedesTheRemoteCursorBaseline(bool callbackObserved)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        var old = Common.LastPos; ulong oldReal = Event.RealInputEventCount, oldTotal = Event.InputEventCount;
        IntPtr payload = Marshal.AllocHGlobal(32); int dx = 0, dy = 0;
        hook.MouseEvent += (_, x, y) => { dx = x; dy = y; return true; };
        var callback = typeof(InputHook).GetMethod("MouseHookProc", flags)!;
        try
        {
            for (int i = 0; i < 32; i += 4) Marshal.WriteInt32(payload, i, 0);
            void Move(int x, int y, bool injected, IntPtr tag)
            {
                Marshal.WriteInt32(payload, 0, x); Marshal.WriteInt32(payload, 4, y);
                Marshal.WriteInt32(payload, 12, injected ? 1 : 0); Marshal.WriteIntPtr(payload, 24, tag);
                callback.Invoke(hook, [0, WM.WM_MOUSEMOVE, payload]);
            }
            Common.LastPos = new(100, 100);
            Move(2000, 800, true, new(0x43524430));
            if (callbackObserved) Move(500, 600, true, InputSimulation.InjectedInputTag);
            else Common.LastPos = new(500, 600); // HideMouseCursor updates its anchor before SetCursorPos.
            Move(502, 600, false, IntPtr.Zero);
            Assert.AreEqual(2, dx); Assert.AreEqual(0, dy);
            // Coincident marker bits alone must never classify physical movement as remote.
            int before = dx;
            Move(510, 600, false, new(0x43524430));
            Assert.AreNotEqual(before, dx);
        }
        finally { Common.LastPos = old; Event.RealInputEventCount = oldReal; Event.InputEventCount = oldTotal; Marshal.FreeHGlobal(payload); }
    }

    [TestMethod]
    public void LocalOleDragIsCancelledBeforeReleasingTheNativeButton()
    {
        var inputs = InputSimulation.OleDragCancelInputs();
        Assert.AreEqual(3, inputs.Length, "A bare LEFTUP can drop or move the source file into the window below the helper.");
        Assert.AreEqual(1, inputs[0].type);
        Assert.AreEqual((short)0x1b, inputs[0].ki.wVk);
        Assert.AreEqual(0, inputs[0].ki.dwFlags);
        Assert.AreEqual(1, inputs[1].type);
        Assert.AreEqual((short)0x1b, inputs[1].ki.wVk);
        Assert.AreEqual((int)NativeMethods.KEYEVENTF.KEYUP, inputs[1].ki.dwFlags);
        Assert.AreEqual(0, inputs[2].type);
        Assert.AreEqual((int)NativeMethods.MOUSEEVENTF.LEFTUP, inputs[2].mi.dwFlags);
        foreach (var input in inputs)
            Assert.AreEqual(InputSimulation.InjectedInputTag, input.type == 1 ? input.ki.dwExtraInfo : input.mi.dwExtraInfo);
    }

    [TestMethod]
    public void LocalOleEscapePassesWindowsWithoutRunningKeyboardActions()
    {
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook));
        GC.SuppressFinalize(hook);
        var callback = typeof(InputHook).GetMethod("KeyboardHookProc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int delivered = 0;
        hook.KeyboardEvent += _ => delivered++;
        var beforeReal = Event.RealInputEventCount;
        var beforeCount = Event.InputEventCount;
        IntPtr payload = Marshal.AllocHGlobal(24);
        try
        {
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(payload, i, 0);
            foreach (var input in InputSimulation.OleDragCancelInputs().Where(input => input.type == 1))
            {
                Marshal.WriteInt32(payload, 0, input.ki.wVk);
                bool up = (input.ki.dwFlags & (int)NativeMethods.KEYEVENTF.KEYUP) != 0;
                Marshal.WriteInt32(payload, 8, up ? 0x90 : 0x10);
                Marshal.WriteIntPtr(payload, 16, input.ki.dwExtraInfo);
                callback.Invoke(hook, [0, up ? WM.WM_KEYUP : WM.WM_KEYDOWN, payload]);
            }
            Assert.AreEqual(beforeCount + 2, Event.InputEventCount, "Both cancellation key events must traverse the real hook.");
            Assert.AreEqual(beforeReal, Event.RealInputEventCount, "Cancellation must bypass shortcut and network processing.");
            Assert.AreEqual(0, delivered);
        }
        finally
        {
            Event.RealInputEventCount = beforeReal;
            Event.InputEventCount = beforeCount;
            Marshal.FreeHGlobal(payload);
        }
    }

    [TestMethod]
    public void SupportedNativeInputHasOne64BitUnion()
    {
        Assert.AreEqual(40, Marshal.SizeOf<NativeMethods.INPUT64>());
        Assert.AreEqual(8, Marshal.OffsetOf<NativeMethods.INPUT64>("mi").ToInt32());
        Assert.AreEqual(8, Marshal.OffsetOf<NativeMethods.INPUT64>("ki").ToInt32());
    }

    [TestMethod]
    [DataRow("MouseLLHookStruct", 24, 32, 20, 24)]
    [DataRow("KeyboardHookStruct", 16, 24, 16, 20)]
    public void HookPayloadUsesNativePointerSizedExtraInfo(string name, int offset64, int size64, int offset32, int size32)
    {
        var type = typeof(InputHook).GetNestedType(name, BindingFlags.NonPublic)!;
        Assert.AreEqual(IntPtr.Size == 8 ? offset64 : offset32, Marshal.OffsetOf(type, "DwExtraInfo").ToInt32());
        Assert.AreEqual(IntPtr.Size == 8 ? size64 : size32, Marshal.SizeOf(type));
        Assert.AreEqual(typeof(IntPtr), type.GetField("DwExtraInfo", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
    }

    [TestMethod]
    public void OwnKeyDoesNotLoopButForeignInjectedAndPhysicalKeysStillForward()
    {
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook));
        GC.SuppressFinalize(hook);
        var callback = typeof(InputHook).GetMethod("KeyboardHookProc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int delivered = 0;
        hook.KeyboardEvent += _ => delivered++;
        IntPtr payload = Marshal.AllocHGlobal(24);
        try
        {
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(payload, i, 0);
            Marshal.WriteInt32(payload, 0, 0x87); // F24, no modifier or Easy Mouse state.
            Marshal.WriteInt32(payload, 8, 0x90); // injected + key up
            Marshal.WriteIntPtr(payload, 16, new IntPtr(0x4D574250));
            callback.Invoke(hook, [0, WM.WM_KEYUP, payload]);
            Assert.AreEqual(0, delivered, "The app's own injected key must reach Windows without entering its network path again.");
            Marshal.WriteIntPtr(payload, 16, new IntPtr(0x1234));
            callback.Invoke(hook, [0, WM.WM_KEYUP, payload]);
            Assert.AreEqual(1, delivered, "An unmarked local remapper or assistive injector remains a valid input source.");
            Marshal.WriteInt32(payload, 8, 0x80); // physical key up with coincident extra info
            Marshal.WriteIntPtr(payload, 16, new IntPtr(0x4D574250));
            callback.Invoke(hook, [0, WM.WM_KEYUP, payload]);
            Assert.AreEqual(2, delivered, "The marker alone must not suppress a physical event.");
            callback.Invoke(hook, [-1, WM.WM_KEYUP, IntPtr.Zero]);
            Assert.AreEqual(2, delivered, "A negative hook code must chain without reading the payload.");
        }
        finally { Marshal.FreeHGlobal(payload); }
    }

    [TestMethod]
    public void OwnMouseUpCannotConsumeTheFollowingPhysicalOrForeignMouseUp()
    {
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook));
        GC.SuppressFinalize(hook);
        var callback = typeof(InputHook).GetMethod("MouseHookProc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var oldDestination = MachineStuff.newDesMachineID;
        bool oldDown = DragDrop.MouseDown;
        int delivered = 0;
        hook.MouseEvent += (_, _, _) => { delivered++; return false; };
        IntPtr payload = Marshal.AllocHGlobal(32);
        try
        {
            MachineStuff.newDesMachineID = Common.MachineID;
            for (int i = 0; i < 32; i += 4) Marshal.WriteInt32(payload, i, 0);
            int extraOffset = IntPtr.Size == 8 ? 24 : 20;
            Marshal.WriteInt32(payload, 12, 1);
            Marshal.WriteIntPtr(payload, extraOffset, new IntPtr(0x4D574250));
            callback.Invoke(hook, [0, WM.WM_LBUTTONUP, payload]);
            Assert.AreEqual(0, delivered);
            Marshal.WriteInt32(payload, 12, 0);
            Marshal.WriteIntPtr(payload, extraOffset, IntPtr.Zero);
            callback.Invoke(hook, [0, WM.WM_LBUTTONUP, payload]);
            Assert.AreEqual(1, delivered, "An own UP must not leave suppression state for a later physical UP.");
            Marshal.WriteInt32(payload, 12, 1);
            Marshal.WriteIntPtr(payload, extraOffset, new IntPtr(0x1234));
            callback.Invoke(hook, [0, WM.WM_LBUTTONUP, payload]);
            Assert.AreEqual(2, delivered);
            Marshal.WriteInt32(payload, 12, 0);
            Marshal.WriteIntPtr(payload, extraOffset, new IntPtr(0x4D574250));
            callback.Invoke(hook, [0, WM.WM_LBUTTONUP, payload]);
            Assert.AreEqual(3, delivered);
            callback.Invoke(hook, [-1, WM.WM_LBUTTONUP, IntPtr.Zero]);
            Assert.AreEqual(3, delivered);
        }
        finally
        {
            MachineStuff.newDesMachineID = oldDestination;
            DragDrop.MouseDown = oldDown;
            Marshal.FreeHGlobal(payload);
        }
    }
}
