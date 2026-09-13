using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.Services.Input;

internal sealed class MacroPhysicalState : IMacroInputContext
{
    private const int DOWN = 1, SUPPRESSED = 2, TAKEOVER = 4, ACTIVATION = 8;
    private readonly int[] _keys = new int[256];
    private readonly int[] _buttons = new int[6];
    private readonly int[] _keyRevisions = new int[256];
    private int _takeovers;

    internal bool HasTakeovers => Volatile.Read(ref _takeovers) != 0;
    public bool PhysicalModifiersDown => Modifiers != ModifierKeys.None;
    // A consumed activation is held physically but never reaches the target application.
    public bool IsPhysicalKeyDown(int vk) => (uint)vk < 256 && (KeyState(vk) & (DOWN | ACTIVATION)) == DOWN;
    internal bool IsRawKeyDown(int vk) => (uint)vk < 256 && (KeyState(vk) & DOWN) != 0;
    internal int KeyState(int vk) => Volatile.Read(ref _keys[vk]);
    public bool IsPhysicalMouseButtonDown(MouseButton button) => (Volatile.Read(ref _buttons[(int)button]) & DOWN) != 0;
    public bool HasPhysicalKeyTakeover(int vk) => (uint)vk < 256 && (Volatile.Read(ref _keys[vk]) & (DOWN | TAKEOVER)) == (DOWN | TAKEOVER);
    public bool HasPhysicalMouseTakeover(MouseButton button) => (Volatile.Read(ref _buttons[(int)button]) & (DOWN | TAKEOVER)) == (DOWN | TAKEOVER);
    internal bool AnyMouseButtonDown => IsPhysicalMouseButtonDown(MouseButton.Left) || IsPhysicalMouseButtonDown(MouseButton.Right) ||
        IsPhysicalMouseButtonDown(MouseButton.Middle) || IsPhysicalMouseButtonDown(MouseButton.XButton1) || IsPhysicalMouseButtonDown(MouseButton.XButton2);
    internal int KeyRevision(int vk) => Volatile.Read(ref _keyRevisions[vk]);
    internal static bool WasDown(int state) => (state & DOWN) != 0;
    internal static bool WasSuppressed(int state) => (state & SUPPRESSED) != 0;
    internal static bool WasTakeover(int state) => (state & TAKEOVER) != 0;

    internal ModifierKeys Modifiers =>
        ((IsPhysicalKeyDown(0x11) || IsPhysicalKeyDown(0xA2) || IsPhysicalKeyDown(0xA3)) ? ModifierKeys.Control : 0) |
        ((IsPhysicalKeyDown(0x12) || IsPhysicalKeyDown(0xA4) || IsPhysicalKeyDown(0xA5)) ? ModifierKeys.Alt : 0) |
        ((IsPhysicalKeyDown(0x10) || IsPhysicalKeyDown(0xA0) || IsPhysicalKeyDown(0xA1)) ? ModifierKeys.Shift : 0) |
        ((IsPhysicalKeyDown(0x5B) || IsPhysicalKeyDown(0x5C)) ? ModifierKeys.Windows : 0);

    internal static ModifierKeys ModifierForKey(int vk) => vk switch
    {
        0x11 or 0xA2 or 0xA3 => ModifierKeys.Control,
        0x12 or 0xA4 or 0xA5 => ModifierKeys.Alt,
        0x10 or 0xA0 or 0xA1 => ModifierKeys.Shift,
        0x5B or 0x5C => ModifierKeys.Windows,
        _ => ModifierKeys.None
    };

    internal int ObserveKey(int vk, bool down, bool activation = false)
    {
        var previous = Volatile.Read(ref _keys[vk]);
        Volatile.Write(ref _keys[vk], down ? previous | DOWN | (activation ? ACTIVATION : 0) : 0);
        if (!down && WasTakeover(previous)) Interlocked.Decrement(ref _takeovers);
        if (down && !WasDown(previous)) Interlocked.Increment(ref _keyRevisions[vk]);
        return previous;
    }

    internal int ObserveButton(MouseButton button, bool down)
    {
        var index = (int)button;
        var previous = Volatile.Read(ref _buttons[index]);
        Volatile.Write(ref _buttons[index], down ? previous | DOWN : 0);
        if (!down && WasTakeover(previous)) Interlocked.Decrement(ref _takeovers);
        return previous;
    }

    internal void CompleteKey(int vk, bool down, bool suppressed)
    {
        if (down && suppressed) Interlocked.Or(ref _keys[vk], SUPPRESSED);
    }

    internal void CompleteButton(MouseButton button, bool down, bool suppressed)
    {
        if (down && suppressed) Interlocked.Or(ref _buttons[(int)button], SUPPRESSED);
    }

    internal void TakeKey(int vk)
    {
        if (!WasTakeover(Interlocked.Or(ref _keys[vk], TAKEOVER))) Interlocked.Increment(ref _takeovers);
    }
    internal void TakeButton(MouseButton button)
    {
        if (!WasTakeover(Interlocked.Or(ref _buttons[(int)button], TAKEOVER))) Interlocked.Increment(ref _takeovers);
    }

    internal void Seed(Func<int, bool> unknownState)
    {
        Volatile.Write(ref _takeovers, 0);
        for (var vk = 0; vk < _keys.Length; vk++)
            Volatile.Write(ref _keys[vk], vk is not (0x10 or 0x11 or 0x12) && unknownState(vk) ? DOWN : 0);
        int[] mouseKeys = [0, 1, 2, 4, 5, 6];
        for (var i = 1; i < _buttons.Length; i++) Volatile.Write(ref _buttons[i], unknownState(mouseKeys[i]) ? DOWN : 0);
    }

    internal (bool[] Keys, bool[] Buttons) CaptureHeld()
    {
        var keys = new bool[256];
        var buttons = new bool[6];
        for (var i = 0; i < keys.Length; i++) keys[i] = IsRawKeyDown(i);
        for (var i = 1; i < buttons.Length; i++) buttons[i] = IsPhysicalMouseButtonDown((MouseButton)i);
        return (keys, buttons);
    }
}

/// <summary>One worker owns playback, waits and retirement. Hooks only publish fixed-state requests.</summary>
internal sealed class MacroStateMachine : IInputCommandGuard, IDisposable
{
    internal const int EMERGENCY_STOP_VK = 0x7B;
    private sealed record Entry(Profile Owner, MacroDefinition Definition, int VirtualKey);
    private sealed record Status(MacroSessionSnapshot Value);
    private sealed record RecordingRequest(Profile Owner, Guid MacroId, int AvailableRows, TaskCompletionSource<MacroRecordingResult> Completion);

    private readonly InputRuntimeState _runtime;
    private readonly InputExecutor _executor;
    private readonly MacroPhysicalState _physical;
    private readonly AutoRunStateMachine _autoRun;
    private readonly ILoggerService _logger;
    private readonly Action _prepareRecording;
    private readonly Func<Action, Task> _onHookThread;
    private readonly Func<(int X, int Y)> _cursor;
    private readonly Func<Rectangle[]> _monitors;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private readonly bool[] _activationPairs = new bool[256];
    private readonly Key[] _intendedModifiers = new Key[256];
    private readonly int[] _modifierRevisions = new int[256];
    private Entry[] _lookup = [];
    private Entry? _pending;
    private RecordingRequest? _recordRequest;
    private MacroRecorder? _recorder;
    private Status _status = new(new MacroSessionSnapshot(0, null, Guid.Empty, MacroSessionMode.Idle, TimeSpan.Zero, 0));
    private ForegroundIdentitySnapshot? _foreground;
    private long _sessionId;
    private long _startedAt;
    private int _busy;
    private int _requestReady;
    private int _cancelled;
    private int _recordStopRequested;
    private int _disposed;
    private int _closing;
    private int _moving;
    private int _verifyClickPosition;
    private int _clickX, _clickY;
    private Rectangle[]? _movementMonitors;
    private string? _failure;

    internal MacroStateMachine(InputRuntimeState runtime, InputExecutor executor, MacroPhysicalState physical,
        AutoRunStateMachine autoRun, ILoggerService logger, Action prepareRecording, Func<Action, Task> onHookThread,
        Func<(int X, int Y)>? cursor = null, Func<Rectangle[]>? monitors = null)
    {
        _runtime = runtime;
        _executor = executor;
        _physical = physical;
        _autoRun = autoRun;
        _logger = logger;
        _prepareRecording = prepareRecording;
        _onHookThread = onHookThread;
        _cursor = cursor ?? ReadCursor;
        _monitors = monitors ?? WindowsInputSender.GetPhysicalMonitorBounds;
        _worker = new Thread(Work) { IsBackground = true, Name = "Macro playback" };
        _worker.Start();
    }

    internal event EventHandler? Changed;
    internal bool IsBusy => Volatile.Read(ref _busy) != 0;
    internal bool IsMoving => Volatile.Read(ref _moving) != 0;
    internal bool IsRecording => Volatile.Read(ref _recordRequest) is not null;

    internal MacroSessionSnapshot GetSession()
    {
        var value = Volatile.Read(ref _status).Value;
        return IsBusy && value.Mode is not MacroSessionMode.Faulted
            ? value with { Elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref _startedAt)) }
            : value;
    }

    internal void Rebuild(Profile? profile, Profile? windows, int colorVk, int crosshairVk, int rapidFireVk)
    {
        var entries = new List<Entry>();
        if (profile is { IsEnabled: true, IsWindowsProfile: false } && profile.Macros.IsEnabled && !profile.IsPersistenceSuspended &&
            MacroValidation.GetFormatError(profile.Macros) is null)
        {
            foreach (var definition in profile.Macros.Definitions)
            {
                if (definition.IsEnabled && MacroValidation.GetPlaybackError(definition) is null &&
                    GetShortcutError(profile, definition.Id, windows, colorVk, crosshairVk, rapidFireVk) is null)
                    entries.Add(new Entry(profile, definition with { Steps = (MacroStep[])definition.Steps.Clone() },
                        KeyInteropUtilities.ToVirtualKey(definition.ShortcutKey)));
            }
        }
        Volatile.Write(ref _lookup, entries.ToArray());
        RaiseChanged();
    }

    internal void Invalidate(Profile owner)
    {
        if (ReferenceEquals(owner, _runtime.ActiveProfile)) Volatile.Write(ref _lookup, []);
        Cancel("Macro settings changed.", owner, playbackOnly: true);
    }

    internal static string? GetShortcutError(Profile profile, Guid id, Profile? windows, int colorVk, int crosshairVk, int rapidFireVk)
    {
        var definition = Array.Find(profile.Macros.Definitions, m => m.Id == id);
        if (definition is null || definition.ShortcutKey == Key.None) return null;
        var vk = KeyInteropUtilities.ToVirtualKey(definition.ShortcutKey);
        var mods = definition.ShortcutModifiers;
        if (vk == EMERGENCY_STOP_VK) return "F12 is reserved for emergency cancellation.";
        if (vk == colorVk || vk == crosshairVk || vk == rapidFireVk) return "This key is assigned to an app-level toggle.";
        if (profile.Macros.Definitions.Any(m => m.Id != id && m.IsEnabled && m.ShortcutKey == definition.ShortcutKey && m.ShortcutModifiers == mods))
            return "Another enabled macro uses this shortcut.";
        if ((mods & ModifierKeys.Windows) != 0 && windows is { IsEnabled: true } && windows.WindowsLauncher.IsEnabled &&
            windows.WindowsLauncher.Launchers.TryGetValue(definition.ShortcutKey, out var launcher) && !string.IsNullOrWhiteSpace(launcher.Path))
            return "Windows Launcher uses this shortcut.";
        if ((mods & ModifierKeys.Alt) != 0 && profile.AltKeyboard.IsEnabled &&
            profile.AltKeyboard.Bindings.TryGetValue(definition.ShortcutKey, out var binding) && (binding.TapKey.HasValue || binding.HoldKey.HasValue))
            return "Alt + Keyboard uses this shortcut.";
        if (profile.CombinedMappings.IsEnabled && profile.CombinedMappings.Mappings.Any(m =>
            m.Source.Kind == InputTriggerKind.KeyboardKey && m.Source.Key == definition.ShortcutKey))
            return "Key Mapping uses this key.";
        if (definition.ShortcutKey == Key.CapsLock && profile.CapsLock.IsEnabled &&
            (profile.CapsLock.Mode != CapsLockMode.Normal || profile.CapsLock.IsRemapEnabled))
            return "Caps Lock behavior uses this key.";
        if (profile.AutoRun.IsEnabled && profile.AutoRun.TriggerKey == definition.ShortcutKey &&
            (profile.AutoRun.TriggerModifier == ModifierKeys.None || (mods & profile.AutoRun.TriggerModifier) != 0))
            return "Auto-Run uses this shortcut.";
        if (profile.RightClickHoldBreath.IsEnabled && profile.RightClickHoldBreath.SuppressEarlyCancelInput &&
            profile.RightClickHoldBreath.PanicTrigger == InputTrigger.FromKey(definition.ShortcutKey))
            return "Hold-Breath Early Cancel uses this key.";
        return null;
    }

    // null continues ordinary feature dispatch; true suppresses a pair; false gives a physical takeover priority.
    internal bool? HandleKey(int vk, bool down, int previous, bool allowActivation = true)
    {
        if (_activationPairs[vk])
        {
            _physical.ObserveKey(vk, down, activation: true);
            if (!down) _activationPairs[vk] = false;
            _wake.Set();
            return true;
        }
        if (vk == EMERGENCY_STOP_VK && IsBusy && down && !MacroPhysicalState.WasDown(previous))
        {
            _physical.ObserveKey(vk, down, activation: true);
            _activationPairs[vk] = true;
            Cancel("Stopped with F12.");
            return true;
        }
        if (allowActivation && Volatile.Read(ref _closing) == 0 && !IsRecording && down && !MacroPhysicalState.WasDown(previous) &&
            _runtime.AdvancedModeEnabled && _runtime.ProfileInputGenerationIsCurrent())
        {
            var mods = _physical.Modifiers & ~MacroPhysicalState.ModifierForKey(vk);
            var lookup = Volatile.Read(ref _lookup);
            foreach (var entry in lookup)
            {
                if (entry.VirtualKey != vk || entry.Definition.ShortcutModifiers != mods || !ReferenceEquals(entry.Owner, _runtime.ActiveProfile)) continue;
                _physical.ObserveKey(vk, down, activation: true);
                _activationPairs[vk] = true;
                if (Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
                {
                    Volatile.Write(ref _cancelled, 0);
                    _failure = null;
                    _foreground = _runtime.ForegroundIdentity;
                    _pending = entry;
                    Interlocked.Increment(ref _sessionId);
                    Volatile.Write(ref _startedAt, Stopwatch.GetTimestamp());
                    if (!ReferenceEquals(lookup, Volatile.Read(ref _lookup))) Volatile.Write(ref _cancelled, 1);
                    Volatile.Write(ref _requestReady, 1);
                    _wake.Set();
                }
                return true;
            }
        }
        _physical.ObserveKey(vk, down);
        if (_physical.HasPhysicalKeyTakeover(vk) || MacroPhysicalState.WasTakeover(previous)) return false;
        if (down && !MacroPhysicalState.WasSuppressed(previous) && _executor.IsMacroKeyOwned(vk))
        {
            _physical.TakeKey(vk);
            if (MacroPhysicalState.ModifierForKey(vk) == ModifierKeys.None) Cancel("Physical input took over a macro key.");
            _wake.Set();
            return false;
        }
        if (IsBusy && (MacroPhysicalState.ModifierForKey(vk) != ModifierKeys.None || !down)) _wake.Set();
        return null;
    }

    internal bool? HandleButton(MouseButton button, bool down, int previous)
    {
        if (_physical.HasPhysicalMouseTakeover(button) || MacroPhysicalState.WasTakeover(previous)) return false;
        if (down && !MacroPhysicalState.WasSuppressed(previous) && _executor.IsMacroMouseOwned(button))
        {
            _physical.TakeButton(button);
            Cancel("Physical input took over a macro mouse button.");
            return false;
        }
        return null;
    }

    internal void Capture(in RecordedMacroEvent input) => Volatile.Read(ref _recorder)?.Capture(input);
    internal void StopRecording()
    {
        Volatile.Write(ref _recordStopRequested, 1);
        Volatile.Read(ref _recorder)?.RequestStop(MacroRecordingEndReason.Stopped);
        _wake.Set();
    }
    internal void StopFromControl(Key? key)
    {
        Volatile.Read(ref _recorder)?.StopFromControl(key.HasValue ? Interop.NativeMethods.WM_KEYDOWN : Interop.NativeMethods.WM_LBUTTONDOWN,
            key.HasValue ? KeyInteropUtilities.ToVirtualKey(key.Value) : 0);
        _wake.Set();
    }

    internal void StopBeforeExecutorClose()
    {
        Volatile.Write(ref _closing, 1);
        Cancel("Input service stopped.");
        if (IsBusy) _executor.Enqueue(new InputCommand(Key.None, false, Kind: InputCommandKind.MacroCleanup, Token: _sessionId));
    }

    internal async Task<bool> RetireAsync()
    {
        Volatile.Write(ref _closing, 1);
        Cancel("Input service is closing.");
        var started = Stopwatch.GetTimestamp();
        while (IsBusy)
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds >= 2)
            {
                Volatile.Write(ref _closing, 0);
                return false;
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
        return true;
    }

    internal void ResumeAdmission() => Volatile.Write(ref _closing, 0);

    internal Task<MacroRecordingResult> RecordAsync(Profile owner, Guid macroId, int availableRows, CancellationToken cancellationToken)
    {
        var definition = Array.Find(owner.Macros.Definitions, macro => macro.Id == macroId);
        if (owner.IsWindowsProfile || !owner.IsEnabled || owner.IsPersistenceSuspended || definition is null ||
            availableRows <= 0 || definition.Steps.Length >= MacroValidation.MaxSteps ||
            Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _closing) != 0)
            return Task.FromResult(new MacroRecordingResult(0, owner, macroId, [], MacroRecordingEndReason.Faulted, false,
                "Select an editable application macro with available step capacity."));
        availableRows = Math.Min(availableRows, MacroValidation.MaxSteps - definition.Steps.Length);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return Task.FromResult(new MacroRecordingResult(0, owner, macroId, [], MacroRecordingEndReason.Faulted, false, "A macro session is already active."));
        var completion = new TaskCompletionSource<MacroRecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _recordStopRequested, 0);
        _recordRequest = new RecordingRequest(owner, macroId, availableRows, completion);
        _pending = null;
        _failure = null;
        Volatile.Write(ref _cancelled, cancellationToken.IsCancellationRequested ? 1 : 0);
        var sessionId = Interlocked.Increment(ref _sessionId);
        Volatile.Write(ref _startedAt, Stopwatch.GetTimestamp());
        Volatile.Write(ref _requestReady, 1);
        _wake.Set();
        return AwaitRecordingAsync(completion.Task, sessionId, cancellationToken);
    }

    private async Task<MacroRecordingResult> AwaitRecordingAsync(Task<MacroRecordingResult> result, long sessionId, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() =>
        {
            if (Volatile.Read(ref _sessionId) == sessionId) Cancel("Recording was cancelled.");
        });
        return await result.ConfigureAwait(false);
    }

    internal void Cancel(string reason, Profile? owner = null, bool playbackOnly = false)
    {
        if (!IsBusy || (playbackOnly && IsRecording) ||
            (owner is not null && !ReferenceEquals(owner, _pending?.Owner) && !ReferenceEquals(owner, _recordRequest?.Owner))) return;
        _failure = reason;
        Volatile.Write(ref _cancelled, 1);
        Volatile.Read(ref _recorder)?.RequestStop(MacroRecordingEndReason.Interrupted);
        _wake.Set();
    }

    public bool CanExecute(in InputCommand command)
    {
        if (!Current() || (command.Token != 0 && command.Token != Volatile.Read(ref _sessionId))) return false;
        if (_recordRequest is not null) return _runtime.IsRunning && !_runtime.IsDisposed;
        var entry = _pending;
        if (entry is null || !_runtime.AdvancedModeEnabled || !entry.Owner.IsEnabled || !entry.Owner.Macros.IsEnabled ||
            !ReferenceEquals(_foreground, _runtime.ForegroundIdentity) || _foreground is null ||
            !_runtime.LiveForegroundMatches(entry.Owner, _foreground.Generation)) return false;
        var expectedMonitors = Volatile.Read(ref _movementMonitors);
        if (expectedMonitors is not null && !expectedMonitors.SequenceEqual(_monitors())) return false;
        if (Volatile.Read(ref _verifyClickPosition) != 0)
        {
            var point = _cursor();
            if (point.X != _clickX || point.Y != _clickY) return false;
        }
        return Current() && (command.Token == 0 || command.Token == Volatile.Read(ref _sessionId)) &&
            ReferenceEquals(_foreground, _runtime.ForegroundIdentity);
    }

    private bool Current() => Volatile.Read(ref _cancelled) == 0 && Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _closing) == 0;

    private void Work()
    {
        while (true)
        {
            _wake.WaitOne(GetSession().Mode == MacroSessionMode.Faulted ? 50 : Timeout.Infinite);
            if (Volatile.Read(ref _disposed) != 0 && (!IsBusy || GetSession().Mode == MacroSessionMode.Faulted)) break;
            if (GetSession().Mode == MacroSessionMode.Faulted)
            {
                if (!_executor.IsMacroSessionReserved(_sessionId))
                {
                    _autoRun.EndMacroReservation();
                    _runtime.EndMacroInhibition();
                    Volatile.Write(ref _requestReady, 0);
                    Volatile.Write(ref _busy, 0);
                    Publish(MacroSessionMode.Idle);
                }
                continue;
            }
            if (!IsBusy || Volatile.Read(ref _requestReady) == 0) continue;
            ExecuteSession();
            if (Volatile.Read(ref _disposed) != 0) break;
        }
    }

    private void ExecuteSession()
    {
        var recording = _recordRequest;
        var owner = recording?.Owner ?? _pending!.Owner;
        var macroId = recording?.MacroId ?? _pending!.Definition.Id;
        var reserved = false;
        var executorReserved = false;
        var cleaned = true;
        MacroRecordingResult? recordingResult = null;
        try
        {
            Publish(recording is null ? MacroSessionMode.PreparingPlayback : MacroSessionMode.PreparingRecording);
            if (!_runtime.IsRunning || _runtime.IsDisposed || !Current()) throw new InvalidOperationException("Input service is unavailable.");
            _runtime.BeginMacroInhibition(recording is not null);
            if (recording is not null) _prepareRecording();
            var deadline = Stopwatch.GetTimestamp();
            do
            {
                reserved = _autoRun.TryReserveForMacro();
                if (reserved) break;
                if (recording is null || Stopwatch.GetElapsedTime(deadline).TotalSeconds >= 2)
                    throw new InvalidOperationException("Auto-Run is active or still releasing input.");
                Wait(10);
            } while (Current());
            while (!_runtime.AutomationOutputDrained || !_autoRun.MacroAdmissionDrained)
            {
                if (Stopwatch.GetElapsedTime(deadline).TotalSeconds >= 2) throw new InvalidOperationException("Existing automation did not finish releasing input.");
                Wait(10);
            }
            // Even a timed-out admission may still be queued; its token must retire before reuse.
            executorReserved = true;
            var preparationBudget = Math.Max(1, 2000 - (int)Stopwatch.GetElapsedTime(deadline).TotalMilliseconds);
            if (!SendRaw(new InputCommand(Key.None, false, Kind: InputCommandKind.MacroReserveModifiers,
                Guard: this, Token: _sessionId, RequireAllReleased: recording is not null), preparationBudget))
                throw new InvalidOperationException("Another input action still owns a modifier or could not finish releasing its input.");
            if (recording is not null)
            {
                var recorder = new MacroRecorder(recording.AvailableRows, Stopwatch.Frequency);
                var begin = _onHookThread(() =>
                {
                    if (!Current() || Volatile.Read(ref _recordStopRequested) != 0) return;
                    var held = _physical.CaptureHeld();
                    recorder.Begin(Stopwatch.GetTimestamp(), held.Keys, held.Buttons);
                    Volatile.Write(ref _recorder, recorder);
                    if (!Current() || Volatile.Read(ref _recordStopRequested) != 0) recorder.RequestStop(MacroRecordingEndReason.Interrupted);
                });
                if (!begin.Wait(Math.Max(1, 2000 - (int)Stopwatch.GetElapsedTime(deadline).TotalMilliseconds)))
                    throw new InvalidOperationException("The input dispatcher could not start recording.");
                begin.GetAwaiter().GetResult();
                Publish(MacroSessionMode.Recording);
                var lastCount = -1;
                while (recorder.IsCapturing)
                {
                    if (!Current()) recorder.RequestStop(MacroRecordingEndReason.Interrupted);
                    recorder.CheckDuration(Stopwatch.GetTimestamp());
                    if (lastCount != recorder.Count) { lastCount = recorder.Count; Publish(MacroSessionMode.Recording, lastCount); }
                    _wake.WaitOne(50);
                }
                var (steps, balanced) = recorder.BuildSteps();
                recordingResult = new MacroRecordingResult(_sessionId, owner, macroId, steps, recorder.EndReason, balanced, _failure);
            }
            else
            {
                Publish(MacroSessionMode.WaitingForShortcutRelease);
                while (_physical.IsRawKeyDown(_pending!.VirtualKey) || _physical.PhysicalModifiersDown) Wait(10);
                Array.Clear(_intendedModifiers);
                var steps = _pending.Definition.Steps;
                for (var i = 0; i < steps.Length; i++)
                {
                    if (!CanExecute(default)) throw new OperationCanceledException();
                    Publish(MacroSessionMode.Playing, i + 1);
                    Play(steps[i]);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _failure ??= "Macro cancelled because its input context changed.";
        }
        catch (Exception exception)
        {
            _failure = exception.Message;
            if (_logger.IsEnabled) _logger.Log($"[Macros] {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _cancelled, 1);
            Volatile.Write(ref _moving, 0);
            Volatile.Write(ref _movementMonitors, null);
            Volatile.Write(ref _verifyClickPosition, 0);
            Publish(MacroSessionMode.Finishing);
            // Failed preparation has no captured rows. Report it promptly even when a prior native
            // call is stuck; the worker keeps the slot and queued cleanup until accounting finishes.
            if (recording is not null && recordingResult is null)
                recording.Completion.TrySetResult(new MacroRecordingResult(_sessionId, owner, macroId, [],
                    MacroRecordingEndReason.Faulted, false, _failure));
            if (executorReserved)
                cleaned = SendRaw(new InputCommand(Key.None, false, Kind: InputCommandKind.MacroCleanup, Token: _sessionId)) ||
                    !_executor.IsMacroSessionReserved(_sessionId);
            if (!cleaned) _failure = "A macro input could not be released. Playback is blocked until input service recovery.";
            if (cleaned)
            {
                if (reserved) _autoRun.EndMacroReservation();
                _runtime.EndMacroInhibition();
            }
            Volatile.Write(ref _recorder, null);
            _recordRequest = null;
            Publish(cleaned ? MacroSessionMode.Idle : MacroSessionMode.Faulted);
            if (cleaned)
            {
                Volatile.Write(ref _requestReady, 0);
                Volatile.Write(ref _busy, 0);
            }
            recording?.Completion.TrySetResult(recordingResult ?? new MacroRecordingResult(_sessionId, owner, macroId, [],
                MacroRecordingEndReason.Faulted, false, _failure));
            RaiseChanged();
        }
    }

    private void Play(MacroStep step)
    {
        switch (step.Kind)
        {
            case MacroStepKind.Wait: Wait(step.DurationMs); break;
            case MacroStepKind.KeyPress:
                SendKey(step.Key, true); Wait(step.DurationMs == 0 ? Random.Shared.Next(GestureChordStateMachine.KEY_PRESS_MIN_MS,
                    GestureChordStateMachine.KEY_PRESS_MAX_MS + 1) : step.DurationMs); SendKey(step.Key, false); break;
            case MacroStepKind.KeyDown: SendKey(step.Key, true); break;
            case MacroStepKind.KeyUp: SendKey(step.Key, false); break;
            case MacroStepKind.MoveTo: MoveTo(step.X, step.Y); break;
            case MacroStepKind.MouseClick:
                MoveTo(step.X, step.Y);
                _clickX = step.X; _clickY = step.Y;
                Volatile.Write(ref _verifyClickPosition, 1);
                try { SendButton(step.MouseButton!.Value, true); }
                finally { Volatile.Write(ref _verifyClickPosition, 0); }
                Wait(step.DurationMs == 0 ? Random.Shared.Next(RapidFireStateMachine.HOLD_MIN_MS,
                    RapidFireStateMachine.HOLD_MAX_MS + 1) : step.DurationMs);
                SendButton(step.MouseButton!.Value, false); break;
            case MacroStepKind.MouseDown: SendButton(step.MouseButton!.Value, true); break;
            case MacroStepKind.MouseUp: SendButton(step.MouseButton!.Value, false); break;
            case MacroStepKind.MouseWheel:
                Send(new InputCommand(Key.None, false, Kind: InputCommandKind.MouseWheel, WheelDelta: step.WheelDelta, HorizontalWheel: step.HorizontalWheel)); break;
        }
    }

    private void SendKey(Key key, bool down)
    {
        var vk = KeyInteropUtilities.ToVirtualKey(key);
        var revision = _physical.KeyRevision(vk);
        if (!down) _intendedModifiers[vk] = Key.None;
        Send(new InputCommand(key, down), newOutput: down);
        if (down && MacroPhysicalState.ModifierForKey(vk) != ModifierKeys.None)
        {
            _intendedModifiers[vk] = key;
            // Delivery can pump a physical takeover before returning. Only acknowledge the
            // revision from before that attempt, so its eventual physical UP requires restoration.
            _modifierRevisions[vk] = revision;
        }
    }

    private void SendButton(MouseButton button, bool down) =>
        Send(new InputCommand(Key.None, down, Kind: InputCommandKind.MouseButtonTransition, Button: button), down);

    private void Send(InputCommand command, bool newOutput = true, bool restoreModifiers = true)
    {
        while (true)
        {
            if (newOutput)
            {
                while (_physical.PhysicalModifiersDown) { Publish(MacroSessionMode.WaitingForPhysicalModifiers); Wait(10); }
                if (!CanExecute(default)) throw new OperationCanceledException();
                if (restoreModifiers)
                {
                    for (var vk = 0; vk < _intendedModifiers.Length; vk++)
                    {
                        var key = _intendedModifiers[vk];
                        if (key == Key.None) continue;
                        while (_modifierRevisions[vk] != _physical.KeyRevision(vk))
                        {
                            var revision = _physical.KeyRevision(vk);
                            Send(new InputCommand(key, true), restoreModifiers: false);
                            _modifierRevisions[vk] = revision;
                        }
                    }
                }
            }
            var acknowledgement = new InputCommandAcknowledgement();
            var sent = SendRaw(command with
            {
                HoldOwner = InputHoldOwner.Macro, Token = _sessionId, Generation = _sessionId,
                Guard = this, Acknowledgement = acknowledgement,
                ExpectedProfile = _pending?.Owner, ForegroundGeneration = _foreground?.Generation ?? 0
            });
            if (sent) return;
            if (newOutput && acknowledgement.DeferredForPhysicalModifiers && Current()) continue;
            throw new InvalidOperationException("Macro input was refused or could not be inserted.");
        }
    }

    private bool SendRaw(InputCommand command, int preparationTimeoutMs = 0)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_executor.Enqueue(command with { Completion = completion })) return false;
        if (preparationTimeoutMs > 0)
        {
            var started = Stopwatch.GetTimestamp();
            while (!completion.Task.Wait(10))
                if (!Current() || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= preparationTimeoutMs) return false;
        }
        else if (command.Kind == InputCommandKind.MacroCleanup && !completion.Task.Wait(2000))
        {
            _failure = "Input cleanup is still waiting for a native call. New macros are blocked.";
            Publish(MacroSessionMode.Faulted);
        }
        // This is the dedicated macro worker, never the hook/dispatcher or shared executor. A DOWN
        // already in native delivery must finish accounting before this worker can order cleanup.
        return completion.Task.GetAwaiter().GetResult();
    }

    private void Wait(int milliseconds)
    {
        var start = Stopwatch.GetTimestamp();
        do
        {
            if (!Current()) throw new OperationCanceledException();
            var remaining = milliseconds - Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (remaining <= 0) return;
            _wake.WaitOne((int)Math.Ceiling(remaining));
        } while (true);
    }

    private void MoveTo(int x, int y)
    {
        if (_physical.AnyMouseButtonDown) throw new InvalidOperationException("Release physical mouse buttons before moving the cursor.");
        var start = _cursor();
        var monitors = _monitors();
        var points = MacroCursorPath.Create(start.X, start.Y, x, y, monitors, Random.Shared.NextDouble() * 2 - 1);
        Volatile.Write(ref _movementMonitors, monitors);
        Volatile.Write(ref _moving, 1);
        try
        {
            var previous = new Point(start.X, start.Y);
            foreach (var point in points)
            {
                if (_physical.AnyMouseButtonDown || !monitors.SequenceEqual(_monitors()))
                    throw new InvalidOperationException("Physical mouse input or display geometry changed during movement.");
                Wait((int)Math.Ceiling(Math.Max(8, MacroCursorPath.Distance(previous, point) / 3)));
                Send(new InputCommand(Key.None, false, Kind: InputCommandKind.MoveTo, X: point.X, Y: point.Y));
                var settleStart = Stopwatch.GetTimestamp();
                while (_cursor() != (point.X, point.Y))
                {
                    if (Stopwatch.GetElapsedTime(settleStart).TotalMilliseconds >= 50)
                        throw new InvalidOperationException("The cursor was clipped or moved away from the requested position.");
                    Wait(1);
                }
                previous = point;
            }
        }
        finally { Volatile.Write(ref _moving, 0); Volatile.Write(ref _movementMonitors, null); }
    }

    private static (int X, int Y) ReadCursor() => WindowsInputSender.TryGetPhysicalCursorPosition(out var x, out var y)
        ? (x, y) : throw new InvalidOperationException("The physical cursor position is unavailable.");

    private void Publish(MacroSessionMode mode, int row = 0)
    {
        var value = new MacroSessionSnapshot(_sessionId, _recordRequest?.Owner ?? _pending?.Owner,
            _recordRequest?.MacroId ?? _pending?.Definition.Id ?? Guid.Empty, mode,
            Stopwatch.GetElapsedTime(_startedAt), row, _failure);
        Volatile.Write(ref _status, new Status(value));
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception exception) { if (_logger.IsEnabled) _logger.Log($"[Macros] Status notification failed: {exception.Message}"); }
    }

    public void Dispose()
    {
        Cancel("Input service stopped.");
        Volatile.Write(ref _disposed, 1);
        _wake.Set();
        if (Thread.CurrentThread != _worker) _worker.Join(300);
        // A blocked native call retains its worker and signal until its release accounting completes.
    }
}

internal static class MacroCursorPath
{
    internal static double Distance(Point from, Point to) => Math.Sqrt(Math.Pow((double)to.X - from.X, 2) + Math.Pow((double)to.Y - from.Y, 2));

    internal static Point[] Create(int fromX, int fromY, int toX, int toY, Rectangle[] monitors, double bendFactor)
    {
        var start = new Point(fromX, fromY);
        var end = new Point(toX, toY);
        if (!monitors.Any(r => r.Contains(start)) || !monitors.Any(r => r.Contains(end)))
            throw new InvalidOperationException("The cursor endpoint is outside the current monitors.");
        var length = Distance(start, end);
        if (length == 0) return [];
        if (length > Math.Sqrt(2) * 65536)
            throw new InvalidOperationException("The desktop is too large for exact absolute cursor input.");
        var bend = length < 4 ? 0 : Math.Clamp(bendFactor, -1, 1) * Math.Min(8, length * .01);
        for (var attempt = 0; attempt < 2; attempt++, bend = 0)
        {
            var count = (int)Math.Ceiling(Math.Sqrt(length * length + 16 * bend * bend) / 24);
            var result = new Point[count];
            var valid = true;
            var previous = start;
            for (var i = 1; i <= count; i++)
            {
                var t = (double)i / count;
                var offset = 4 * bend * t * (1 - t);
                var point = i == count ? end : new Point(
                    (int)Math.Round(fromX + t * ((double)toX - fromX) - ((double)toY - fromY) / length * offset),
                    (int)Math.Round(fromY + t * ((double)toY - fromY) + ((double)toX - fromX) / length * offset));
                // Check the short segment's pixels too: endpoints alone can jump a narrow monitor gap.
                var pixels = Math.Max(1, (int)Math.Ceiling(Distance(previous, point)));
                for (var pixel = 1; pixel <= pixels; pixel++)
                {
                    var probe = new Point((int)Math.Round(previous.X + (point.X - previous.X) * (double)pixel / pixels),
                        (int)Math.Round(previous.Y + (point.Y - previous.Y) * (double)pixel / pixels));
                    if (!monitors.Any(r => r.Contains(probe))) { valid = false; break; }
                }
                if (!valid) break;
                result[i - 1] = point;
                previous = point;
            }
            if (valid) return result;
        }
        throw new InvalidOperationException("The cursor path crosses a gap between monitors.");
    }
}
