using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Protocol;
using MyPowerTools.Abstractions;

namespace ScreenEase.MyPowerTools;

public sealed class ScreenEaseModule : IMptModule
{
    private readonly IDisplayService? _displayOverride;
    private readonly IScreenEaseOverlayService? _overlayOverride;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private ModuleContext? _context;
    private ScreenEaseStore? _store;
    private IDisplayService? _display;
    private IScreenEaseOverlayService? _overlay;
    private bool _disposed;
    private bool _disposeResetCompleted;
    private bool _hardwareResetRequired;
    private string _overlayInventoryFingerprint = "";

    public string Id => "screenease";
    public string PackageId => "screenease";
    public Version Version => new(0, 2, 0);

    private ScreenEaseStore Store => _store ?? throw new InvalidOperationException("ScreenEase was not initialized.");
    private IDisplayService Display => _display ?? throw new InvalidOperationException("ScreenEase was not initialized.");
    private IScreenEaseOverlayService Overlay => _overlay ?? throw new InvalidOperationException("ScreenEase overlay was not initialized.");

    public ScreenEaseModule()
    {
    }

    public ScreenEaseModule(IDisplayService display)
    {
        _displayOverride = display;
    }

    internal ScreenEaseModule(IDisplayService display, IScreenEaseOverlayService overlay)
    {
        _displayOverride = display;
        _overlayOverride = overlay;
    }

    public async ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _context = context;
            _disposed = false;
            _disposeResetCompleted = false;
            _hardwareResetRequired = false;
            Directory.CreateDirectory(context.DataDirectory);
            Directory.CreateDirectory(context.CacheDirectory);
            Directory.CreateDirectory(context.LogDirectory);
            var legacySettingsPath = IsTemporaryDataDirectory(context.DataDirectory)
                ? null
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ScreenEase",
                    "settings.json");
            _store = new ScreenEaseStore(
                Path.Combine(context.DataDirectory, "screenease-state.json"),
                legacySettingsPath,
                Path.Combine(context.LogDirectory, "screenease-state-recovery.log"));
            _display = _displayOverride ?? CreateDisplayService(context);
            _overlay = _overlayOverride ?? (OperatingSystem.IsWindows()
                ? new ScreenEaseWindowsOverlayService()
                : new ScreenEaseLogicalOverlayService());
            Store.EnsureDefaults();
            await ReapplyPersistedEffectCoreAsync(cancellationToken).ConfigureAwait(false);
            await RefreshOverlayCoreAsync(Store.Load(), cancellationToken, force: true).ConfigureAwait(false);
            await SyncPendingHotkeysCoreAsync(cancellationToken).ConfigureAwait(false);
            return new InitializeResult(true, context.ProtocolVersion, ["status", "commands", "settings", "logs", "dashboardCard", "detailPage"]);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await RefreshDynamicStateCoreAsync(cancellationToken).ConfigureAwait(false);
            var displays = await Display.ListDisplaysAsync(cancellationToken).ConfigureAwait(false);
            var writer = await Display.GetWriterStatusAsync(cancellationToken).ConfigureAwait(false);
            var overlay = await Overlay.GetStateAsync(cancellationToken).ConfigureAwait(false);
            var usableDisplays = displays.Where(IsConnectedDisplay).ToArray();
            var nativeHostReady = writer.Available;
            var effect = state.GetEffect();
            var checks = new[]
            {
                new HealthCheckSnapshot("display.enumeration", "Display enumeration", usableDisplays.Length > 0, usableDisplays.Length > 0 ? $"{usableDisplays.Length} display(s) detected." : "No usable display provider was detected."),
                new HealthCheckSnapshot("profile.store", "Profile store", state.Profiles.Count > 0 && string.IsNullOrWhiteSpace(Store.LastRecoveryMessage), string.IsNullOrWhiteSpace(Store.LastRecoveryMessage) ? $"{state.Profiles.Count} profile(s) available; active profile is '{state.ActiveProfileId}'." : Store.LastRecoveryMessage),
                new HealthCheckSnapshot("rule.store", "Rule store", true, $"{state.Rules.Count} rule(s) configured."),
                new HealthCheckSnapshot("native-host", "Native display writer", nativeHostReady, NativeWriterMessage(state.NativeHost, writer)),
                new HealthCheckSnapshot("overlay", "Display overlay", !state.GetOverlay().Enabled || overlay.Enabled, state.GetOverlay().Enabled ? overlay.Message : "Screen overlay is disabled.")
            };

            var moduleState = usableDisplays.Length == 0 || !string.IsNullOrWhiteSpace(Store.LastRecoveryMessage) ? "degraded" : "running";
            var summary = nativeHostReady
                ? $"ScreenEase effect is {(effect.Enabled ? "enabled" : "disabled")} with profile '{effect.ProfileId}' across {usableDisplays.Length} display(s)."
                : $"Profile '{effect.ProfileId}' is managed in logical state; display hardware is read-only in this session.";
            return new ModuleStatusSnapshot(Id, moduleState, summary, DateTimeOffset.UtcNow, checks, 0);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            Command("screenease.status.summary", "Summarize ScreenEase status", "Display state, active profile, rules, and native host readiness"),
            Command("screenease.displays.list", "List displays", "Enumerate displays through the platform display provider"),
            Command("screenease.effect.status", "Show ScreenEase effect", "Read the persisted logical eye-care effect independently from display hardware readiness"),
            Command("screenease.effect.apply", "Apply manual ScreenEase values", "Apply manual color temperature and brightness without overwriting the selected saved profile", EffectApplyParameters()),
            Command("screenease.effect.disable", "Disable ScreenEase effect", "Disable the logical eye-care effect while retaining the selected profile and values"),
            Command("screenease.effect.toggle", "Toggle ScreenEase effect", "Enable the current effect or disable it and restore the identity gamma ramp"),
            Command("screenease.effect.brightness.increase", "Increase ScreenEase brightness", "Increase the active effect brightness by five percentage points"),
            Command("screenease.effect.brightness.decrease", "Decrease ScreenEase brightness", "Decrease the active effect brightness by five percentage points"),
            Command("screenease.effect.temperature.increase", "Increase ScreenEase color temperature", "Increase the active effect color temperature by 250 K"),
            Command("screenease.effect.temperature.decrease", "Decrease ScreenEase color temperature", "Decrease the active effect color temperature by 250 K"),
            Command("screenease.profile.apply-long-read", "Apply long-read profile", "Enable and apply the original long-read ScreenEase profile"),
            Command("screenease.profile.apply-low-blue-evening", "Apply low-blue evening profile", "Enable and apply the original low-blue evening ScreenEase profile"),
            Command("screenease.profile.list", "List ScreenEase profiles", "Show brightness and color temperature profiles"),
            Command("screenease.profile.plan", "Plan profile application", "Preview display changes for a selected profile", ProfileParameters(includeHardwareWrite: false)),
            Command("screenease.profile.apply", "Apply ScreenEase profile", "Switch active profile and request hardware apply when native host is ready", ProfileParameters(includeHardwareWrite: true)),
            Command("screenease.profile.save", "Save ScreenEase profile", "Persist a profile into ScreenEase shared state", SaveProfileParameters()),
            Command("screenease.schedule.configure", "Configure ScreenEase schedule", "Persist day/night value and local schedule settings", ScheduleParameters()),
            Command("screenease.reminder.configure", "Configure ScreenEase rest timer", "Persist work, break, auto-start, and enabled settings"),
            Command("screenease.reminder.status", "Show ScreenEase rest timer", "Read and advance the persisted work and break timer state"),
            Command("screenease.reminder.start", "Start ScreenEase rest timer", "Start a persisted work session"),
            Command("screenease.reminder.pause", "Pause ScreenEase rest timer", "Pause the current persisted work or break session"),
            Command("screenease.reminder.resume", "Resume ScreenEase rest timer", "Resume the persisted paused session"),
            Command("screenease.reminder.reset", "Reset ScreenEase rest timer", "Reset the persisted timer and completed session count"),
            Command("screenease.overlay.status", "Show ScreenEase overlay", "Read the configured overlay and current native window state"),
            Command("screenease.overlay.configure", "Configure ScreenEase overlay", "Apply or hide the click-through overlay on every display", OverlayParameters()),
            Command("screenease.overlay.toggle", "Toggle ScreenEase overlay", "Toggle the persisted overlay without changing its opacity or color"),
            Command("screenease.legacy.import", "Import CareUEyes settings", "Import the original CareUEyes-compatible INI profile, schedule, transition, and rest settings",
            [
                new CommandParameterDescriptor("path", "INI settings path", "text", true, "")
            ]),
            Command("screenease.rules.status", "Show ScreenEase rules", "Inspect schedule and ambient rule status"),
            Command("screenease.native-writer.status", "Show ScreenEase native writer status", "Probe Windows gamma-ramp display write readiness")
        ];
        return ValueTask.FromResult(commands);
    }

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return request.CommandId switch
            {
                "screenease.status.summary" => Succeeded(request, (await BuildStatusPayloadAsync(cancellationToken).ConfigureAwait(false)).ToJsonString()),
                "screenease.displays.list" => Succeeded(request, DisplayListJson(await Display.ListDisplaysAsync(cancellationToken).ConfigureAwait(false)).ToJsonString()),
                "screenease.effect.status" => Succeeded(request, Store.Load().GetEffect().ToJson().ToJsonString()),
                "screenease.effect.apply" => await ApplyEffectAsync(request, cancellationToken).ConfigureAwait(false),
                "screenease.effect.disable" => await DisableEffectAsync(request, cancellationToken).ConfigureAwait(false),
                "screenease.effect.toggle" => await ToggleEffectAsync(request, cancellationToken).ConfigureAwait(false),
                "screenease.effect.brightness.increase" => await AdjustEffectAsync(request, brightnessDelta: 5, temperatureDelta: 0, cancellationToken).ConfigureAwait(false),
                "screenease.effect.brightness.decrease" => await AdjustEffectAsync(request, brightnessDelta: -5, temperatureDelta: 0, cancellationToken).ConfigureAwait(false),
                "screenease.effect.temperature.increase" => await AdjustEffectAsync(request, brightnessDelta: 0, temperatureDelta: 250, cancellationToken).ConfigureAwait(false),
                "screenease.effect.temperature.decrease" => await AdjustEffectAsync(request, brightnessDelta: 0, temperatureDelta: -250, cancellationToken).ConfigureAwait(false),
                "screenease.profile.apply-long-read" => await ApplyFixedProfileAsync(request, "long-read", cancellationToken).ConfigureAwait(false),
                "screenease.profile.apply-low-blue-evening" => await ApplyFixedProfileAsync(request, "low-blue-evening", cancellationToken).ConfigureAwait(false),
                "screenease.profile.list" => Succeeded(request, Store.Load().ProfilesJson().ToJsonString()),
                "screenease.profile.plan" => await PlanProfileAsync(request, cancellationToken).ConfigureAwait(false),
                "screenease.profile.apply" => await ApplyProfileAsync(request, cancellationToken).ConfigureAwait(false),
                "screenease.profile.save" => SaveProfile(request),
                "screenease.schedule.configure" => await ConfigureScheduleAsync(request, cancellationToken).ConfigureAwait(false),
                "screenease.reminder.configure" => ConfigureReminder(request),
                "screenease.reminder.status" => ReminderStatus(request),
                "screenease.reminder.start" => ReminderStart(request),
                "screenease.reminder.pause" => ReminderPause(request),
                "screenease.reminder.resume" => ReminderResume(request),
                "screenease.reminder.reset" => ReminderReset(request),
                "screenease.overlay.status" => await OverlayStatusAsync(request, cancellationToken).ConfigureAwait(false),
                "screenease.overlay.configure" => await ConfigureOverlayAsync(request, cancellationToken).ConfigureAwait(false),
                "screenease.overlay.toggle" => await ToggleOverlayAsync(request, cancellationToken).ConfigureAwait(false),
                "screenease.legacy.import" => await ImportLegacyIniAsync(request, cancellationToken).ConfigureAwait(false),
                "screenease.rules.status" => Succeeded(request, Store.Load().RulesJson().ToJsonString()),
                "screenease.native-writer.status" => Succeeded(request, (await BuildNativeWriterPayloadAsync(cancellationToken).ConfigureAwait(false)).ToJsonString()),
                _ => Failed(request, MptErrorCodes.NotFound, $"Command '{request.CommandId}' is not implemented by ScreenEase.")
            };
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = await RefreshDynamicStateWithGateAsync(cancellationToken).ConfigureAwait(false);
        var seq = Math.Max(3UL, cursor.LastEventSeq);
        if (cursor.LastEventSeq < 1)
        {
            var displays = await Display.ListDisplaysAsync(cancellationToken);
            yield return new MptModuleEvent(
                Id,
                1,
                "display.changed",
                DateTimeOffset.UtcNow,
                new JsonObject
                {
                    ["title"] = "Display inventory",
                    ["message"] = $"{displays.Count} display(s) reported by the platform provider.",
                    ["displayCount"] = displays.Count,
                    ["usableDisplayCount"] = displays.Count(IsConnectedDisplay)
                });
        }

        if (cursor.LastEventSeq < 2)
        {
            yield return new MptModuleEvent(
                Id,
                2,
                "profile.applied",
                DateTimeOffset.UtcNow,
                new JsonObject
                {
                    ["title"] = "ScreenEase active profile",
                    ["message"] = $"Active profile is '{state.ActiveProfileId}'.",
                    ["profileId"] = state.ActiveProfileId,
                    ["profileCount"] = state.Profiles.Count
                });
        }

        if (cursor.LastEventSeq < 3)
        {
            var writer = await Display.GetWriterStatusAsync(cancellationToken);
            yield return new MptModuleEvent(
                Id,
                3,
                writer.Available ? "native-writer.ready" : "native-writer.failed",
                DateTimeOffset.UtcNow,
                new JsonObject
                {
                    ["title"] = "ScreenEase native writer",
                    ["message"] = writer.Message,
                    ["available"] = writer.Available,
                    ["state"] = writer.State
                });
        }

        var fingerprint = await BuildEventFingerprintAsync(cancellationToken);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            var nextFingerprint = await BuildEventFingerprintAsync(cancellationToken);
            if (string.Equals(nextFingerprint, fingerprint, StringComparison.Ordinal))
            {
                continue;
            }

            fingerprint = nextFingerprint;
            state = await LoadStateWithGateAsync(cancellationToken).ConfigureAwait(false);
            seq++;
            yield return new MptModuleEvent(
                Id,
                seq,
                "display.changed",
                DateTimeOffset.UtcNow,
                new JsonObject
                {
                    ["title"] = "ScreenEase state changed",
                    ["message"] = $"Active profile is '{state.ActiveProfileId}'.",
                    ["profileId"] = state.ActiveProfileId,
                    ["profileCount"] = state.Profiles.Count
                });
        }
    }

    private async Task<string> BuildEventFingerprintAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await RefreshDynamicStateCoreAsync(cancellationToken).ConfigureAwait(false);
            var displays = await Display.ListDisplaysAsync(cancellationToken).ConfigureAwait(false);
            var writer = await Display.GetWriterStatusAsync(cancellationToken).ConfigureAwait(false);
            var displayFingerprint = string.Join(
                ",",
                displays
                    .OrderBy(display => display.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(display => $"{display.Id}:{display.State}:{display.Width}x{display.Height}:{display.RefreshRateHz}:{display.Primary}"));
            var effect = state.GetEffect();
            return $"{state.ActiveProfileId}|{effect.Enabled}:{effect.ProfileId}:{effect.ColorTemperatureKelvin}:{effect.BrightnessPercent}:{effect.IsNightValue}:{effect.AppliedAt:O}|{state.Profiles.Count}|{state.Rules.Count}|{writer.Available}:{writer.State}:{writer.Message}|{displayFingerprint}";
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """
        {
          "type": "object",
          "properties": {
            "activeProfileId": { "type": "string", "default": "low-blue-evening" },
            "profiles": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["id", "name"],
                "properties": {
                  "id": { "type": "string" },
                  "name": { "type": "string" },
                  "brightness": { "type": "integer", "minimum": 1, "maximum": 150 },
                  "colorTemperature": { "type": "integer", "minimum": 1000, "maximum": 10000 },
                  "nightBrightness": { "type": "integer", "minimum": 1, "maximum": 150 },
                  "nightColorTemperature": { "type": "integer", "minimum": 1000, "maximum": 10000 }
                }
              }
            },
            "rules": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["id", "profileId", "enabled"],
                "properties": {
                  "id": { "type": "string" },
                  "profileId": { "type": "string" },
                  "enabled": { "type": "boolean" },
                  "condition": { "type": "string" }
                }
              }
            },
            "nativeHost": {
              "type": "object",
              "properties": {
                "available": { "type": "boolean", "default": false },
                "state": { "type": "string" },
                "message": { "type": "string" }
              }
            },
            "effect": {
              "type": "object",
              "properties": {
                "enabled": { "type": "boolean", "default": false },
                "profileId": { "type": "string", "default": "low-blue-evening" },
                "colorTemperatureKelvin": { "type": "integer", "minimum": 1000, "maximum": 10000, "default": 3700 },
                "brightnessPercent": { "type": "integer", "minimum": 1, "maximum": 150, "default": 75 },
                "isNightValue": { "type": "boolean", "default": false },
                "appliedAt": { "type": "string", "format": "date-time" }
              }
            },
            "reminder": {
              "type": "object",
              "properties": {
                "enabled": { "type": "boolean", "default": false },
                "autoStartNext": { "type": "boolean", "default": false },
                "focusMinutes": { "type": "integer", "minimum": 1, "maximum": 240, "default": 25 },
                "shortBreakMinutes": { "type": "integer", "minimum": 1, "maximum": 120, "default": 5 },
                "longBreakMinutes": { "type": "integer", "minimum": 1, "maximum": 240, "default": 15 },
                "longBreakInterval": { "type": "integer", "minimum": 1, "maximum": 12, "default": 4 }
              }
            },
            "schedule": {
              "type": "object",
              "properties": {
                "useNightValues": { "type": "boolean", "default": true },
                "useSchedule": { "type": "boolean", "default": false },
                "sunrise": { "type": "string", "default": "07:00" },
                "sunset": { "type": "string", "default": "19:00" }
              }
            },
            "advanced": {
              "type": "object",
              "properties": {
                "smoothTransitions": { "type": "boolean", "default": true },
                "transitionDurationMs": { "type": "integer", "minimum": 0, "maximum": 120000, "default": 2000 }
              }
            },
            "overlay": {
              "type": "object",
              "properties": {
                "enabled": { "type": "boolean", "default": false },
                "opacityPercent": { "type": "integer", "minimum": 0, "maximum": 95, "default": 18 },
                "colorHex": { "type": "string", "default": "#FFC98A" }
              }
            },
            "hotkeys": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["id", "gesture", "enabled"],
                "properties": {
                  "id": {
                    "type": "string",
                    "enum": [
                      "toggle-enabled",
                      "brightness-up",
                      "brightness-down",
                      "temperature-up",
                      "temperature-down",
                      "long-read-profile",
                      "low-blue-evening-profile",
                      "toggle-overlay"
                    ]
                  },
                  "gesture": { "type": "string", "minLength": 1 },
                  "enabled": { "type": "boolean", "default": false }
                },
                "additionalProperties": false
              }
            }
          }
        }
        """));
    }

    public async ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return Store.Load().ToSettingsSnapshot(Id);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        if (patch.Patch.TryGetPropertyValue("activeProfileId", out var activeNode) && activeNode is not null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(activeNode.GetValue<string>()))
                {
                    messages.Add("activeProfileId cannot be empty.");
                }
            }
            catch (InvalidOperationException)
            {
                messages.Add("activeProfileId must be a string.");
            }
        }

        if (patch.Patch.TryGetPropertyValue("profiles", out var profilesNode) && profilesNode is JsonArray profiles)
        {
            foreach (var profile in profiles.OfType<JsonObject>())
            {
                var parsed = ScreenEaseProfile.FromJson(profile);
                var validation = parsed.Validate();
                messages.AddRange(validation);
            }
        }

        if (patch.Patch.TryGetPropertyValue("nativeHost", out var nativeHostNode) && nativeHostNode is JsonObject nativeHost &&
            nativeHost.TryGetPropertyValue("enabled", out var enabledNode) && enabledNode is not null)
        {
            try
            {
                _ = enabledNode.GetValue<bool>();
            }
            catch (InvalidOperationException)
            {
                messages.Add("nativeHost.enabled must be a boolean.");
            }
        }

        return ValueTask.FromResult(new SettingsValidationResult(
            messages.Count == 0,
            messages,
            messages.Count == 0 ? null : new MptRuntimeError(MptErrorCodes.ValidationFailed, string.Join("; ", messages))));
    }

    public async ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = Store.Load();
            var mergedValues = SettingsJson.Merge(current.ToSettingsSnapshot(Id).Values, snapshot.Values);
            var state = ScreenEaseState.FromSettings(mergedValues, current.GetReminderState(), current.GetEffect());
            var hotkeysChanged = current.HotkeysNeedSync ||
                                 !current.GetHotkeys().SequenceEqual(state.GetHotkeys());
            state = state with { HotkeysNeedSync = hotkeysChanged };
            var effectInputsChanged =
                !string.Equals(current.ActiveProfileId, state.ActiveProfileId, StringComparison.OrdinalIgnoreCase) ||
                !current.Profiles.SequenceEqual(state.Profiles) ||
                current.GetSchedule() != state.GetSchedule();
            if (snapshot.Values["effect"] is not JsonObject &&
                effectInputsChanged &&
                state.FindProfile(state.ActiveProfileId) is { } profile)
            {
                var values = profile.ResolveValues(state.GetSchedule(), DateTimeOffset.Now);
                state = state with
                {
                    Effect = current.GetEffect() with
                    {
                        ProfileId = profile.Id,
                        ColorTemperatureKelvin = values.ColorTemperature,
                        BrightnessPercent = values.Brightness,
                        IsNightValue = values.IsNightValue,
                        AppliedAt = DateTimeOffset.Now
                    }
                };
            }
            state = state.AdvanceReminder(DateTimeOffset.UtcNow);
            Store.Save(state);
            await ReapplyPersistedEffectCoreAsync(cancellationToken).ConfigureAwait(false);
            await RefreshOverlayCoreAsync(Store.Load(), cancellationToken, force: true).ConfigureAwait(false);
            if (hotkeysChanged && await SyncHotkeysCoreAsync(Store.Load(), cancellationToken).ConfigureAwait(false))
            {
                var synchronized = Store.Load() with { HotkeysNeedSync = false, UpdatedAt = DateTimeOffset.UtcNow };
                Store.Save(synchronized);
            }
            return Store.Load().ToSettingsSnapshot(Id) with { Revision = snapshot.Revision };
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UiSurfaceDescriptor> surfaces =
        [
            new("screenease.dashboard", "dashboard-card", "ScreenEase", new JsonObject { ["moduleId"] = Id }),
            new("screenease.detail", "detail-page", "ScreenEase Profiles", new JsonObject { ["moduleId"] = Id }),
            new("screenease.settings", "settings", "ScreenEase Settings", new JsonObject { ["moduleId"] = Id })
        ];
        return ValueTask.FromResult(surfaces);
    }

    public async ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_overlay is not null)
            {
                using var overlayTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await _overlay.HideAsync(overlayTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or ExternalException)
                {
                    // Continue with native display cleanup even when an overlay window rejects shutdown.
                }
                finally
                {
                    try
                    {
                        _overlay.Dispose();
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
                    {
                        // Gamma cleanup remains mandatory after an overlay disposal failure.
                    }
                }
            }

            if (!_disposeResetCompleted && _hardwareResetRequired && _display is IScreenEaseDisplayResetService resetService)
            {
                _disposeResetCompleted = true;
                using var resetTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    var reset = await resetService.ResetAsync(resetTimeout.Token).ConfigureAwait(false);
                    if (reset.Success)
                    {
                        _hardwareResetRequired = false;
                    }
                }
                catch (OperationCanceledException) when (resetTimeout.IsCancellationRequested)
                {
                    // Disposal remains bounded; the logical effect is persisted for reapply on next startup.
                }
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<CommandExecutionResult> PlanProfileAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var state = Store.Load();
        var profileId = ReadString(request.Args, "profileId") ?? state.ActiveProfileId;
        var displayId = ReadString(request.Args, "displayId") ?? "all";
        var profile = state.FindProfile(profileId);
        if (profile is null)
        {
            return Failed(request, MptErrorCodes.NotFound, $"ScreenEase profile '{profileId}' was not found.");
        }

        var displays = await Display.ListDisplaysAsync(cancellationToken);
        return Succeeded(request, BuildPlan(state, profile, displays, displayId).ToJsonString());
    }

    private async Task<CommandExecutionResult> ApplyProfileAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return await ApplyEffectCoreAsync(request, allowManualValues: false, cancellationToken);
    }

    private async Task<CommandExecutionResult> ApplyEffectAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return await ApplyEffectCoreAsync(request, allowManualValues: true, cancellationToken);
    }

    private async Task<CommandExecutionResult> ApplyEffectCoreAsync(
        CommandRequest request,
        bool allowManualValues,
        CancellationToken cancellationToken)
    {
        var state = Store.Load();
        var requestedProfileId = ReadString(request.Args, "profileId");
        var requestedTemperature = allowManualValues ? ReadInt(request.Args, "colorTemperatureKelvin") : null;
        var requestedBrightness = allowManualValues ? ReadInt(request.Args, "brightnessPercent") : null;
        var profileId = requestedProfileId ?? state.ActiveProfileId;
        var profile = state.FindProfile(profileId);
        if (profile is null)
        {
            return Failed(request, MptErrorCodes.NotFound, $"ScreenEase profile '{profileId}' was not found.");
        }

        var hasManualValues = requestedTemperature is not null || requestedBrightness is not null;
        var useManualProfile = hasManualValues &&
            (requestedProfileId is null || string.Equals(ScreenEaseProfileIds.Normalize(requestedProfileId), "manual-adjustment", StringComparison.OrdinalIgnoreCase));

        if (useManualProfile)
        {
            var temperature = Math.Clamp(requestedTemperature ?? profile.ColorTemperature, 1000, 10000);
            var brightness = Math.Clamp(requestedBrightness ?? profile.Brightness, 1, 150);
            profile = new ScreenEaseProfile(
                "manual-adjustment",
                "自定义调节",
                brightness,
                temperature,
                brightness,
                temperature);
            state = state with
            {
                Profiles = state.Profiles
                    .Where(item => !string.Equals(item.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
                    .Append(profile)
                    .ToArray()
            };
        }

        var displays = await Display.ListDisplaysAsync(cancellationToken);
        var writer = await Display.GetWriterStatusAsync(cancellationToken);
        var hardwareWrite = ReadBool(request.Args, "hardwareWrite") ?? writer.Available;
        var displayId = ReadString(request.Args, "displayId") ?? "all";
        var resolvedValues = profile.ResolveValues(state.GetSchedule(), DateTimeOffset.Now);
        var values = hasManualValues
            ? new ScreenEaseProfileValues(
                Math.Clamp(requestedBrightness ?? resolvedValues.Brightness, 1, 150),
                Math.Clamp(requestedTemperature ?? resolvedValues.ColorTemperature, 1000, 10000),
                false)
            : resolvedValues;
        var appliedAt = DateTimeOffset.Now;
        var nativeResult = hardwareWrite
            ? await Display.ApplyProfileAsync(
                new DisplayProfileIntent(profile.Id, displayId, values.Brightness, values.ColorTemperature, "ScreenEase profile apply"),
                cancellationToken)
             : new BrokerOperationResult(
                true,
                "logical-only",
                 "ScreenEase effect state was applied; no display hardware write was requested.");
        if (hardwareWrite)
        {
            _hardwareResetRequired = true;
            _disposeResetCompleted = false;
        }
        state = state with
        {
            ActiveProfileId = profile.Id,
            Effect = new ScreenEaseDisplayEffect(
                true,
                profile.Id,
                values.ColorTemperature,
                values.Brightness,
                values.IsNightValue,
                appliedAt),
            NativeHost = new ScreenEaseNativeHostState(true, writer.Available, nativeResult.Message),
            UpdatedAt = appliedAt
        };
        Store.Save(state);

        var payload = BuildPlan(state, profile, displays, displayId);
        payload["effect"] = state.GetEffect().ToJson();
        payload["nativeHost"] = BuildNativeHostJson(state.NativeHost, writer, nativeResult, hardwareWrite);

        if (hardwareWrite && !nativeResult.Success)
        {
            return Failed(request, MptErrorCodes.RuntimeUnavailable, nativeResult.Message);
        }

        return Succeeded(request, payload.ToJsonString());
    }

    private async Task<CommandExecutionResult> DisableEffectAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var appliedAt = DateTimeOffset.Now;
        var state = Store.Load();
        var effect = state.GetEffect() with
        {
            Enabled = false,
            AppliedAt = appliedAt
        };
        state = state with { Effect = effect, UpdatedAt = appliedAt };
        Store.Save(state);
        var payload = effect.ToJson();
        payload["displayReset"] = new JsonObject
        {
            ["attempted"] = false,
            ["success"] = true,
            ["state"] = "logical-only",
            ["message"] = "The logical ScreenEase effect was disabled; this display provider exposes no gamma-ramp reset operation."
        };
        if (Display is IScreenEaseDisplayResetService resetService)
        {
            var reset = await resetService.ResetAsync(cancellationToken);
            payload["displayReset"] = new JsonObject
            {
                ["attempted"] = true,
                ["success"] = reset.Success,
                ["state"] = reset.State,
                ["message"] = reset.Message
            };
            if (reset.Success)
            {
                _hardwareResetRequired = false;
                _disposeResetCompleted = true;
            }
        }

        return Succeeded(request, payload.ToJsonString());
    }

    private async Task<CommandExecutionResult> ToggleEffectAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var effect = Store.Load().GetEffect();
        if (effect.Enabled)
        {
            return await DisableEffectAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await ApplyEffectCoreAsync(
            request with
            {
                Args = new JsonObject
                {
                    ["profileId"] = effect.ProfileId,
                    ["colorTemperatureKelvin"] = effect.ColorTemperatureKelvin,
                    ["brightnessPercent"] = effect.BrightnessPercent
                }
            },
            allowManualValues: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandExecutionResult> AdjustEffectAsync(
        CommandRequest request,
        int brightnessDelta,
        int temperatureDelta,
        CancellationToken cancellationToken)
    {
        var effect = Store.Load().GetEffect();
        return await ApplyEffectCoreAsync(
            request with
            {
                Args = new JsonObject
                {
                    ["profileId"] = effect.ProfileId,
                    ["colorTemperatureKelvin"] = Math.Clamp(effect.ColorTemperatureKelvin + temperatureDelta, 1000, 10000),
                    ["brightnessPercent"] = Math.Clamp(effect.BrightnessPercent + brightnessDelta, 1, 150)
                }
            },
            allowManualValues: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandExecutionResult> ApplyFixedProfileAsync(
        CommandRequest request,
        string profileId,
        CancellationToken cancellationToken)
    {
        return await ApplyEffectCoreAsync(
            request with { Args = new JsonObject { ["profileId"] = profileId } },
            allowManualValues: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandExecutionResult> OverlayStatusAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var state = Store.Load();
        var runtime = await Overlay.GetStateAsync(cancellationToken).ConfigureAwait(false);
        return Succeeded(request, BuildOverlayPayload(state.GetOverlay(), runtime).ToJsonString());
    }

    private async Task<CommandExecutionResult> ConfigureOverlayAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var state = Store.Load();
        var current = state.GetOverlay();
        var overlay = ScreenEaseOverlaySettings.Normalize(new ScreenEaseOverlaySettings(
            ReadBool(request.Args, "enabled") ?? current.Enabled,
            ReadInt(request.Args, "opacityPercent") ?? current.OpacityPercent,
            ReadString(request.Args, "colorHex") ?? current.ColorHex));
        state = state with { Overlay = overlay, UpdatedAt = DateTimeOffset.UtcNow };
        Store.Save(state);
        var runtime = await RefreshOverlayCoreAsync(state, cancellationToken, force: true).ConfigureAwait(false);
        return Succeeded(request, BuildOverlayPayload(overlay, runtime).ToJsonString());
    }

    private async Task<CommandExecutionResult> ToggleOverlayAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var current = Store.Load().GetOverlay();
        return await ConfigureOverlayAsync(
            request with { Args = new JsonObject { ["enabled"] = !current.Enabled } },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandExecutionResult> ImportLegacyIniAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var path = ReadString(request.Args, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return Failed(request, MptErrorCodes.ValidationFailed, "A CareUEyes-compatible INI settings path is required.");
        }

        ScreenEaseState imported;
        try
        {
            imported = await ScreenEaseLegacyIniImporter.ImportAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return Failed(request, MptErrorCodes.ValidationFailed, exception.Message);
        }

        Store.Save(imported);
        await ReapplyPersistedEffectCoreAsync(cancellationToken).ConfigureAwait(false);
        var overlay = await RefreshOverlayCoreAsync(Store.Load(), cancellationToken, force: true).ConfigureAwait(false);
        if (await SyncHotkeysCoreAsync(Store.Load(), cancellationToken).ConfigureAwait(false))
        {
            var synchronized = Store.Load() with { HotkeysNeedSync = false, UpdatedAt = DateTimeOffset.UtcNow };
            Store.Save(synchronized);
        }
        var payload = Store.Load().ToSettingsSnapshot(Id).Values.DeepClone().AsObject();
        payload["overlayRuntime"] = overlay.ToJson();
        return Succeeded(request, payload.ToJsonString());
    }

    private CommandExecutionResult SaveProfile(CommandRequest request)
    {
        var profile = ScreenEaseProfile.FromJson(request.Args);
        var validation = profile.Validate();
        if (validation.Count > 0)
        {
            return Failed(request, MptErrorCodes.ValidationFailed, string.Join("; ", validation));
        }

        var state = Store.Load();
        var profiles = state.Profiles
            .Where(item => !string.Equals(item.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
            .Append(profile)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        state = state with { Profiles = profiles, UpdatedAt = DateTimeOffset.UtcNow };
        Store.Save(state);
        return Succeeded(request, profile.ToJson().ToJsonString());
    }

    private async Task<CommandExecutionResult> ConfigureScheduleAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var state = Store.Load();
        var schedule = ScreenEaseScheduleSettings.FromJson(request.Args);
        state = state with { Schedule = schedule, UpdatedAt = DateTimeOffset.UtcNow };
        Store.Save(state);
        var refreshed = await RefreshDynamicStateCoreAsync(cancellationToken).ConfigureAwait(false);
        var payload = schedule.ToJson();
        payload["effect"] = refreshed.GetEffect().ToJson();
        return Succeeded(request, payload.ToJsonString());
    }

    private CommandExecutionResult ConfigureReminder(CommandRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var state = Store.Load();
        var reminder = ScreenEaseReminderSettings.FromJson(request.Args);
        var runtime = reminder.Enabled
            ? ScreenEaseReminderRuntime.Tick(state.GetReminderState(), reminder, now)
            : ScreenEaseReminderRuntime.Stopped();
        state = state with { Reminder = reminder, ReminderState = runtime, UpdatedAt = now };
        Store.Save(state);
        return Succeeded(request, new JsonObject
        {
            ["settings"] = reminder.ToJson(),
            ["state"] = runtime.ToJson(now)
        }.ToJsonString());
    }

    private CommandExecutionResult ReminderStatus(CommandRequest request)
    {
        var state = Store.Load().AdvanceReminder(DateTimeOffset.UtcNow);
        Store.Save(state);
        return Succeeded(request, state.GetReminderState().ToJson(DateTimeOffset.UtcNow).ToJsonString());
    }

    private CommandExecutionResult ReminderStart(CommandRequest request)
    {
        var state = Store.Load();
        state = state with
        {
            ReminderState = ScreenEaseReminderRuntime.Start(state.GetReminder(), DateTimeOffset.UtcNow),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        Store.Save(state);
        return Succeeded(request, state.GetReminderState().ToJson(DateTimeOffset.UtcNow).ToJsonString());
    }

    private CommandExecutionResult ReminderPause(CommandRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var state = Store.Load().AdvanceReminder(now);
        state = state with { ReminderState = ScreenEaseReminderRuntime.Pause(state.GetReminderState(), now), UpdatedAt = now };
        Store.Save(state);
        return Succeeded(request, state.GetReminderState().ToJson(now).ToJsonString());
    }

    private CommandExecutionResult ReminderResume(CommandRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var state = Store.Load();
        state = state with { ReminderState = ScreenEaseReminderRuntime.Resume(state.GetReminderState(), now), UpdatedAt = now };
        Store.Save(state);
        return Succeeded(request, state.GetReminderState().ToJson(now).ToJsonString());
    }

    private CommandExecutionResult ReminderReset(CommandRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var state = Store.Load() with { ReminderState = ScreenEaseReminderRuntime.Stopped(), UpdatedAt = now };
        Store.Save(state);
        return Succeeded(request, state.GetReminderState().ToJson(now).ToJsonString());
    }

    private async Task<JsonObject> BuildStatusPayloadAsync(CancellationToken cancellationToken)
    {
        var state = await RefreshDynamicStateCoreAsync(cancellationToken).ConfigureAwait(false);
        var displays = await Display.ListDisplaysAsync(cancellationToken);
        var writer = await Display.GetWriterStatusAsync(cancellationToken);
        var overlay = await Overlay.GetStateAsync(cancellationToken).ConfigureAwait(false);
        var payload = new JsonObject
        {
            ["moduleId"] = Id,
            ["activeProfileId"] = state.ActiveProfileId,
            ["activeProfile"] = state.FindProfile(state.ActiveProfileId)?.ToJson(),
            ["displayCount"] = displays.Count,
            ["displays"] = DisplayListJson(displays)["displays"]!.DeepClone(),
            ["profiles"] = state.ProfilesJson()["profiles"]!.DeepClone(),
            ["rules"] = state.RulesJson()["rules"]!.DeepClone(),
            ["effect"] = state.GetEffect().ToJson(),
            ["nativeHost"] = BuildNativeHostJson(state.NativeHost, writer, null, false),
            ["reminder"] = state.GetReminder().ToJson(),
            ["reminderState"] = state.GetReminderState().ToJson(DateTimeOffset.UtcNow),
            ["schedule"] = state.GetSchedule().ToJson(),
            ["isNight"] = state.GetSchedule().IsNight(DateTimeOffset.Now),
            ["advanced"] = state.GetAdvanced().ToJson(),
            ["overlay"] = BuildOverlayPayload(state.GetOverlay(), overlay)
        };
        payload["stateRecovery"] = Store.LastRecoveryMessage;
        return payload;
    }

    private async Task<JsonObject> BuildNativeWriterPayloadAsync(CancellationToken cancellationToken)
    {
        var state = Store.Load();
        var writer = await Display.GetWriterStatusAsync(cancellationToken);
        return BuildNativeHostJson(state.NativeHost, writer, null, false);
    }

    private async Task<ScreenEaseState> RefreshDynamicStateCoreAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var original = Store.Load();
        var state = original.AdvanceReminder(now.ToUniversalTime());
        var effect = state.GetEffect();
        if (effect.Enabled && state.FindProfile(effect.ProfileId) is { } profile)
        {
            var values = profile.ResolveValues(state.GetSchedule(), now);
            if (values.IsNightValue != effect.IsNightValue)
            {
                effect = effect with
                {
                    BrightnessPercent = values.Brightness,
                    ColorTemperatureKelvin = values.ColorTemperature,
                    IsNightValue = values.IsNightValue,
                    AppliedAt = now
                };
                state = state with { Effect = effect, UpdatedAt = now };

                var writer = await Display.GetWriterStatusAsync(cancellationToken);
                if (writer.Available)
                {
                    var apply = await Display.ApplyProfileAsync(
                        new DisplayProfileIntent(
                            profile.Id,
                            "all",
                            values.Brightness,
                            values.ColorTemperature,
                            "ScreenEase scheduled day/night transition"),
                        cancellationToken);
                    _hardwareResetRequired = true;
                    _disposeResetCompleted = false;
                    state = state with
                    {
                        NativeHost = state.NativeHost with { Available = writer.Available, Message = apply.Message }
                    };
                }
            }
        }

        if (state != original)
        {
            Store.Save(state);
        }

        await RefreshOverlayCoreAsync(state, cancellationToken, force: false).ConfigureAwait(false);

        return state;
    }

    private async Task<ScreenEaseOverlayState> RefreshOverlayCoreAsync(
        ScreenEaseState state,
        CancellationToken cancellationToken,
        bool force)
    {
        var settings = state.GetOverlay();
        var displays = await Display.ListDisplaysAsync(cancellationToken).ConfigureAwait(false);
        var fingerprint = $"{settings.Enabled}:{settings.OpacityPercent}:{settings.ColorHex}|{string.Join(';', displays.Select(display => $"{display.Id}:{display.State}:{display.Detail}"))}";
        if (!force && string.Equals(fingerprint, _overlayInventoryFingerprint, StringComparison.Ordinal))
        {
            return await Overlay.GetStateAsync(cancellationToken).ConfigureAwait(false);
        }

        _overlayInventoryFingerprint = fingerprint;
        try
        {
            return settings.Enabled
                ? await Overlay.ApplyAsync(settings, cancellationToken).ConfigureAwait(false)
                : await Overlay.HideAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
        {
            return new ScreenEaseOverlayState(
                settings.Enabled,
                settings.OpacityPercent,
                settings.ColorHex,
                0,
                "failed",
                exception.Message);
        }
    }

    private static JsonObject BuildOverlayPayload(
        ScreenEaseOverlaySettings settings,
        ScreenEaseOverlayState runtime) => new()
    {
        ["settings"] = settings.ToJson(),
        ["runtime"] = runtime.ToJson()
    };

    private async Task<bool> SyncHotkeysCoreAsync(ScreenEaseState state, CancellationToken cancellationToken)
    {
        if (_context?.TryGetCapability<IModuleHotkeyConfigurationService>("runtime.hotkeys", out var hotkeys) != true)
        {
            return false;
        }

        await hotkeys.ApplyAsync(
            state.GetHotkeys()
                .Select(binding => new ModuleHotkeyConfiguration(binding.Id, binding.Gesture, binding.Enabled))
                .ToArray(),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task SyncPendingHotkeysCoreAsync(CancellationToken cancellationToken)
    {
        var state = Store.Load();
        if (!state.HotkeysNeedSync)
        {
            return;
        }

        if (await SyncHotkeysCoreAsync(state, cancellationToken).ConfigureAwait(false))
        {
            Store.Save(state with { HotkeysNeedSync = false, UpdatedAt = DateTimeOffset.UtcNow });
        }
    }

    private async Task ReapplyPersistedEffectCoreAsync(CancellationToken cancellationToken)
    {
        var state = Store.Load();
        var effect = state.GetEffect();
        var writer = await Display.GetWriterStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!effect.Enabled)
        {
            if (writer.Available && Display is IScreenEaseDisplayResetService resetService)
            {
                var reset = await resetService.ResetAsync(cancellationToken).ConfigureAwait(false);
                state = state with
                {
                    NativeHost = state.NativeHost with { Available = writer.Available, Message = reset.Message },
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                Store.Save(state);
            }
            return;
        }

        if (!writer.Available)
        {
            Store.Save(state with
            {
                NativeHost = state.NativeHost with { Enabled = true, Available = writer.Available, Message = writer.Message },
                UpdatedAt = DateTimeOffset.UtcNow
            });
            return;
        }

        var apply = await Display.ApplyProfileAsync(
            new DisplayProfileIntent(
                effect.ProfileId,
                "all",
                effect.BrightnessPercent,
                effect.ColorTemperatureKelvin,
                "Restore persisted ScreenEase effect during module startup"),
            cancellationToken).ConfigureAwait(false);
        _hardwareResetRequired = true;
        Store.Save(state with
        {
            NativeHost = state.NativeHost with { Available = writer.Available, Message = apply.Message },
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private async Task<ScreenEaseState> RefreshDynamicStateWithGateAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await RefreshDynamicStateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<ScreenEaseState> LoadStateWithGateAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return Store.Load();
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static JsonObject BuildNativeHostJson(
        ScreenEaseNativeHostState configured,
        DisplayWriterStatus writer,
        BrokerOperationResult? applyResult,
        bool hardwareWriteRequested)
    {
        return new JsonObject
        {
            ["enabled"] = true,
            ["available"] = writer.Available,
            ["state"] = applyResult?.State ?? writer.State,
            ["success"] = applyResult?.Success,
            ["hardwareWriteRequested"] = hardwareWriteRequested,
            ["message"] = applyResult?.Message ?? NativeWriterMessage(configured, writer)
        };
    }

    private static string NativeWriterMessage(ScreenEaseNativeHostState configured, DisplayWriterStatus writer)
    {
        return writer.Available
            ? writer.Message
            : $"Native display writer is unavailable: {writer.Message}";
    }

    private static JsonObject BuildPlan(
        ScreenEaseState state,
        ScreenEaseProfile profile,
        IReadOnlyList<DisplaySnapshot> displays,
        string displayId = "all")
    {
        var values = profile.ResolveValues(state.GetSchedule(), DateTimeOffset.Now);
        var actions = new JsonArray();
        var targets = displays.Where(display =>
            IsConnectedDisplay(display) &&
            (string.Equals(displayId, "all", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(display.Id, displayId, StringComparison.OrdinalIgnoreCase)));
        foreach (var display in targets)
        {
            actions.Add(new JsonObject
            {
                ["displayId"] = display.Id,
                ["displayName"] = display.Name,
                ["profileId"] = profile.Id,
                ["brightness"] = values.Brightness,
                ["colorTemperature"] = values.ColorTemperature,
                ["isNightValue"] = values.IsNightValue,
                ["nativeAction"] = "screen-profile.apply"
            });
        }

        return new JsonObject
        {
            ["activeProfileId"] = state.ActiveProfileId,
            ["profile"] = profile.ToJson(),
            ["targetDisplayId"] = displayId,
            ["isNightValue"] = values.IsNightValue,
            ["displayCount"] = displays.Count,
            ["expectedChange"] = new JsonObject
            {
                ["actions"] = actions
            },
            ["rules"] = state.RulesJson()["rules"]!.DeepClone()
        };
    }

    private static JsonObject DisplayListJson(IReadOnlyList<DisplaySnapshot> displays)
    {
        var array = new JsonArray();
        foreach (var display in displays)
        {
            array.Add(new JsonObject
            {
                ["id"] = display.Id,
                ["name"] = display.Name,
                ["state"] = display.State,
                ["width"] = display.Width,
                ["height"] = display.Height,
                ["refreshRateHz"] = display.RefreshRateHz,
                ["orientation"] = display.Orientation,
                ["primary"] = display.Primary,
                ["detail"] = display.Detail
            });
        }

        return new JsonObject
        {
            ["displayCount"] = displays.Count,
            ["displays"] = array
        };
    }

    private static bool IsConnectedDisplay(DisplaySnapshot display)
    {
        return display.Width > 0 &&
               display.Height > 0 &&
               display.State.Trim().ToLowerInvariant() is "connected" or "ready" or "available";
    }

    private MptCommandDescriptor Command(string id, string title, string subtitle, IReadOnlyList<CommandParameterDescriptor>? parameters = null)
    {
        return new MptCommandDescriptor(id, Id, title, subtitle, "action", Category: "ScreenEase", Execution: new JsonObject { ["type"] = "module.execute" }, Parameters: parameters);
    }

    private static IReadOnlyList<CommandParameterDescriptor> ProfileParameters(bool includeHardwareWrite)
    {
        var parameters = new List<CommandParameterDescriptor>
        {
            new("profileId", "Profile ID", "text", false, ""),
            new("displayId", "Display ID", "text", false, "all")
        };
        if (includeHardwareWrite)
        {
            parameters.Add(new CommandParameterDescriptor("hardwareWrite", "Hardware write", "boolean", false, "false"));
        }

        return parameters;
    }

    private static IReadOnlyList<CommandParameterDescriptor> EffectApplyParameters()
    {
        return
        [
            new CommandParameterDescriptor("profileId", "Profile ID", "text", false, ""),
            new CommandParameterDescriptor("colorTemperatureKelvin", "Color temperature", "number", false, "5000"),
            new CommandParameterDescriptor("brightnessPercent", "Brightness", "number", false, "85"),
            new CommandParameterDescriptor("displayId", "Display ID", "text", false, "all"),
            new CommandParameterDescriptor("hardwareWrite", "Hardware write", "boolean", false, "false")
        ];
    }

    private static IReadOnlyList<CommandParameterDescriptor> SaveProfileParameters()
    {
        return
        [
            new CommandParameterDescriptor("id", "Profile ID", "text", true, ""),
            new CommandParameterDescriptor("name", "Name", "text", true, ""),
            new CommandParameterDescriptor("brightness", "Brightness", "number", false, "70"),
            new CommandParameterDescriptor("colorTemperature", "Color temperature", "number", false, "5200"),
            new CommandParameterDescriptor("nightBrightness", "Night brightness", "number", false, "70"),
            new CommandParameterDescriptor("nightColorTemperature", "Night color temperature", "number", false, "4200")
        ];
    }

    private static IReadOnlyList<CommandParameterDescriptor> ScheduleParameters()
    {
        return
        [
            new CommandParameterDescriptor("useNightValues", "Use night values", "boolean", false, "true"),
            new CommandParameterDescriptor("useSchedule", "Use schedule", "boolean", false, "false"),
            new CommandParameterDescriptor("sunrise", "Sunrise", "text", false, "07:00"),
            new CommandParameterDescriptor("sunset", "Sunset", "text", false, "19:00")
        ];
    }

    private static IReadOnlyList<CommandParameterDescriptor> OverlayParameters()
    {
        return
        [
            new CommandParameterDescriptor("enabled", "Enabled", "boolean", false, "false"),
            new CommandParameterDescriptor("opacityPercent", "Opacity", "number", false, "18"),
            new CommandParameterDescriptor("colorHex", "Color", "text", false, "#FFC98A")
        ];
    }

    private static CommandExecutionResult Succeeded(CommandRequest request, string output)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, output);
    }

    private static CommandExecutionResult Failed(CommandRequest request, string code, string message)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "failed", false, "", new MptRuntimeError(code, message));
    }

    private static string? ReadString(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool? ReadBool(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int? ReadInt(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            return int.TryParse(node.ToString(), out var value) ? value : null;
        }
    }

    private static IDisplayService CreateDisplayService(ModuleContext context)
    {
        if (context.TryGetCapability<IDisplayService>("display.profile", out var display))
        {
            return OperatingSystem.IsWindows() && display is INativeDisplayInventoryService
                ? new ScreenEaseWindowsGammaDisplayService(display)
                : display;
        }

        var unsupported = new UnsupportedDisplayService(
            "display.profile",
            "No display capability provider was injected by the host runtime.");
        return OperatingSystem.IsWindows()
            ? new ScreenEaseWindowsGammaDisplayService(unsupported)
            : unsupported;
    }

    private static bool IsTemporaryDataDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        return fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class ScreenEaseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string? _legacySettingsPath;
    private readonly string? _recoveryLogPath;
    private readonly object _gate = new();
    private string _lastRecoveryMessage = "";

    public ScreenEaseStore(string path, string? legacySettingsPath = null, string? recoveryLogPath = null)
    {
        _path = path;
        _legacySettingsPath = legacySettingsPath;
        _recoveryLogPath = recoveryLogPath;
    }

    public string LastRecoveryMessage
    {
        get
        {
            lock (_gate)
            {
                return _lastRecoveryMessage;
            }
        }
    }

    public void EnsureDefaults()
    {
        if (!File.Exists(_path))
        {
            Save(ScreenEaseLegacySettingsImporter.TryImportFile(_legacySettingsPath, out var imported)
                ? imported
                : ScreenEaseState.Default());
            return;
        }

        var state = Load();
        var usesGeneratedProfileIds = state.Profiles.Any(profile =>
            profile.Id is "day" or "reading" or "clarity" or "media" or "focus" or "night" or "custom");
        if (usesGeneratedProfileIds &&
            ScreenEaseLegacySettingsImporter.TryImportFile(_legacySettingsPath, out var legacyState))
        {
            Save(legacyState);
            return;
        }

        var defaults = ScreenEaseState.Default();
        var sourceProfiles = state.Profiles.ToList();
        var migrateLegacyActiveProfile = sourceProfiles.Count == 3 &&
            sourceProfiles.All(IsLegacyDefault) &&
            string.Equals(state.ActiveProfileId, "day", StringComparison.OrdinalIgnoreCase);
        var profiles = sourceProfiles
            .Select(profile => profile with { Id = ScreenEaseProfileIds.Normalize(profile.Id) })
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        var migrateEffect = state.Effect is null;
        var migrateExtendedSettings = state.Advanced is null || state.Overlay is null || state.Hotkeys is null;
        var migrateLegacyWriterDefault = !state.NativeHost.Enabled;
        var changed = sourceProfiles.Count != profiles.Count || sourceProfiles
            .Zip(profiles, (source, normalized) => !string.Equals(source.Id, normalized.Id, StringComparison.OrdinalIgnoreCase))
            .Any(item => item);
        foreach (var builtIn in defaults.Profiles)
        {
            var index = profiles.FindIndex(profile =>
                string.Equals(profile.Id, builtIn.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                profiles.Add(builtIn);
                changed = true;
                continue;
            }

            var existing = profiles[index];
            if (IsLegacyDefault(existing))
            {
                profiles[index] = builtIn;
                changed = true;
                continue;
            }

            if (existing.NightBrightness < 0 || existing.NightColorTemperature < 0)
            {
                profiles[index] = existing with
                {
                    NightBrightness = builtIn.EffectiveNightBrightness,
                    NightColorTemperature = builtIn.EffectiveNightColorTemperature
                };
                changed = true;
            }
        }

        if (changed || migrateLegacyActiveProfile || migrateEffect || migrateLegacyWriterDefault || migrateExtendedSettings)
        {
            var activeProfileId = migrateLegacyActiveProfile
                ? defaults.ActiveProfileId
                : ScreenEaseProfileIds.Normalize(state.ActiveProfileId);
            var effect = (state.Effect ?? state.GetEffect()) with
            {
                ProfileId = ScreenEaseProfileIds.Normalize((state.Effect ?? state.GetEffect()).ProfileId)
            };
            Save(state with
            {
                ActiveProfileId = activeProfileId,
                Profiles = profiles,
                Rules = state.Rules
                    .Select(rule => rule with { ProfileId = ScreenEaseProfileIds.Normalize(rule.ProfileId) })
                    .ToArray(),
                Effect = effect,
                Advanced = state.GetAdvanced(),
                Overlay = state.GetOverlay(),
                Hotkeys = state.GetHotkeys(),
                NativeHost = migrateLegacyWriterDefault
                    ? state.NativeHost with { Enabled = true, Message = "ScreenEase gamma-ramp hardware writes are enabled when the local display session supports them." }
                    : state.NativeHost,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private static bool IsLegacyDefault(ScreenEaseProfile profile)
    {
        return profile switch
        {
            { Id: "day", Name: "Day", Brightness: 85, ColorTemperature: 6500 } => true,
            { Id: "day-office", Name: "Day", Brightness: 85, ColorTemperature: 6500 } => true,
            { Id: "night", Name: "Night", Brightness: 45, ColorTemperature: 4200 } => true,
            { Id: "low-blue-evening", Name: "Night", Brightness: 45, ColorTemperature: 4200 } => true,
            { Id: "focus", Name: "Focus", Brightness: 70, ColorTemperature: 5200 } => true,
            { Id: "bright-focus", Name: "Focus", Brightness: 70, ColorTemperature: 5200 } => true,
            _ => false
        };
    }

    public ScreenEaseState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return ScreenEaseState.Default();
            }

            if (TryReadState(_path, out var state, out var error))
            {
                return state!;
            }

            var quarantinePath = QuarantineCorruptState();
            var backupPath = _path + ".bak";
            if (TryReadState(backupPath, out var recovered, out _))
            {
                File.Copy(backupPath, _path, overwrite: true);
                _lastRecoveryMessage = $"Recovered ScreenEase settings from backup after quarantining a damaged state file as '{Path.GetFileName(quarantinePath)}'.";
                LogRecovery(_lastRecoveryMessage, error);
                return recovered!;
            }

            var defaults = ScreenEaseState.Default();
            WriteStateCore(defaults, createBackup: false);
            _lastRecoveryMessage = $"ScreenEase settings were damaged and no valid backup existed. The damaged file was preserved as '{Path.GetFileName(quarantinePath)}' and source defaults were restored.";
            LogRecovery(_lastRecoveryMessage, error);
            return defaults;
        }
    }

    public void Save(ScreenEaseState state)
    {
        lock (_gate)
        {
            WriteStateCore(state, createBackup: true);
        }
    }

    private void WriteStateCore(ScreenEaseState state, bool createBackup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (createBackup && TryReadState(_path, out _, out _))
        {
            File.Copy(_path, _path + ".bak", overwrite: true);
        }

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Move(tmp, _path, overwrite: true);
                return;
            }
            catch (Exception) when (attempt < 4)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * (attempt + 1)));
            }
        }

        File.Move(tmp, _path, overwrite: true);
    }

    private static bool TryReadState(string path, out ScreenEaseState? state, out Exception? error)
    {
        state = null;
        error = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            state = JsonSerializer.Deserialize<ScreenEaseState>(File.ReadAllText(path), JsonOptions);
            return state is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            error = exception;
            return false;
        }
    }

    private string QuarantineCorruptState()
    {
        var quarantinePath = $"{_path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json";
        try
        {
            File.Move(_path, quarantinePath);
            return quarantinePath;
        }
        catch (IOException)
        {
            File.Copy(_path, quarantinePath, overwrite: false);
            return quarantinePath;
        }
    }

    private void LogRecovery(string message, Exception? error)
    {
        if (string.IsNullOrWhiteSpace(_recoveryLogPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_recoveryLogPath)!);
            File.AppendAllText(
                _recoveryLogPath,
                $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}{error?.GetType().Name}: {error?.Message}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Recovery remains successful even if the diagnostic log location is unavailable.
        }
    }
}

internal sealed record ScreenEaseState(
    string ActiveProfileId,
    IReadOnlyList<ScreenEaseProfile> Profiles,
    IReadOnlyList<ScreenEaseRule> Rules,
    ScreenEaseNativeHostState NativeHost,
    DateTimeOffset UpdatedAt,
    ScreenEaseReminderSettings? Reminder = null,
    ScreenEaseScheduleSettings? Schedule = null,
    ScreenEaseReminderRuntime? ReminderState = null,
    ScreenEaseDisplayEffect? Effect = null,
    ScreenEaseAdvancedSettings? Advanced = null,
    ScreenEaseOverlaySettings? Overlay = null,
    IReadOnlyList<ScreenEaseHotkeyBinding>? Hotkeys = null,
    bool HotkeysNeedSync = false)
{
    public static ScreenEaseState Default()
    {
        return new ScreenEaseState(
            "low-blue-evening",
            [
                new ScreenEaseProfile("day-office", "日间办公", 100, 6500, 90, 5000),
                new ScreenEaseProfile("long-read", "长读柔光", 85, 5000, 75, 4200),
                new ScreenEaseProfile("detail-work", "细节清晰", 90, 6500, 85, 5000),
                new ScreenEaseProfile("warm-video", "影音暖光", 85, 4500, 75, 3700),
                new ScreenEaseProfile("bright-focus", "高亮专注", 95, 6500, 85, 5000),
                new ScreenEaseProfile("low-blue-evening", "夜间低蓝", 75, 3700, 65, 3200),
                new ScreenEaseProfile("personal", "我的方案", 85, 5000, 75, 4200)
            ],
            [
                new ScreenEaseRule("evening", "low-blue-evening", true, "local-time >= sunset"),
                new ScreenEaseRule("morning", "day-office", true, "local-time >= sunrise")
            ],
            new ScreenEaseNativeHostState(true, false, "ScreenEase gamma-ramp hardware writes are enabled when the local display session supports them."),
            DateTimeOffset.UtcNow,
            ScreenEaseReminderSettings.Default(),
            ScreenEaseScheduleSettings.Default(),
            ScreenEaseReminderRuntime.Stopped(),
            new ScreenEaseDisplayEffect(false, "low-blue-evening", 3700, 75, false, DateTimeOffset.Now),
            ScreenEaseAdvancedSettings.Default(),
            ScreenEaseOverlaySettings.Default(),
            ScreenEaseHotkeyBinding.Defaults(),
            false);
    }

    public static ScreenEaseState FromSettings(
        JsonObject values,
        ScreenEaseReminderRuntime? reminderState = null,
        ScreenEaseDisplayEffect? currentEffect = null)
    {
        var defaults = Default();
        var profiles = values.TryGetPropertyValue("profiles", out var profilesNode) && profilesNode is JsonArray profilesArray
            ? profilesArray
                .OfType<JsonObject>()
                .Select(ScreenEaseProfile.FromJson)
                .Where(profile => profile.Validate().Count == 0)
                .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToArray()
            : defaults.Profiles;
        if (profiles.Count == 0)
        {
            profiles = defaults.Profiles;
        }

        var rules = values.TryGetPropertyValue("rules", out var rulesNode) && rulesNode is JsonArray rulesArray
            ? rulesArray.OfType<JsonObject>().Select(ScreenEaseRule.FromJson).ToArray()
            : defaults.Rules;
        var activeProfileId = ScreenEaseProfileIds.Normalize(
            SettingsJson.ReadString(values, "activeProfileId") ?? defaults.ActiveProfileId);
        if (!profiles.Any(profile => string.Equals(profile.Id, activeProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            activeProfileId = profiles[0].Id;
        }

        var nativeHost = values.TryGetPropertyValue("nativeHost", out var nativeHostNode) && nativeHostNode is JsonObject nativeHostObject
            ? ScreenEaseNativeHostState.FromJson(nativeHostObject)
            : defaults.NativeHost;
        var reminder = values.TryGetPropertyValue("reminder", out var reminderNode) && reminderNode is JsonObject reminderObject
            ? ScreenEaseReminderSettings.FromJson(reminderObject)
            : defaults.GetReminder();
        var schedule = values.TryGetPropertyValue("schedule", out var scheduleNode) && scheduleNode is JsonObject scheduleObject
            ? ScreenEaseScheduleSettings.FromJson(scheduleObject)
            : defaults.GetSchedule();
        var effect = values.TryGetPropertyValue("effect", out var effectNode) && effectNode is JsonObject effectObject
            ? ScreenEaseDisplayEffect.FromJson(effectObject)
            : currentEffect ?? defaults.GetEffect();
        effect = effect with { ProfileId = ScreenEaseProfileIds.Normalize(effect.ProfileId) };
        if (!profiles.Any(profile => string.Equals(profile.Id, effect.ProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            effect = effect with { ProfileId = activeProfileId };
        }
        var advanced = values.TryGetPropertyValue("advanced", out var advancedNode) && advancedNode is JsonObject advancedObject
            ? ScreenEaseAdvancedSettings.FromJson(advancedObject)
            : defaults.GetAdvanced();
        var overlay = values.TryGetPropertyValue("overlay", out var overlayNode) && overlayNode is JsonObject overlayObject
            ? ScreenEaseOverlaySettings.FromJson(overlayObject)
            : defaults.GetOverlay();
        var hotkeys = values.TryGetPropertyValue("hotkeys", out var hotkeysNode) && hotkeysNode is JsonArray hotkeyArray
            ? ScreenEaseHotkeyBinding.Normalize(hotkeyArray.OfType<JsonObject>().Select(ParseHotkey))
            : defaults.GetHotkeys();

        return new ScreenEaseState(
            activeProfileId,
            profiles,
            rules,
            nativeHost,
            DateTimeOffset.UtcNow,
            reminder,
            schedule,
            reminderState ?? ScreenEaseReminderRuntime.Stopped(),
            effect,
            advanced,
            overlay,
            hotkeys);
    }

    public ScreenEaseProfile? FindProfile(string id)
    {
        var normalized = ScreenEaseProfileIds.Normalize(id);
        return Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public SettingsSnapshotDocument ToSettingsSnapshot(string moduleId)
    {
        return new SettingsSnapshotDocument(moduleId, 1, new JsonObject
        {
            ["activeProfileId"] = ActiveProfileId,
            ["profiles"] = ProfilesJson()["profiles"]!.DeepClone(),
            ["rules"] = RulesJson()["rules"]!.DeepClone(),
            ["nativeHost"] = NativeHost.ToJson(),
            ["effect"] = GetEffect().ToJson(),
            ["reminder"] = GetReminder().ToJson(),
            ["schedule"] = GetSchedule().ToJson(),
            ["advanced"] = GetAdvanced().ToJson(),
            ["overlay"] = GetOverlay().ToJson(),
            ["hotkeys"] = new JsonArray(GetHotkeys().Select(hotkey => (JsonNode)hotkey.ToJson()).ToArray())
        }, UpdatedAt);
    }

    public ScreenEaseReminderSettings GetReminder()
    {
        return Reminder ?? ScreenEaseReminderSettings.Default();
    }

    public ScreenEaseScheduleSettings GetSchedule()
    {
        return Schedule ?? ScreenEaseScheduleSettings.Default();
    }

    public ScreenEaseReminderRuntime GetReminderState()
    {
        return ReminderState ?? ScreenEaseReminderRuntime.Stopped();
    }

    public ScreenEaseDisplayEffect GetEffect()
    {
        if (Effect is not null)
        {
            return Effect;
        }

        var profile = FindProfile(ActiveProfileId) ?? Profiles.First();
        var values = profile.ResolveValues(GetSchedule(), DateTimeOffset.Now);
        return new ScreenEaseDisplayEffect(
            false,
            profile.Id,
            values.ColorTemperature,
            values.Brightness,
            values.IsNightValue,
            UpdatedAt);
    }

    public ScreenEaseAdvancedSettings GetAdvanced() => Advanced ?? ScreenEaseAdvancedSettings.Default();

    public ScreenEaseOverlaySettings GetOverlay() => Overlay ?? ScreenEaseOverlaySettings.Default();

    public IReadOnlyList<ScreenEaseHotkeyBinding> GetHotkeys() => ScreenEaseHotkeyBinding.Normalize(Hotkeys);

    private static ScreenEaseHotkeyBinding ParseHotkey(JsonObject node) => new(
        SettingsJson.ReadString(node, "id") ?? "",
        SettingsJson.ReadString(node, "gesture") ?? "",
        SettingsJson.ReadBool(node, "enabled") ?? false);

    public ScreenEaseState AdvanceReminder(DateTimeOffset now)
    {
        var next = ScreenEaseReminderRuntime.Tick(GetReminderState(), GetReminder(), now);
        return next == GetReminderState()
            ? this
            : this with { ReminderState = next, UpdatedAt = now };
    }

    public JsonObject ProfilesJson()
    {
        var array = new JsonArray();
        foreach (var profile in Profiles)
        {
            array.Add(profile.ToJson());
        }

        return new JsonObject
        {
            ["activeProfileId"] = ActiveProfileId,
            ["profiles"] = array
        };
    }

    public JsonObject RulesJson()
    {
        var array = new JsonArray();
        foreach (var rule in Rules)
        {
            array.Add(rule.ToJson(FindProfile(rule.ProfileId)?.Name ?? ""));
        }

        return new JsonObject
        {
            ["rules"] = array
        };
    }
}

internal sealed record ScreenEaseProfile(
    string Id,
    string Name,
    int Brightness,
    int ColorTemperature,
    int NightBrightness = -1,
    int NightColorTemperature = -1)
{
    public IReadOnlyList<string> Validate()
    {
        var messages = new List<string>();
        if (string.IsNullOrWhiteSpace(Id))
        {
            messages.Add("profile id is required.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            messages.Add("profile name is required.");
        }

        if (Brightness is < 1 or > 150)
        {
            messages.Add("brightness must be between 1 and 150.");
        }

        if (ColorTemperature is < 1000 or > 10000)
        {
            messages.Add("colorTemperature must be between 1000 and 10000.");
        }

        if (EffectiveNightBrightness is < 1 or > 150)
        {
            messages.Add("nightBrightness must be between 1 and 150.");
        }

        if (EffectiveNightColorTemperature is < 1000 or > 10000)
        {
            messages.Add("nightColorTemperature must be between 1000 and 10000.");
        }

        return messages;
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["id"] = Id,
            ["name"] = Name,
            ["brightness"] = Brightness,
            ["colorTemperature"] = ColorTemperature,
            ["nightBrightness"] = EffectiveNightBrightness,
            ["nightColorTemperature"] = EffectiveNightColorTemperature
        };
    }

    public static ScreenEaseProfile FromJson(JsonObject node)
    {
        return new ScreenEaseProfile(
            ScreenEaseProfileIds.Normalize(ReadString(node, "id")),
            ReadString(node, "name") ?? ReadString(node, "id") ?? "",
            ReadInt(node, "brightness") ?? 70,
            ReadInt(node, "colorTemperature") ?? 5200,
            ReadInt(node, "nightBrightness") ?? ReadInt(node, "brightness") ?? 70,
            ReadInt(node, "nightColorTemperature") ?? ReadInt(node, "colorTemperature") ?? 5200);
    }

    public int EffectiveNightBrightness => NightBrightness < 0 ? Brightness : NightBrightness;

    public int EffectiveNightColorTemperature => NightColorTemperature < 0 ? ColorTemperature : NightColorTemperature;

    public ScreenEaseProfileValues ResolveValues(ScreenEaseScheduleSettings schedule, DateTimeOffset now)
    {
        var useNight = schedule.UseNightValues && schedule.UseSchedule && schedule.IsNight(now);
        return useNight
            ? new ScreenEaseProfileValues(EffectiveNightBrightness, EffectiveNightColorTemperature, true)
            : new ScreenEaseProfileValues(Brightness, ColorTemperature, false);
    }

    private static string? ReadString(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int? ReadInt(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            try
            {
                return checked((int)node.GetValue<long>());
            }
            catch
            {
                return null;
            }
        }
    }
}

internal static class ScreenEaseProfileIds
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "personal";
        }

        var id = value.Trim().ToLowerInvariant();
        return id switch
        {
            "day" or "office" => "day-office",
            "reading" or "read" => "long-read",
            "clarity" or "editing" or "edit" => "detail-work",
            "media" or "movie" => "warm-video",
            "focus" or "game" => "bright-focus",
            "night" or "health" => "low-blue-evening",
            "custom" => "personal",
            "manual" => "manual-adjustment",
            _ => id
        };
    }
}

internal sealed record ScreenEaseRule(string Id, string ProfileId, bool Enabled, string Condition)
{
    public static ScreenEaseRule FromJson(JsonObject node)
    {
        return new ScreenEaseRule(
            SettingsJson.ReadString(node, "id") ?? "",
            ScreenEaseProfileIds.Normalize(SettingsJson.ReadString(node, "profileId")),
            true,
            SettingsJson.ReadString(node, "condition") ?? "");
    }

    public JsonObject ToJson(string profileName)
    {
        return new JsonObject
        {
            ["id"] = Id,
            ["profileId"] = ProfileId,
            ["profileName"] = profileName,
            ["enabled"] = Enabled,
            ["condition"] = Condition,
            ["state"] = Enabled ? "ready" : "disabled"
        };
    }
}

internal sealed record ScreenEaseNativeHostState(bool Enabled, bool Available, string Message)
{
    public static ScreenEaseNativeHostState FromJson(JsonObject node)
    {
        return new ScreenEaseNativeHostState(
            SettingsJson.ReadBool(node, "enabled") ?? true,
            SettingsJson.ReadBool(node, "available") ?? false,
            SettingsJson.ReadString(node, "message") ?? "ScreenEase native host is pending; state/profile management is available.");
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["enabled"] = Enabled,
            ["available"] = Available,
            ["state"] = Available ? "ready" : "native-host-required",
            ["message"] = Message
        };
    }
}

internal sealed record ScreenEaseDisplayEffect(
    bool Enabled,
    string ProfileId,
    int ColorTemperatureKelvin,
    int BrightnessPercent,
    bool IsNightValue,
    DateTimeOffset AppliedAt)
{
    public static ScreenEaseDisplayEffect FromJson(JsonObject node)
    {
        return new ScreenEaseDisplayEffect(
            SettingsJson.ReadBool(node, "enabled") ?? false,
            ScreenEaseProfileIds.Normalize(SettingsJson.ReadString(node, "profileId") ?? "low-blue-evening"),
            Math.Clamp(SettingsJson.ReadInt(node, "colorTemperatureKelvin") ?? 3700, 1000, 10000),
            Math.Clamp(SettingsJson.ReadInt(node, "brightnessPercent") ?? 75, 1, 150),
            SettingsJson.ReadBool(node, "isNightValue") ?? false,
            ReadAppliedAt(node));
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["enabled"] = Enabled,
            ["profileId"] = ProfileId,
            ["colorTemperatureKelvin"] = ColorTemperatureKelvin,
            ["brightnessPercent"] = BrightnessPercent,
            ["isNightValue"] = IsNightValue,
            ["appliedAt"] = AppliedAt
        };
    }

    private static DateTimeOffset ReadAppliedAt(JsonObject node)
    {
        return DateTimeOffset.TryParse(SettingsJson.ReadString(node, "appliedAt"), out var appliedAt)
            ? appliedAt
            : DateTimeOffset.Now;
    }
}

internal sealed record ScreenEaseReminderSettings(
    bool Enabled,
    bool AutoStartNext,
    int FocusMinutes,
    int ShortBreakMinutes,
    int LongBreakMinutes,
    int LongBreakInterval)
{
    public static ScreenEaseReminderSettings Default()
    {
        return new ScreenEaseReminderSettings(false, false, 25, 5, 15, 4);
    }

    public static ScreenEaseReminderSettings FromJson(JsonObject node)
    {
        var defaults = Default();
        return new ScreenEaseReminderSettings(
            SettingsJson.ReadBool(node, "enabled") ?? defaults.Enabled,
            SettingsJson.ReadBool(node, "autoStartNext") ?? defaults.AutoStartNext,
            ReadBoundedInt(node, "focusMinutes", 1, 240, defaults.FocusMinutes),
            ReadBoundedInt(node, "shortBreakMinutes", 1, 120, defaults.ShortBreakMinutes),
            ReadBoundedInt(node, "longBreakMinutes", 1, 240, defaults.LongBreakMinutes),
            ReadBoundedInt(node, "longBreakInterval", 1, 12, defaults.LongBreakInterval));
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["enabled"] = Enabled,
            ["autoStartNext"] = AutoStartNext,
            ["focusMinutes"] = FocusMinutes,
            ["shortBreakMinutes"] = ShortBreakMinutes,
            ["longBreakMinutes"] = LongBreakMinutes,
            ["longBreakInterval"] = LongBreakInterval
        };
    }

    private static int ReadBoundedInt(JsonObject node, string key, int minimum, int maximum, int fallback)
    {
        try
        {
            var value = node[key]?.GetValue<int>() ?? fallback;
            return Math.Clamp(value, minimum, maximum);
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }
}

internal sealed record ScreenEaseScheduleSettings(
    bool UseNightValues,
    bool UseSchedule,
    string Sunrise,
    string Sunset)
{
    public static ScreenEaseScheduleSettings Default() => new(true, false, "07:00", "19:00");

    public static ScreenEaseScheduleSettings FromJson(JsonObject node)
    {
        var defaults = Default();
        return new ScreenEaseScheduleSettings(
            SettingsJson.ReadBool(node, "useNightValues") ?? defaults.UseNightValues,
            SettingsJson.ReadBool(node, "useSchedule") ?? defaults.UseSchedule,
            NormalizeTime(SettingsJson.ReadString(node, "sunrise"), defaults.Sunrise),
            NormalizeTime(SettingsJson.ReadString(node, "sunset"), defaults.Sunset));
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["useNightValues"] = UseNightValues,
            ["useSchedule"] = UseSchedule,
            ["sunrise"] = Sunrise,
            ["sunset"] = Sunset
        };
    }

    public bool IsNight(DateTimeOffset now)
    {
        var sunrise = TimeOnly.ParseExact(Sunrise, "HH:mm");
        var sunset = TimeOnly.ParseExact(Sunset, "HH:mm");
        var current = TimeOnly.FromDateTime(now.LocalDateTime);
        return sunset < sunrise
            ? current >= sunset && current < sunrise
            : current >= sunset || current < sunrise;
    }

    private static string NormalizeTime(string? value, string fallback)
    {
        return TimeOnly.TryParse(value, out var parsed)
            ? parsed.ToString("HH:mm")
            : fallback;
    }
}

internal sealed record ScreenEaseReminderRuntime(
    string Phase,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndsAt,
    int? PausedRemainingSeconds,
    string? PausedFrom,
    int CompletedWorkSessions)
{
    public const string StoppedPhase = "stopped";
    public const string WorkPhase = "work";
    public const string ShortBreakPhase = "short-break";
    public const string LongBreakPhase = "long-break";
    public const string PausedPhase = "paused";

    public static ScreenEaseReminderRuntime Stopped() => new(StoppedPhase, null, null, null, null, 0);

    public static ScreenEaseReminderRuntime Start(ScreenEaseReminderSettings settings, DateTimeOffset now) =>
        new(WorkPhase, now, now.AddMinutes(settings.FocusMinutes), null, null, 0);

    public static ScreenEaseReminderRuntime Pause(ScreenEaseReminderRuntime state, DateTimeOffset now)
    {
        if (state.Phase is StoppedPhase or PausedPhase)
        {
            return state;
        }

        var remaining = state.EndsAt is null
            ? 0
            : Math.Max(0, checked((int)Math.Ceiling((state.EndsAt.Value - now).TotalSeconds)));
        return state with
        {
            Phase = PausedPhase,
            EndsAt = null,
            PausedRemainingSeconds = remaining,
            PausedFrom = state.Phase
        };
    }

    public static ScreenEaseReminderRuntime Resume(ScreenEaseReminderRuntime state, DateTimeOffset now)
    {
        if (state.Phase != PausedPhase)
        {
            return state;
        }

        var remaining = Math.Max(0, state.PausedRemainingSeconds ?? 0);
        return state with
        {
            Phase = state.PausedFrom is WorkPhase or ShortBreakPhase or LongBreakPhase ? state.PausedFrom : WorkPhase,
            StartedAt = now,
            EndsAt = now.AddSeconds(remaining),
            PausedRemainingSeconds = null,
            PausedFrom = null
        };
    }

    public static ScreenEaseReminderRuntime Tick(
        ScreenEaseReminderRuntime state,
        ScreenEaseReminderSettings settings,
        DateTimeOffset now)
    {
        if (!settings.Enabled)
        {
            return Stopped();
        }

        if (state.Phase == StoppedPhase)
        {
            return settings.AutoStartNext ? Start(settings, now) : state;
        }

        if (state.Phase == PausedPhase || state.EndsAt is null || now < state.EndsAt.Value)
        {
            return state;
        }

        if (state.Phase == WorkPhase)
        {
            var completed = state.CompletedWorkSessions + 1;
            var longBreak = completed % settings.LongBreakInterval == 0;
            var minutes = longBreak ? settings.LongBreakMinutes : settings.ShortBreakMinutes;
            return new ScreenEaseReminderRuntime(
                longBreak ? LongBreakPhase : ShortBreakPhase,
                now,
                now.AddMinutes(minutes),
                null,
                null,
                completed);
        }

        if (state.Phase is ShortBreakPhase or LongBreakPhase)
        {
            return new ScreenEaseReminderRuntime(
                WorkPhase,
                now,
                now.AddMinutes(settings.FocusMinutes),
                null,
                null,
                state.CompletedWorkSessions);
        }

        return state;
    }

    public JsonObject ToJson(DateTimeOffset now)
    {
        var remainingSeconds = Phase == PausedPhase
            ? Math.Max(0, PausedRemainingSeconds ?? 0)
            : EndsAt is null ? 0 : Math.Max(0, checked((int)Math.Ceiling((EndsAt.Value - now).TotalSeconds)));
        return new JsonObject
        {
            ["phase"] = Phase,
            ["startedAt"] = StartedAt,
            ["endsAt"] = EndsAt,
            ["pausedRemainingSeconds"] = PausedRemainingSeconds,
            ["pausedFrom"] = PausedFrom,
            ["completedWorkSessions"] = CompletedWorkSessions,
            ["remainingSeconds"] = remainingSeconds
        };
    }
}

internal sealed record ScreenEaseProfileValues(int Brightness, int ColorTemperature, bool IsNightValue);

