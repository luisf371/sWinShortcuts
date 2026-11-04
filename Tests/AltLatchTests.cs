using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class AltLatchTests
{
    private static readonly int Q = KeyInterop.VirtualKeyFromKey(Key.Q);

    [Fact]
    public void AppHeldRightAlt_RebaselineAndLeftAltTap_DoNotLatchAlt()
    {
        var windowsKeys = new bool[256];
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender, Stopwatch.GetTimestamp,
            vk => Volatile.Read(ref windowsKeys[vk]), FakeAutoRunTransport.MatchingForeground());
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureActiveProfileForTesting(CreateProfile(), foregroundGeneration: 1, altPressed: false);

            // Hold-breath bound to RightAlt presses it through SendInput, so Windows reports it held.
            InjectedKeyboardEvent(service, 0xA5, down: true);
            Volatile.Write(ref windowsKeys[0xA5], true);

            // A focus change re-baselines Alt from Windows while that hold is still active.
            service.ReleaseForegroundState();
            Assert.False(Press(service, Q));

            // A physical LeftAlt tap while the app still holds RightAlt.
            Assert.False(service.DispatchDecodedKeyboardEvent(0xA4, isKeyDown: true, isKeyUp: false));
            Volatile.Write(ref windowsKeys[0xA4], true);
            Assert.False(service.DispatchDecodedKeyboardEvent(0xA4, isKeyDown: false, isKeyUp: true));
            Volatile.Write(ref windowsKeys[0xA4], false);
            Assert.False(Press(service, Q));

            // Control: a held physical LeftAlt still arms the binding.
            Assert.False(service.DispatchDecodedKeyboardEvent(0xA4, isKeyDown: true, isKeyUp: false));
            Volatile.Write(ref windowsKeys[0xA4], true);
            Assert.True(Press(service, Q));
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void AltReleasedWhereHookCannotSeeIt_NextTriggerPassesThrough()
    {
        var windowsKeys = new bool[256];
        using var service = new InputHookService(new NullLoggerService(), new RecordingInputSender(), Stopwatch.GetTimestamp,
            vk => Volatile.Read(ref windowsKeys[vk]), FakeAutoRunTransport.MatchingForeground());
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureActiveProfileForTesting(CreateProfile(), foregroundGeneration: 1, altPressed: false);
            Assert.False(service.DispatchDecodedKeyboardEvent(0xA4, isKeyDown: true, isKeyUp: false));
            Volatile.Write(ref windowsKeys[0xA4], true);
            Assert.True(Press(service, Q));

            // Released on the secure desktop (Ctrl+Alt+Del, UAC): the hook never sees the UP.
            Volatile.Write(ref windowsKeys[0xA4], false);
            Assert.False(Press(service, Q));
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    private static bool Press(InputHookService service, int vk)
    {
        var consumed = service.DispatchDecodedKeyboardEvent(vk, isKeyDown: true, isKeyUp: false);
        Assert.Equal(consumed, service.DispatchDecodedKeyboardEvent(vk, isKeyDown: false, isKeyUp: true));
        return consumed;
    }

    private static void InjectedKeyboardEvent(InputHookService service, int vk, bool down)
    {
        var payload = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.KBDLLHOOKSTRUCT>());
        try
        {
            Marshal.StructureToPtr(new NativeMethods.KBDLLHOOKSTRUCT
            {
                vkCode = (uint)vk,
                flags = NativeMethods.KbdLlFlags.LLKHF_INJECTED | (down ? default : NativeMethods.KbdLlFlags.LLKHF_UP),
                dwExtraInfo = NativeMethods.INPUT_IGNORE
            }, payload, false);
            var callback = typeof(InputHookService).GetMethod("KeyboardCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;
            callback.Invoke(service, [0, (IntPtr)(down ? NativeMethods.WM_KEYDOWN : NativeMethods.WM_KEYUP), payload]);
        }
        finally
        {
            Marshal.FreeHGlobal(payload);
        }
    }

    private static Profile CreateProfile() => new()
    {
        Name = "Game",
        Executable = "game.exe",
        AltKeyboard =
        {
            IsEnabled = true,
            Bindings = new Dictionary<Key, AltKeyboardBinding>
            {
                [Key.Q] = new() { TapKey = Key.A }
            }
        }
    };
}
