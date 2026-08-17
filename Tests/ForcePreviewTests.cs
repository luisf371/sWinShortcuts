using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;

namespace Tests;

// 30 s force preview (game-profile color editing), three layers. Plan-level tests pin the forced
// BuildColorPlan overload (overrides, per-display fallback, disabled degeneration, explicit
// variant). Service-level tests run the real ProfileActivationService with the recording color
// service to pin apply/restore/survival/hotkey-retarget behavior. VM-level tests pin the
// ColorSettingsViewModel checkbox/countdown state machine against a recording runtime (headless:
// PreviewTick is driven directly; no DispatcherTimer is ever created).
public sealed class ForcePreviewTests
{
    // ── Plan-level ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ForcedPlan_OverridesGlobalColor()
    {
        var manager = await CreateManagerAsync();
        EnableDisplayColor(manager.ColorProfile, "DISPLAY1", 60);
        var game = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        EnableDisplayColor(game, "DISPLAY1", 75);

        var plan = ProfileActivationService.BuildColorPlan(
            game.ColorSettings, ColorVariant.Primary, [CreateDisplay("DISPLAY1")], manager);

        var display = Assert.Single(plan.Displays);
        Assert.True(display.IsEnabled);
        Assert.Equal(75, display.Brightness);
    }

    [Fact]
    public async Task ForcedPlan_MissingDisplay_FallsBackToGlobalColor()
    {
        var manager = await CreateManagerAsync();
        EnableDisplayColor(manager.ColorProfile, "DISPLAY1", 60);
        EnableDisplayColor(manager.ColorProfile, "DISPLAY2", 80);
        var game = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        EnableDisplayColor(game, "DISPLAY1", 75);

        var plan = ProfileActivationService.BuildColorPlan(
            game.ColorSettings,
            ColorVariant.Primary,
            [CreateDisplay("DISPLAY1"), CreateDisplay("DISPLAY2")],
            manager);

        Assert.Collection(
            plan.Displays,
            primary => { Assert.Equal("DISPLAY1", primary.DisplayId); Assert.Equal(75, primary.Brightness); },
            secondary => { Assert.Equal("DISPLAY2", secondary.DisplayId); Assert.Equal(80, secondary.Brightness); });
    }

    [Fact]
    public async Task ForcedPlan_DisabledForcedSettings_UsesGlobalFallback()
    {
        var manager = await CreateManagerAsync();
        EnableDisplayColor(manager.ColorProfile, "DISPLAY1", 60);
        var game = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        EnableDisplayColor(game, "DISPLAY1", 75);
        game.ColorSettings.IsEnabled = false; // unchecking profile color mid-preview previews the global look

        var plan = ProfileActivationService.BuildColorPlan(
            game.ColorSettings, ColorVariant.Primary, [CreateDisplay("DISPLAY1")], manager);

        var display = Assert.Single(plan.Displays);
        Assert.Equal(60, display.Brightness);
    }

    [Fact]
    public async Task ForcedPlan_UsesExplicitVariant_WithoutMutatingRuntimeVariant()
    {
        var manager = await CreateManagerAsync();
        var game = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        game.ColorSettings.IsEnabled = true;
        game.ColorSettings.HasSecondary = true;
        SetDisplayColor(game, "DISPLAY1", ColorVariant.Primary, 40);
        SetDisplayColor(game, "DISPLAY1", ColorVariant.Secondary, 90);

        var plan = ProfileActivationService.BuildColorPlan(
            game.ColorSettings, ColorVariant.Secondary, [CreateDisplay("DISPLAY1")], manager);

        Assert.Equal(90, Assert.Single(plan.Displays).Brightness);
        // The preview snapshots the explicit variant — it never flips the runtime active variant.
        Assert.Equal(ColorVariant.Primary, game.ColorSettings.ActiveVariant);
    }

    // ── Service-level ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ForcedPreview_AppliesWhileOtherAppIsForeground_AndClearRestoresGlobal()
    {
        var harness = await CreateServiceHarness(CreateGameProfile(primaryBrightness: 75));
        await harness.Service.StartAsync(CancellationToken.None);
        try
        {
            // Another (unmatched) app is foreground: the global plan is on screen.
            harness.Watcher.RaiseForegroundChanged("other.exe", 321);
            await WaitForAsync(() => harness.LastBrightness == 60);

            harness.Service.SetForcedColorPreview(harness.GameProfile.ColorSettings, ColorVariant.Primary);

            // The forced plan applies even though no game is foreground.
            await WaitForAsync(() => harness.LastBrightness == 75);

            harness.Service.ClearForcedColorPreview();

            // Auto-restore: force skips the plan dedup, so the global plan RE-applies (a second
            // 60-brightness apply) even though those same values were on screen pre-preview.
            await WaitForAsync(() => harness.LastBrightness == 60 && harness.CountAtBrightness(60) >= 2);
        }
        finally
        {
            await StopAsync(harness);
        }
    }

    [Fact]
    public async Task ForcedPreview_ForegroundChangeDuringPreview_KeepsForcedPlan()
    {
        var harness = await CreateServiceHarness(CreateGameProfile(primaryBrightness: 75));
        await harness.Service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => harness.LastBrightness == 60); // warm-up: global applied

            harness.Service.SetForcedColorPreview(harness.GameProfile.ColorSettings, ColorVariant.Primary);
            await WaitForAsync(() => harness.LastBrightness == 75);

            // Alt-tab to a different unmatched app: the worker rebuilds the IDENTICAL forced plan
            // (dedup skips; no fighting) — the preview survives foreground changes.
            harness.Watcher.RaiseForegroundChanged("another.exe", 654);
            await Task.Delay(300); // let both lanes drain

            Assert.Equal(75, harness.LastBrightness);

            // The color lane is still alive (not merely starved): clearing restores the global look.
            harness.Service.ClearForcedColorPreview();
            await WaitForAsync(() => harness.LastBrightness == 60 && harness.CountAtBrightness(60) >= 2);
        }
        finally
        {
            await StopAsync(harness);
        }
    }

    [Fact]
    public async Task ColorVariantToggleDuringPreview_RetargetsForcedVariant_AndToastMatches()
    {
        var harness = await CreateServiceHarness(CreateGameProfile(
            hasSecondary: true, primaryBrightness: 40, secondaryBrightness: 90));
        await harness.Service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => harness.LastBrightness == 60); // warm-up: global applied

            harness.Service.SetForcedColorPreview(harness.GameProfile.ColorSettings, ColorVariant.Primary);
            await WaitForAsync(() => harness.LastBrightness == 40);
            await Task.Delay(100); // publication lands a few instructions after the apply

            // The hotkey flips the color visible on screen — during a preview that IS the forced
            // profile, so the forced record retargets (plan + publication stay consistent).
            harness.Input.RaiseColorVariantToggle();

            await WaitForAsync(() => harness.LastBrightness == 90);
            Assert.Equal(ColorVariant.Secondary, harness.Toast.AppliedVariant);
            Assert.Equal(1, harness.Toast.ShownCount);

            // Clearing still restores the foreground-appropriate (global) plan.
            harness.Service.ClearForcedColorPreview();
            await WaitForAsync(() => harness.LastBrightness == 60);
        }
        finally
        {
            await StopAsync(harness);
        }
    }

    [Fact]
    public async Task ForcedPreview_DisabledForcedSettings_HotkeyTargetsGlobalSettings()
    {
        // Publication invariant: while the forced settings are color-disabled the rendered plan is
        // the global fallback, so _activeColorSettings (the hotkey target) must be the GLOBAL
        // settings — flipping them applies the global Secondary, and the disabled game settings'
        // variant state is never touched.
        var harness = await CreateServiceHarness(CreateGameProfile(colorEnabled: false, primaryBrightness: 75), globalSecondary: true);
        await harness.Service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => harness.LastBrightness == 60); // warm-up: global Primary

            harness.Service.SetForcedColorPreview(harness.GameProfile.ColorSettings, ColorVariant.Primary);
            await Task.Delay(100); // forced plan (global fallback, deduped) + publication land

            harness.Input.RaiseColorVariantToggle();

            // The GLOBAL settings flipped to their Secondary (80) — proving the hotkey did NOT
            // target the disabled forced game settings (whose ToggleVariant would no-op).
            await WaitForAsync(() => harness.LastBrightness == 80);
            Assert.Equal(ColorVariant.Secondary, harness.Toast.AppliedVariant);
            Assert.Equal(ColorVariant.Primary, harness.GameProfile.ColorSettings.ActiveVariant);
        }
        finally
        {
            await StopAsync(harness);
        }
    }

    [Fact]
    public async Task ForcedPreview_SetAndClearWhileStopping_AreSideEffectFree()
    {
        var harness = await CreateServiceHarness(CreateGameProfile(primaryBrightness: 75));
        await harness.Service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => harness.LastBrightness == 60); // warm-up
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None);
        }

        var appliedAfterStop = harness.Color.AppliedProfiles.Count;

        // Post-stop: neither call throws, and no apply/restore is queued (F-010).
        harness.Service.SetForcedColorPreview(harness.GameProfile.ColorSettings, ColorVariant.Primary);
        harness.Service.ClearForcedColorPreview();
        await Task.Delay(200);

        Assert.Equal(appliedAfterStop, harness.Color.AppliedProfiles.Count);
        harness.Toast.Dispose();
    }

    // ── VM-level ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ForcePreview_CountdownTicksDown_AutoOffAndClearsAtZero()
    {
        var runtime = new RecordingPreviewRuntimeService();
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var viewModel = CreateGameViewModel(profile, runtime);
        var colorSettings = viewModel.ColorSettings;

        Assert.True(colorSettings.IsForcePreviewAvailable);
        Assert.False(colorSettings.IsForcePreviewEnabled);
        Assert.Equal(string.Empty, colorSettings.ForcePreviewCountdown);

        colorSettings.IsForcePreviewEnabled = true;
        Assert.True(colorSettings.IsForcePreviewEnabled);
        Assert.Equal("30s", colorSettings.ForcePreviewCountdown);
        var forced = Assert.Single(runtime.ForcedPreviews);
        Assert.Same(profile.ColorSettings, forced.Settings);
        Assert.Equal(ColorVariant.Primary, forced.Variant);
        Assert.Equal(0, runtime.ClearedPreviews);

        for (var i = 0; i < 29; i++)
        {
            colorSettings.PreviewTick();
        }

        Assert.True(colorSettings.IsForcePreviewEnabled);
        Assert.Equal("1s", colorSettings.ForcePreviewCountdown);

        colorSettings.PreviewTick(); // 30th tick: expiry

        Assert.False(colorSettings.IsForcePreviewEnabled);
        Assert.Equal(string.Empty, colorSettings.ForcePreviewCountdown);
        Assert.Equal(1, runtime.ClearedPreviews); // the auto-restore clear
    }

    [Fact]
    public void ForcePreview_EditPresetFlipMidPreview_ResetsToEditedVariant()
    {
        var runtime = new RecordingPreviewRuntimeService();
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var viewModel = CreateGameViewModel(profile, runtime);
        var colorSettings = viewModel.ColorSettings;

        colorSettings.IsForcePreviewEnabled = true; // previews Primary
        colorSettings.HasSecondary = true;
        colorSettings.IsEditingSecondary = true; // flip the segmented toggle mid-preview

        Assert.Equal(2, runtime.ForcedPreviews.Count);
        var last = runtime.ForcedPreviews[^1];
        Assert.Same(profile.ColorSettings, last.Settings);
        Assert.Equal(ColorVariant.Secondary, last.Variant);
        Assert.True(colorSettings.IsForcePreviewEnabled); // same preview, now of Secondary

        // Flipping back re-targets Primary again.
        colorSettings.IsEditingSecondary = false;
        Assert.Equal(ColorVariant.Primary, runtime.ForcedPreviews[^1].Variant);
        Assert.Equal(3, runtime.ForcedPreviews.Count);
    }

    [Fact]
    public void ForcePreview_HasSecondaryFalse_Cancels()
    {
        var runtime = new RecordingPreviewRuntimeService();
        using var viewModel = CreateGameViewModel(ProfileFactory.CreateCustomProfile("Game", "game.exe"), runtime);
        var colorSettings = viewModel.ColorSettings;

        colorSettings.HasSecondary = true;
        colorSettings.IsForcePreviewEnabled = true;
        colorSettings.HasSecondary = false; // the previewed-away preset no longer exists

        Assert.False(colorSettings.IsForcePreviewEnabled);
        Assert.Equal(1, runtime.ClearedPreviews);
    }

    [Fact]
    public void ForcePreview_IsEnabledFalse_Cancels()
    {
        var runtime = new RecordingPreviewRuntimeService();
        using var viewModel = CreateGameViewModel(ProfileFactory.CreateCustomProfile("Game", "game.exe"), runtime);
        var colorSettings = viewModel.ColorSettings;

        colorSettings.IsEnabled = true;
        colorSettings.IsForcePreviewEnabled = true;
        colorSettings.IsEnabled = false; // graying the panel ends the preview

        Assert.False(colorSettings.IsForcePreviewEnabled);
        Assert.Equal(1, runtime.ClearedPreviews);
    }

    [Fact]
    public void ForcePreview_Dispose_Cancels()
    {
        var runtime = new RecordingPreviewRuntimeService();
        var viewModel = CreateGameViewModel(ProfileFactory.CreateCustomProfile("Game", "game.exe"), runtime);

        viewModel.ColorSettings.IsForcePreviewEnabled = true;
        viewModel.Dispose(); // e.g. profile removed mid-preview

        Assert.False(viewModel.ColorSettings.IsForcePreviewEnabled);
        Assert.Equal(1, runtime.ClearedPreviews);
    }

    [Fact]
    public void ForcePreview_GlobalColorProfile_NotAvailable()
    {
        var runtime = new RecordingPreviewRuntimeService();
        using var viewModel = new ProfileViewModel(
            ProfileFactory.CreateColorProfile(),
            new FakeDisplayService(),
            new RecordingColorControlService(),
            profileRuntimeService: runtime);

        // The global Color profile is already live — no preview checkbox.
        Assert.False(viewModel.ColorSettings.IsForcePreviewAvailable);

        viewModel.ColorSettings.IsForcePreviewEnabled = true; // no-op

        Assert.False(viewModel.ColorSettings.IsForcePreviewEnabled);
        Assert.Empty(runtime.ForcedPreviews);
        Assert.Equal(0, runtime.ClearedPreviews);
    }

    [Fact]
    public async Task ForcePreview_MainViewModelSelectionChange_Cancels()
    {
        var store = new InMemoryProfileStore();
        var profileA = ProfileFactory.CreateCustomProfile("Game A", "game-a.exe");
        var profileB = ProfileFactory.CreateCustomProfile("Game B", "game-b.exe");
        store.Profiles.AddRange([profileA, profileB]);
        var manager = new ProfileManager(store);
        var runtime = new RecordingPreviewRuntimeService();
        var main = new MainViewModel(
            manager,
            new FakeDialogService(),
            new FakeDisplayService(),
            new RecordingColorControlService(),
            runtime);

        await main.InitializeAsync();
        var vmA = main.Profiles.Single(p => ReferenceEquals(p.Model, profileA));
        var vmB = main.Profiles.Single(p => ReferenceEquals(p.Model, profileB));

        main.SelectedProfile = vmA;
        vmA.ColorSettings.IsForcePreviewEnabled = true;
        Assert.Single(runtime.ForcedPreviews);

        main.SelectedProfile = vmB; // deselecting cancels A's preview

        Assert.False(vmA.ColorSettings.IsForcePreviewEnabled);
        Assert.Equal(1, runtime.ClearedPreviews);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static ProfileViewModel CreateGameViewModel(Profile profile, RecordingPreviewRuntimeService runtime)
    {
        return new ProfileViewModel(
            profile,
            new FakeDisplayService(),
            new RecordingColorControlService(),
            profileRuntimeService: runtime);
    }

    private static async Task<ProfileManager> CreateManagerAsync()
    {
        var manager = new ProfileManager(new InMemoryProfileStore());
        await manager.InitializeAsync();
        return manager;
    }

    private static void EnableDisplayColor(Profile profile, string displayId, int brightness)
    {
        profile.ColorSettings.IsEnabled = true;
        SetDisplayColor(profile, displayId, ColorVariant.Primary, brightness);
    }

    private static void SetDisplayColor(Profile profile, string displayId, ColorVariant variant, int brightness)
    {
        profile.ColorSettings.SetProfile(new DisplayColorProfile
        {
            DisplayId = displayId,
            IsEnabled = true,
            Brightness = brightness,
            Contrast = 50,
            Gamma = 1.0,
            DigitalVibrance = 50
        }, variant);
    }

    private static Profile CreateGameProfile(
        bool colorEnabled = true,
        bool hasSecondary = false,
        int primaryBrightness = 75,
        int secondaryBrightness = 90)
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.ColorSettings.IsEnabled = colorEnabled;
        SetDisplayColor(profile, "DISPLAY1", ColorVariant.Primary, primaryBrightness);
        if (hasSecondary)
        {
            profile.ColorSettings.HasSecondary = true;
            SetDisplayColor(profile, "DISPLAY1", ColorVariant.Secondary, secondaryBrightness);
        }

        return profile;
    }

    private sealed record ServiceHarness(
        ProfileActivationService Service,
        FakeForegroundWatcher Watcher,
        FakeInputHookService Input,
        RecordingColorControlService Color,
        Profile GameProfile,
        ColorProfileToastService Toast)
    {
        public int LastBrightness =>
            Color.AppliedProfiles.Count > 0 ? Color.AppliedProfiles[^1].Profile.Brightness : -1;

        public int CountAtBrightness(int brightness) =>
            Color.AppliedProfiles.Count(p => p.Profile.Brightness == brightness);
    }

    private static async Task<ServiceHarness> CreateServiceHarness(Profile gameProfile, bool globalSecondary = false)
    {
        var store = new InMemoryProfileStore();
        store.Profiles.Add(gameProfile);
        var manager = new ProfileManager(store);

        // Populate the manager's snapshot first (it synthesizes the global Color profile only
        // during InitializeAsync; the store's profile instances are kept by reference).
        await manager.InitializeAsync();

        // Populate the global Color profile Primary 60 (optionally Secondary 80) so restores are
        // observable.
        var global = manager.ColorProfile;
        global.ColorSettings.IsEnabled = true;
        SetDisplayColor(global, "DISPLAY1", ColorVariant.Primary, 60);
        if (globalSecondary)
        {
            global.ColorSettings.HasSecondary = true;
            SetDisplayColor(global, "DISPLAY1", ColorVariant.Secondary, 80);
        }

        var watcher = new FakeForegroundWatcher();
        var input = new FakeInputHookService();
        var color = new RecordingColorControlService();
        var toast = new ColorProfileToastService(new NullLoggerService(), enqueue: a => a());
        var service = new ProfileActivationService(
            manager,
            watcher,
            input,
            new FakeSystemTrayService(),
            color,
            new FakeDisplayService { Displays = [CreateDisplay("DISPLAY1")] },
            new FakeCrosshairService(),
            new NullLoggerService(),
            toast);

        return new ServiceHarness(service, watcher, input, color, gameProfile, toast);
    }

    private static async Task StopAsync(ServiceHarness harness)
    {
        await harness.Service.StopAsync(CancellationToken.None);
        harness.Toast.Dispose();
    }

    private static DisplayInfo CreateDisplay(string id)
    {
        return new DisplayInfo
        {
            Id = id,
            Name = id,
            DeviceName = $@"\\.\{id}",
            IsPrimary = true
        };
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class RecordingPreviewRuntimeService : IProfileRuntimeService
    {
        public List<(ColorSettings Settings, ColorVariant Variant)> ForcedPreviews { get; } = [];

        public int ClearedPreviews { get; private set; }

        public void NotifyProfileChanged(Profile profile, ProfileChangeKind changeKind)
        {
        }

        public void SetForcedColorPreview(ColorSettings settings, ColorVariant variant)
        {
            ForcedPreviews.Add((settings, variant));
        }

        public void ClearForcedColorPreview()
        {
            ClearedPreviews++;
        }
    }
}
