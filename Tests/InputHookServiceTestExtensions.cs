using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using sWinShortcuts.Utilities;

namespace Tests;

internal static class InputHookServiceTestExtensions
{
    private static readonly FieldInfo ProfileLockField = Field("_profileLock");
    private static readonly FieldInfo RuntimeField = Field("_runtime");
    private static readonly FieldInfo ExecutorField = Field("_inputExecutor");
    private static readonly FieldInfo GesturesField = Field("_gestures");
    private static readonly FieldInfo RapidFireField = Field("_rapidFire");
    private static readonly FieldInfo AutoRunField = Field("_autoRun");
    private static readonly FieldInfo RemapsField = Field("_remaps");
    private static readonly FieldInfo RightButtonField = Field("_rightButtonPressed");
    private static readonly FieldInfo WindowsProfileField = Field("_windowsProfile");
    private static readonly MethodInfo ReleaseAllStateMethod = typeof(InputHookService).GetMethod(
        "ReleaseAllState",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static void StartInputExecutorForTesting(this InputHookService service)
    {
        var runtime = Get<InputRuntimeState>(service, RuntimeField);
        lock (Get<object>(service, ProfileLockField))
        {
            if (runtime.IsRunning)
            {
                throw new InvalidOperationException("Input executor is already running.");
            }

            Get<InputExecutor>(service, ExecutorField).Start("InputExecutorTest");
            Get<RapidFireStateMachine>(service, RapidFireField).Release(preservePhysicalPairing: false);
            runtime.SetRunning(true);
        }
    }

    internal static void StopInputExecutorForTesting(this InputHookService service)
    {
        var runtime = Get<InputRuntimeState>(service, RuntimeField);
        lock (Get<object>(service, ProfileLockField))
        {
            runtime.SetRunning(false);
            Get<InputExecutor>(service, ExecutorField).StopAndDrain(
                () => ReleaseAllStateMethod.Invoke(service, [false, false, null]),
                TimeSpan.FromSeconds(2));
        }
    }

    internal static void ConfigureActiveProfileForTesting(
        this InputHookService service,
        Profile profile,
        long foregroundGeneration,
        bool altPressed)
    {
        var runtime = Get<InputRuntimeState>(service, RuntimeField);
        runtime.SetActiveProfile(profile, foregroundGeneration);
        runtime.SetForegroundIdentity(
            IntPtr.Zero,
            0,
            profile.NormalizedExecutable,
            foregroundGeneration);
        Get<GestureChordStateMachine>(service, GesturesField).SeedAltPressed(altPressed);
    }

    internal static void ConfigureCombinedOverrideForTesting(
        this InputHookService service,
        Key source,
        Key target,
        bool suppressOriginal) =>
        Get<RemapStateMachine>(service, RemapsField)
            .ConfigureCombinedOverrideForTesting(source, target, suppressOriginal);

    internal static void ConfigureLauncherLatchForTesting(
        this InputHookService service,
        Profile windowsProfile,
        Key key)
    {
        WindowsProfileField.SetValue(service, windowsProfile);
        Get<RemapStateMachine>(service, RemapsField)
            .ConfigureLauncherLatchForTesting(windowsProfile, key);
    }

    internal static void ConfigureForegroundAutoRunHandoffForTesting(
        this InputHookService service,
        Profile owner,
        bool sprintEnabled = false,
        SprintActivation sprintMode = SprintActivation.Hold,
        Key sprintKey = Key.LeftShift) =>
        Get<AutoRunStateMachine>(service, AutoRunField)
            .ConfigureForegroundHandoffForTesting(owner, sprintEnabled, sprintMode, sprintKey);

    internal static Task<bool> EnqueueDummyForTesting(this InputHookService service)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Get<InputExecutor>(service, ExecutorField).Enqueue(new InputCommand(
                Key.None,
                IsDown: false,
                Kind: InputCommandKind.DummyKey,
                Completion: completion)))
        {
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    internal static bool HandleLauncherForTesting(
        this InputHookService service,
        Key key,
        bool isDown) =>
        Get<RemapStateMachine>(service, RemapsField).HandleKeyboardEvent(
            KeyInteropUtilities.ToVirtualKey(key),
            isKeyDown: isDown,
            isKeyUp: !isDown,
            rightButtonPressed: (bool)RightButtonField.GetValue(service)!);

    internal static void ConfigureRapidFireForTesting(
        this InputHookService service,
        Profile profile,
        long foregroundGeneration,
        bool armed = true)
    {
        ConfigureActiveProfileForTesting(
            service,
            profile,
            foregroundGeneration,
            altPressed: false);
        Get<InputRuntimeState>(service, RuntimeField).SetAdvancedMode(true);
        Get<RapidFireStateMachine>(service, RapidFireField)
            .ConfigureForTesting(profile, foregroundGeneration, armed);
    }

    internal static void FireRapidFireTimerForTesting(this InputHookService service) =>
        Get<RapidFireStateMachine>(service, RapidFireField).FireTimerForTesting();

    internal static void ConfigureForegroundAutoRunForTesting(
        this InputHookService service,
        Profile owner,
        bool sprintInjected,
        Key sprintKey) =>
        Get<AutoRunStateMachine>(service, AutoRunField)
            .ConfigureForegroundForTesting(owner, sprintInjected, sprintKey);

    private static FieldInfo Field(string name) => typeof(InputHookService).GetField(
        name,
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static T Get<T>(InputHookService service, FieldInfo field) where T : class =>
        (T)field.GetValue(service)!;
}
