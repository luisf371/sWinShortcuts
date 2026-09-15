using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class MacroHookLifecycleTests
{
    [Theory]
    [InlineData("replacement")]
    [InlineData("stopped")]
    [InlineData("disposed")]
    public async Task KeyboardCallback_PhysicalTakeoverDuringNativeMacroUp_SurvivesLifecycleFilter(string boundary)
    {
        using var fixture = new CallbackFixture();
        await fixture.StartHeldInput();
        fixture.SetBoundary(boundary, mouse: false);
        var physicalDown = (IntPtr)(-1);
        var macroUp = (IntPtr)(-1);
        var ordinaryInjectedUp = (IntPtr)(-1);
        var taggedDown = (IntPtr)(-1);
        var injectedDownCreatedTakeover = true;

        fixture.Sender.KeyResult = (key, down, macroRelease) =>
        {
            if (key == Key.A && !down && macroRelease)
            {
                // The executor already checked physical state before entering the native sender.
                // Deliver physical DOWN here, before the tagged UP reaches the hook thread.
                fixture.Keyboard(NativeMethods.WM_KEYDOWN, injected: true);
                injectedDownCreatedTakeover = fixture.Physical.HasPhysicalKeyTakeover(0x41);
                physicalDown = fixture.Keyboard(NativeMethods.WM_KEYDOWN);
                macroUp = fixture.Keyboard(NativeMethods.WM_KEYUP, injected: true, macroRelease: true);
                ordinaryInjectedUp = fixture.Keyboard(NativeMethods.WM_KEYUP, injected: true);
                taggedDown = fixture.Keyboard(NativeMethods.WM_KEYDOWN, injected: true, macroRelease: true);
            }
            return true;
        };

        Assert.True(await fixture.Cleanup());
        Assert.Equal(IntPtr.Zero, physicalDown);
        Assert.Equal((IntPtr)1, macroUp);
        Assert.Equal(IntPtr.Zero, ordinaryInjectedUp);
        Assert.Equal(IntPtr.Zero, taggedDown);
        Assert.False(injectedDownCreatedTakeover);
        Assert.True(fixture.Physical.HasPhysicalKeyTakeover(0x41));
        Assert.False(fixture.Executor.IsMacroKeyOwned(0x41));
        MacroPlaybackTests.WaitUntil(() => fixture.Service.GetMacroSession().Mode == MacroSessionMode.Idle);

        // Pair ownership outlives the synthetic hold and ends only at its real physical UP.
        Assert.Equal(IntPtr.Zero, fixture.Keyboard(NativeMethods.WM_KEYUP));
        Assert.False(fixture.Physical.IsPhysicalKeyDown(0x41));
        Assert.False(fixture.Physical.HasPhysicalKeyTakeover(0x41));
        Assert.Equal(IntPtr.Zero, fixture.Keyboard(NativeMethods.WM_KEYUP, injected: true, macroRelease: true));
        Assert.True(await fixture.Cleanup());
        Assert.Single(fixture.Sender.KeyReleases, item => item.Key == Key.A && item.MacroRelease);
    }

    [Theory]
    [InlineData("replacement", MouseButton.Right)]
    [InlineData("replacement", MouseButton.XButton2)]
    [InlineData("stopped", MouseButton.Right)]
    [InlineData("stopped", MouseButton.XButton2)]
    [InlineData("disposed", MouseButton.Right)]
    [InlineData("disposed", MouseButton.XButton2)]
    public async Task MouseCallback_PhysicalTakeoverDuringNativeMacroUp_SurvivesLifecycleFilter(string boundary, MouseButton button)
    {
        using var fixture = new CallbackFixture(button);
        await fixture.StartHeldInput();
        fixture.SetBoundary(boundary, mouse: true);
        var physicalDown = (IntPtr)(-1);
        var macroUp = (IntPtr)(-1);
        var ordinaryInjectedUp = (IntPtr)(-1);
        var taggedDown = (IntPtr)(-1);
        var injectedDownCreatedTakeover = true;

        fixture.Sender.MouseResult = (target, down, macroRelease) =>
        {
            if (target == button && !down && macroRelease)
            {
                fixture.Mouse(isDown: true, injected: true);
                injectedDownCreatedTakeover = fixture.Physical.HasPhysicalMouseTakeover(button);
                physicalDown = fixture.Mouse(isDown: true);
                macroUp = fixture.Mouse(isDown: false, injected: true, macroRelease: true);
                ordinaryInjectedUp = fixture.Mouse(isDown: false, injected: true);
                taggedDown = fixture.Mouse(isDown: true, injected: true, macroRelease: true);
            }
            return true;
        };

        Assert.True(await fixture.Cleanup());
        Assert.Equal(IntPtr.Zero, physicalDown);
        Assert.Equal((IntPtr)1, macroUp);
        Assert.Equal(IntPtr.Zero, ordinaryInjectedUp);
        Assert.Equal(IntPtr.Zero, taggedDown);
        Assert.False(injectedDownCreatedTakeover);
        Assert.True(fixture.Physical.HasPhysicalMouseTakeover(button));
        Assert.False(fixture.Executor.IsMacroMouseOwned(button));
        MacroPlaybackTests.WaitUntil(() => fixture.Service.GetMacroSession().Mode == MacroSessionMode.Idle);

        Assert.Equal(IntPtr.Zero, fixture.Mouse(isDown: false));
        Assert.False(fixture.Physical.IsPhysicalMouseButtonDown(button));
        Assert.False(fixture.Physical.HasPhysicalMouseTakeover(button));
        Assert.Equal(IntPtr.Zero, fixture.Mouse(isDown: false, injected: true, macroRelease: true));
        Assert.True(await fixture.Cleanup());
        Assert.Single(fixture.Sender.MouseTransitions, item => item.Button == button && !item.IsDown && item.MacroRelease);
    }

    [Theory]
    [InlineData(false, "running")]
    [InlineData(false, "replacement")]
    [InlineData(false, "stopped")]
    [InlineData(false, "disposed")]
    [InlineData(true, "running")]
    [InlineData(true, "replacement")]
    [InlineData(true, "stopped")]
    [InlineData(true, "disposed")]
    public async Task Callback_NegativeCodeDuringMacro_DoesNotReadInvalidPayload(bool mouse, string boundary)
    {
        using var fixture = new CallbackFixture(mouse ? MouseButton.Right : null);
        await fixture.StartHeldInput();
        fixture.SetBoundary(boundary, mouse);

        Assert.Equal(IntPtr.Zero, fixture.Invoke(mouse, -1,
            mouse ? NativeMethods.WM_RBUTTONDOWN : NativeMethods.WM_KEYDOWN, (IntPtr)1));
        Assert.False(fixture.Physical.HasPhysicalKeyTakeover(0x41));
        Assert.False(fixture.Physical.HasPhysicalMouseTakeover(MouseButton.Right));
    }

    [Theory]
    [InlineData(false, "replacement")]
    [InlineData(false, "stopped")]
    [InlineData(false, "disposed")]
    [InlineData(true, "replacement")]
    [InlineData(true, "stopped")]
    [InlineData(true, "disposed")]
    public void Callback_NoMacroDuringLifecycleFilter_StillSkipsInvalidPayload(bool mouse, string boundary)
    {
        using var fixture = new CallbackFixture(mouse ? MouseButton.Right : null);
        fixture.SetBoundary(boundary, mouse);

        Assert.Equal(IntPtr.Zero, fixture.Invoke(mouse, 0,
            mouse ? NativeMethods.WM_RBUTTONDOWN : NativeMethods.WM_KEYDOWN, (IntPtr)1));
        Assert.Empty(fixture.Sender.Transitions);
        Assert.Empty(fixture.Sender.MouseTransitions);
    }

    private sealed class CallbackFixture : IDisposable
    {
        private const BindingFlags PRIVATE_INSTANCE = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly MouseButton? _button;
        private readonly InputRuntimeState _runtime;
        internal RecordingInputSender Sender { get; } = new();
        internal InputHookService Service { get; }
        internal InputExecutor Executor { get; }
        internal MacroPhysicalState Physical { get; }

        internal CallbackFixture(MouseButton? button = null)
        {
            _button = button;
            Service = MacroPlaybackTests.Create(Sender, out _,
                new MacroStep { Kind = button.HasValue ? MacroStepKind.MouseDown : MacroStepKind.KeyDown,
                    Key = button.HasValue ? Key.None : Key.A, MouseButton = button },
                new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 30000 },
                new MacroStep { Kind = button.HasValue ? MacroStepKind.MouseUp : MacroStepKind.KeyUp,
                    Key = button.HasValue ? Key.None : Key.A, MouseButton = button });
            Executor = Get<InputExecutor>("_inputExecutor");
            Physical = Get<MacroPhysicalState>("_macroPhysical");
            _runtime = Get<InputRuntimeState>("_runtime");
        }

        internal async Task StartHeldInput()
        {
            MacroPlaybackTests.Press(Service, 0x75);
            MacroPlaybackTests.WaitUntil(() => _button is { } button
                ? Sender.MouseTransitions.Any(item => item.Button == button && item.IsDown)
                : Sender.Transitions.Any(item => item.Key == Key.A && item.IsDown));
            Assert.True(await Service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(_button is { } heldButton ? Executor.IsMacroMouseOwned(heldButton) : Executor.IsMacroKeyOwned(0x41));
        }

        internal void SetBoundary(string boundary, bool mouse)
        {
            if (boundary == "replacement")
                Field(mouse ? "_mouseReplacementInProgress" : "_keyboardReplacementInProgress").SetValue(Service, true);
            if (boundary == "stopped") _runtime.SetRunning(false);
            if (boundary == "disposed") Assert.True(_runtime.TryBeginDispose());
        }

        internal IntPtr Keyboard(int message, bool injected = false, bool macroRelease = false) =>
            WithPayload(new NativeMethods.KBDLLHOOKSTRUCT
            {
                vkCode = 0x41,
                scanCode = 0x1E,
                flags = (injected ? NativeMethods.KbdLlFlags.LLKHF_INJECTED : 0) |
                    (message == NativeMethods.WM_KEYUP ? NativeMethods.KbdLlFlags.LLKHF_UP : 0),
                dwExtraInfo = macroRelease ? NativeMethods.INPUT_MACRO_RELEASE : injected ? NativeMethods.INPUT_IGNORE : IntPtr.Zero
            }, pointer => Invoke(false, 0, message, pointer));

        internal IntPtr Mouse(bool isDown, bool injected = false, bool macroRelease = false)
        {
            var message = _button == MouseButton.XButton2
                ? isDown ? NativeMethods.WM_XBUTTONDOWN : NativeMethods.WM_XBUTTONUP
                : isDown ? NativeMethods.WM_RBUTTONDOWN : NativeMethods.WM_RBUTTONUP;
            return WithPayload(new NativeMethods.MSLLHOOKSTRUCT
            {
                pt = new NativeMethods.POINT { X = -100, Y = 200 },
                mouseData = _button == MouseButton.XButton2 ? 2u << 16 : 0,
                flags = injected ? NativeMethods.MouseLlFlags.LLMHF_INJECTED : 0,
                dwExtraInfo = macroRelease ? NativeMethods.INPUT_MACRO_RELEASE : injected ? NativeMethods.INPUT_IGNORE : IntPtr.Zero
            }, pointer => Invoke(true, 0, message, pointer));
        }

        internal IntPtr Invoke(bool mouse, int code, int message, IntPtr payload) =>
            (IntPtr)typeof(InputHookService).GetMethod(mouse ? "MouseCallback" : "KeyboardCallback", PRIVATE_INSTANCE)!
                .Invoke(Service, [code, (IntPtr)message, payload])!;

        internal async Task<bool> Cleanup()
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(Executor.Enqueue(new InputCommand(Key.None, false, Kind: InputCommandKind.MacroCleanup,
                Token: Service.GetMacroSession().SessionId, Completion: completion)));
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }

        public void Dispose()
        {
            // Only lifecycle flags were published above; restore them before real fixture disposal.
            Sender.KeyResult = null;
            Sender.MouseResult = null;
            Field("_keyboardReplacementInProgress").SetValue(Service, false);
            Field("_mouseReplacementInProgress").SetValue(Service, false);
            typeof(InputRuntimeState).GetField("_disposed", PRIVATE_INSTANCE)!.SetValue(_runtime, 0);
            _runtime.SetRunning(true);
            Service.Dispose();
        }

        private T Get<T>(string field) => (T)Field(field).GetValue(Service)!;
        private static FieldInfo Field(string name) => typeof(InputHookService).GetField(name, PRIVATE_INSTANCE)!;

        private static IntPtr WithPayload<T>(T value, Func<IntPtr, IntPtr> invoke) where T : struct
        {
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
            try
            {
                Marshal.StructureToPtr(value, pointer, false);
                return invoke(pointer);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }
}
