using System;
using System.Globalization;
using System.Windows.Threading;
using sWinShortcuts.Models;
using sWinShortcuts.ViewModels;
using sWinShortcuts.Views;

namespace sWinShortcuts.Services;

// Shows the playing step beside the cursor for macros with Show steps being played on. Session events only start
// and stop the badge. While it is visible, each rendered frame polls GetMacroSession, because step changes inside
// Playing raise no event (MacroStateMachine.Publish).
//
// Dispatch contract: the handler is ENQUEUE-ONLY, as in RapidFireStatusService. MacroSessionChanged fires on the
// macro worker and from profile rebuilds, so building or touching the window inline there is never safe.
public sealed class MacroStepOverlayService : IDisposable
{
    private readonly ILoggerService _logger;
    private readonly IInputHookService _inputHookService;
    // Captured in the ctor, which DI builds on the UI thread. Null (headless tests) disables the window.
    private readonly Dispatcher? _dispatcher;
    private readonly Action<Action> _enqueue;
    private readonly object _gate = new();
    private bool _disposed;
    private CursorCountdownWindow? _window;

    // Dispatcher/test-thread-only state.
    private (long SessionId, MacroSessionMode Mode, int Row) _applied;
    private MacroDefinition? _labeled;
    private string[] _labels = [];

    /// <summary>Badge text from the last apply that passed the dedup; null hides it. Internal for tests.</summary>
    internal string? AppliedText { get; private set; }

    internal int AppliedCount { get; private set; }

    public MacroStepOverlayService(ILoggerService logger, IInputHookService inputHookService)
        : this(logger, inputHookService, enqueue: null)
    {
    }

    // enqueue: test seam. The production enqueue never runs inline, even on the dispatcher thread.
    internal MacroStepOverlayService(ILoggerService logger, IInputHookService inputHookService, Action<Action>? enqueue)
    {
        _logger = logger;
        _inputHookService = inputHookService;
        _dispatcher = System.Windows.Application.Current?.Dispatcher;
        _enqueue = enqueue ?? ProductionEnqueue;
        _inputHookService.MacroSessionChanged += OnMacroSessionChanged;
        _dispatcher?.BeginInvoke(Prebuild, DispatcherPriority.ApplicationIdle);
    }

    // This thread also runs the input hooks. The first show of the badge blocks it for about 55 ms and holds all
    // input, which can fail a macro's first cursor move (50 ms arrival limit). Later shows take about 1.5 ms.
    private void Prebuild()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        if (_window is not null) return;
        try
        {
            _window = CursorCountdownWindow.CreateLiveBadge(ApplyLatest);
            // Plain Show, not ShowLive: no frame callback runs, so the window stays at its XAML opacity of 0.
            _window.Show();
            _window.Hide();
        }
        catch (Exception ex)
        {
            _logger.Log($"[MacroSteps] Prebuild failed: {ex}");
        }
    }

    private void ProductionEnqueue(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(action, DispatcherPriority.Render);
    }

    private void OnMacroSessionChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        _enqueue(ApplyLatest);
    }

    // Runs on the dispatcher, from a queued event or the visible badge's frame callback. Reads the session at
    // execution time, so the newest call wins.
    private void ApplyLatest()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        try
        {
            var session = _inputHookService.GetMacroSession();
            var applied = (session.SessionId, session.Mode, session.RowCount);
            if (applied == _applied) return;
            // Stored before the window work, so a failing apply logs once instead of every frame.
            _applied = applied;

            string? text = null;
            if (session.Definition is { ShowPlayingSteps: true } definition)
            {
                if (!ReferenceEquals(definition, _labeled))
                {
                    _labels = DescribeSteps(definition.Steps);
                    _labeled = definition;
                }
                text = FormatSession(session.Mode, session.RowCount, _labels);
            }
            AppliedText = text;
            AppliedCount++;

            if (System.Windows.Application.Current is null) return;
            if (text is null) _window?.HideLive();
            else (_window ??= CursorCountdownWindow.CreateLiveBadge(ApplyLatest)).ShowLive(text);
        }
        catch (Exception ex)
        {
            // An overlay must never take the app down (same acceptance as RapidFireStatusService).
            _logger.Log($"[MacroSteps] Apply failed: {ex}");
        }
    }

    // row is 1-based while playing (MacroStateMachine.Publish); labels hold one entry per running step.
    internal static string? FormatSession(MacroSessionMode mode, int row, string[] labels)
    {
        var position = row >= 1 && row <= labels.Length
            ? $"{row.ToString(CultureInfo.InvariantCulture)}/{labels.Length.ToString(CultureInfo.InvariantCulture)} · "
            : null;
        return mode switch
        {
            MacroSessionMode.WaitingForShortcutRelease => "Waiting for the shortcut to be released",
            MacroSessionMode.WaitingForPhysicalModifiers => $"{position}Waiting for modifier keys to be released",
            MacroSessionMode.WaitingForMouseRelease => $"{position}Waiting for mouse buttons to be released",
            MacroSessionMode.Playing when position is not null => position + labels[row - 1],
            _ => null
        };
    }

    // The step list's wording. A Down and Up finish in under a millisecond, so the press-hold Wait between
    // them names the held key or button; it is the part of a press the badge can show.
    internal static string[] DescribeSteps(MacroStep[] steps)
    {
        var holds = MacroViewModel.FindPressHolds(steps);
        var labels = new string[steps.Length];
        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            var duration = step.DurationMs.ToString(CultureInfo.InvariantCulture);
            labels[i] = step.Kind != MacroStepKind.Wait
                ? $"{MacroStepViewModel.ActionLabelOf(step.Kind)} {MacroStepViewModel.DetailOf(step)}".TrimEnd()
                : holds[i]
                    ? $"Hold {MacroStepViewModel.DetailOf(steps[i - 1])} ({duration} ms)"
                    : $"Wait {duration} ms";
        }
        return labels;
    }

    public void Dispose()
    {
        CursorCountdownWindow? window;
        lock (_gate)
        {
            if (_disposed) return;
            // Fence first: new events and queued applies no-op, then detach the window.
            _disposed = true;
            _inputHookService.MacroSessionChanged -= OnMacroSessionChanged;
            window = _window;
            _window = null;
        }

        var dispatcher = _dispatcher;
        // During shutdown WPF owns the final teardown of windows.
        if (window is null || dispatcher is null || dispatcher.HasShutdownStarted) return;
        try
        {
            if (dispatcher.CheckAccess()) window.Close();
            else dispatcher.BeginInvoke(window.Close, DispatcherPriority.Send);
        }
        catch (Exception ex)
        {
            _logger.Log($"[MacroSteps] Window close failed: {ex}");
        }
    }
}
