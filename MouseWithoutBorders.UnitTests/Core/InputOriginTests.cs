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
            Assert.AreEqual(1, delivered, "Another injector such as CRD remains a valid input source.");
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
