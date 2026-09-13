using System.Runtime.CompilerServices;
using System.Threading;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services.Input;

internal sealed record ForegroundIdentitySnapshot(
    IntPtr WindowHandle,
    uint ProcessId,
    string? Executable,
    long Generation);

/// <summary>
/// Lock-free runtime publications shared by input state machines.
/// Active-profile writes are serialized by InputHookService; foreground writes are serialized by
/// ProfileActivationService and intentionally take no service or feature lock.
/// </summary>
internal sealed class InputRuntimeState
{
    private readonly IAutoRunTransport _foregroundTransport;
    private int _disposed;
    private volatile bool _isRunning;
    private volatile bool _advancedModeEnabled;
    private volatile Profile? _activeProfile;
    private volatile ForegroundIdentitySnapshot? _foregroundIdentity;
    private long _activeProfileGeneration;
    private long _publishedForegroundGeneration;
    private int _macroInhibition;
    private int _automationOutputCount;
    private volatile bool _recordingPaused;

    internal InputRuntimeState(IAutoRunTransport? foregroundTransport = null)
    {
        _foregroundTransport = foregroundTransport ?? new NativeAutoRunTransport();
    }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal bool IsRunning => _isRunning;

    internal bool AdvancedModeEnabled => _advancedModeEnabled;

    internal bool AutomationInhibited => Volatile.Read(ref _macroInhibition) != 0;
    internal bool RecordingPaused => _recordingPaused;
    internal bool AutomationOutputDrained => Volatile.Read(ref _automationOutputCount) == 0;

    internal void BeginMacroInhibition(bool recording)
    {
        Volatile.Write(ref _macroInhibition, 1);
        _recordingPaused = recording;
    }

    internal void EndMacroInhibition()
    {
        _recordingPaused = false;
        Volatile.Write(ref _macroInhibition, 0);
    }

    internal bool TryBeginAutomationOutput()
    {
        if (AutomationInhibited) return false;
        Interlocked.Increment(ref _automationOutputCount);
        if (!AutomationInhibited) return true;
        Interlocked.Decrement(ref _automationOutputCount);
        return false;
    }

    internal void EndAutomationOutput() => Interlocked.Decrement(ref _automationOutputCount);

    internal Profile? ActiveProfile => _activeProfile;

    internal long ActiveProfileGeneration => Volatile.Read(ref _activeProfileGeneration);

    internal long PublishedForegroundGeneration => Volatile.Read(ref _publishedForegroundGeneration);

    internal ForegroundIdentitySnapshot? ForegroundIdentity => _foregroundIdentity;

    internal bool TryBeginDispose() => Interlocked.CompareExchange(ref _disposed, 1, 0) == 0;

    internal void SetRunning(bool value) => _isRunning = value;

    internal void SetAdvancedMode(bool value) => _advancedModeEnabled = value;

    internal void SetActiveProfile(Profile? profile, long foregroundGeneration)
    {
        _activeProfile = profile;
        Volatile.Write(ref _activeProfileGeneration, foregroundGeneration);
    }

    internal void SetActiveProfileReference(Profile? profile) => _activeProfile = profile;

    internal void SetActiveProfileGeneration(long foregroundGeneration) =>
        Volatile.Write(ref _activeProfileGeneration, foregroundGeneration);

    internal void SetForegroundIdentity(
        IntPtr windowHandle,
        uint processId,
        string? normalizedExecutable,
        long foregroundGeneration)
    {
        _foregroundIdentity = new ForegroundIdentitySnapshot(
            windowHandle,
            processId,
            normalizedExecutable,
            foregroundGeneration);
        Volatile.Write(ref _publishedForegroundGeneration, foregroundGeneration);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ProfileInputGenerationIsCurrent() =>
        ActiveProfileGeneration == PublishedForegroundGeneration;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ProfileInputGenerationIsCurrent(Profile profile, long foregroundGeneration) =>
        !IsDisposed &&
        IsRunning &&
        foregroundGeneration == ActiveProfileGeneration &&
        foregroundGeneration == PublishedForegroundGeneration &&
        ReferenceEquals(ActiveProfile, profile);

    // Worker admission only: watcher publication can lag behind the actual foreground window.
    internal bool LiveForegroundMatches(Profile profile, long foregroundGeneration)
    {
        var expected = ForegroundIdentity;
        if (expected is null || expected.WindowHandle == IntPtr.Zero || expected.ProcessId == 0 ||
            expected.Generation != foregroundGeneration ||
            !ProfileInputGenerationIsCurrent(profile, foregroundGeneration))
        {
            return false;
        }

        var foreground = _foregroundTransport.GetForegroundWindow();
        _foregroundTransport.GetWindowThreadProcessId(foreground, out var processId);
        return foreground == expected.WindowHandle && processId == expected.ProcessId &&
            ReferenceEquals(expected, ForegroundIdentity) &&
            ProfileInputGenerationIsCurrent(profile, foregroundGeneration);
    }
}
