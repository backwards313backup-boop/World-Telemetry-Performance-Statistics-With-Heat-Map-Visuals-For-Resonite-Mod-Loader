using FrooxEngine;
using Renderite.Shared;
using ResoniteModLoader;

namespace WorldTelemetry;

public sealed class WorldTelemetryMod : ResoniteMod
{
    internal const string ModVersion = "1.0.0";

    internal static string ConfigPath => Path.Combine(Directory.GetCurrentDirectory(), "rml_config", Path.ChangeExtension(Path.GetFileName(typeof(WorldTelemetryMod).Assembly.Location), ".json"));

    public override string Name => "WorldTelemetry";
    public override string Author => "backwards";
    public override string Version => ModVersion;
    public override string Link => "https://github.com/backwards313backup-boop/";

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<int> KeyProfileSecondsKey = new("key_profile_seconds",
        "Seconds of CPU profiling before the Create log button writes the report. 0 writes it at once, without CPU times.",
        () => 3, valueValidator: value => value >= 0 && value <= 60);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<int> ProfileSecondsKey = new("profile_seconds",
        "Seconds of CPU profiling when the Profile CPU button on the dash is pressed.",
        () => 5, valueValidator: value => value >= 1 && value <= 60);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<bool> ShowDashScreenKey = new("show_dash_screen",
        "Adds the World Telemetry screen to the dash.",
        () => true);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<bool> ThumbnailsKey = new("thumbnails",
        "Renders a picture of each object shown on the dash screen.",
        () => true);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<int> RowsPerPageKey = new("max_items_per_page",
        "Rows shown on one page of the dash screen. The Max Items Per Page button cycles 25, 50, 75, 100, 125, 150.",
        () => 150, valueValidator: value => value >= 25 && value <= 150);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<bool> AutoScanKey = new("auto_scan",
        "Keeps measuring the focused world on its own. A CPU profile, then a scan, repeated. Also switched by the Auto scan button on the dash screen.",
        () => true);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<int> AutoScanSecondsKey = new("auto_scan_seconds",
        "Seconds between the end of one automatic scan and the start of the next.",
        () => 20, valueValidator: value => value >= 5 && value <= 3600);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<bool> AutoScanReportKey = new("auto_scan_report",
        "While auto scan is on, keeps one report file per world up to date in the Logs folder, named WorldTelemetry <world> latest.txt.",
        () => true);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<float> HeatmapMaximumSizeKey = new("heatmap_max_object_meters",
        "Objects wider or taller than this many meters get no heatmap color, so a whole map does not tint everything around you.",
        () => 50f, valueValidator: value => value >= 1f && value <= 100000f);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<bool> ShareSelectionKey = new("share_selection_box",
        "Shows the red selection box to everyone in the world, like the dev tool selection. When off, only you see it.",
        () => false);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<bool> SelectionBoxHiddenKey = new("selection_box_hidden",
        "Draws no selection box at all, for you or for anyone else. The Box button on the dash cycles everyone, only me and none.",
        () => true);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<int> LightTestMaximumKey = new("light_test_max_lights",
        "The most lights Measure lights tests in one run, heaviest looking first.",
        () => 40, valueValidator: value => value >= 1 && value <= 500);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<float> LightTestBlockSecondsKey = new("light_test_block_seconds",
        "Seconds per on or off block when measuring a light. Each light takes 5 blocks. Longer is steadier.",
        () => 0.3f, valueValidator: value => value >= 0.1f && value <= 5f);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<float> LightVolumeMaximumKey = new("light_volume_max_meters",
        "Lights whose range is larger than this many meters are drawn as a small marker in the Lighting heatmap, so a huge sphere does not tint everything.",
        () => 60f, valueValidator: value => value >= 1f && value <= 100000f);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<float> LightVolumeAlphaKey = new("light_volume_opacity",
        "Opacity of the light range shapes in the Lighting heatmap, from 0.02 to 0.8.",
        () => 0.16f, valueValidator: value => value >= 0.02f && value <= 0.8f);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<int> PhysicsTestMaximumKey = new("physics_test_max_objects",
        "The most objects Measure physics tests in one run, most complex colliders first.",
        () => 30, valueValidator: value => value >= 1 && value <= 500);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<float> PhysicsTestBlockSecondsKey = new("physics_test_block_seconds",
        "Seconds per on or off block when measuring an object's colliders. Each object takes 5 blocks.",
        () => 0.3f, valueValidator: value => value >= 0.1f && value <= 5f);

    private static ModConfiguration? _config;

    internal static int PhysicsTestMaximum => Math.Clamp(Get(PhysicsTestMaximumKey, 30), 1, 500);
    internal static float PhysicsTestBlockSeconds => Math.Clamp(Get(PhysicsTestBlockSecondsKey, 0.3f), 0.1f, 5f);

    internal static float LightVolumeMaximum => Math.Clamp(Get(LightVolumeMaximumKey, 60f), 1f, 100000f);
    internal static float LightVolumeAlpha => Math.Clamp(Get(LightVolumeAlphaKey, 0.16f), 0.02f, 0.8f);

    internal static bool ShareSelection => Get(ShareSelectionKey, false);
    internal static bool SelectionBoxHidden => Get(SelectionBoxHiddenKey, true);
    internal static int LightTestMaximum => Math.Clamp(Get(LightTestMaximumKey, 40), 1, 500);
    internal static float LightTestBlockSeconds => Math.Clamp(Get(LightTestBlockSecondsKey, 0.3f), 0.1f, 5f);

    internal static float HeatmapMaximumSize => Math.Clamp(Get(HeatmapMaximumSizeKey, 50f), 1f, 100000f);

    internal static bool AutoScan => Get(AutoScanKey, true);
    internal static int AutoScanSeconds => Math.Clamp(Get(AutoScanSecondsKey, 20), 5, 3600);
    internal static bool AutoScanReport => Get(AutoScanReportKey, true);

    internal static void SetAutoScan(bool enabled)
    {
        if (_config is null)
            return;

        try
        {
            _config.Set(AutoScanKey, enabled);
            _config.Save(true);
        }
        catch (Exception ex)
        {
            LogWarning("Could not save the auto_scan setting: " + ex.Message);
        }
    }

    internal static void CycleRowsPerPage()
    {
        if (_config is null)
            return;

        int next = (RowsPerPage / 25 + 1) * 25;
        if (next > 150)
            next = 25;

        try
        {
            _config.Set(RowsPerPageKey, next);
            _config.Save(true);
        }
        catch (Exception ex)
        {
            LogWarning("Could not save the max_items_per_page setting: " + ex.Message);
        }
    }

    internal static void CycleSelectionBox()
    {
        if (_config is null)
            return;

        bool hidden = SelectionBoxHidden;
        bool shared = ShareSelection;
        try
        {
            if (hidden)
            {
                _config.Set(SelectionBoxHiddenKey, false);
                _config.Set(ShareSelectionKey, true);
            }
            else if (shared)
                _config.Set(ShareSelectionKey, false);
            else
                _config.Set(SelectionBoxHiddenKey, true);

            _config.Save(true);
        }
        catch (Exception ex)
        {
            LogWarning("Could not save the selection box setting: " + ex.Message);
        }
    }

    internal static void SetShareSelection(bool enabled)
    {
        if (_config is null)
            return;

        try
        {
            _config.Set(ShareSelectionKey, enabled);
            _config.Save(true);
        }
        catch (Exception ex)
        {
            LogWarning("Could not save the share_selection_box setting: " + ex.Message);
        }
    }

    internal static int KeyProfileSeconds => Get(KeyProfileSecondsKey, 3);
    internal static int ProfileSeconds => Get(ProfileSecondsKey, 5);
    internal static bool ShowDashScreen => Get(ShowDashScreenKey, true);
    internal static bool ThumbnailsEnabled => Get(ThumbnailsKey, true);
    internal static int RowsPerPage => Math.Clamp(Get(RowsPerPageKey, 150), 25, 150);

    public override void OnEngineInit()
    {
        _config = GetConfiguration();
        SaveDefaultsIfMissing();
        TelemetryLoop.Start();
        Msg("Loaded. Open the World Telemetry screen on the dash and press Create log to write a report of the focused world to the Logs folder.");
    }

    private static void SaveDefaultsIfMissing()
    {
        if (_config is null)
            return;

        try
        {
            if (!File.Exists(ConfigPath))
                _config.Save(true);
        }
        catch (Exception ex)
        {
            LogWarning("Could not create the settings file: " + ex.Message);
        }
    }

    private static T Get<T>(ModConfigurationKey<T> key, T fallback)
    {
        try
        {
            if (_config is not null && _config.TryGetValue(key, out T? value) && value is not null)
                return value;
        }
        catch (Exception)
        {
        }
        return fallback;
    }

    internal static void Log(string message) => Msg(message);

    internal static void LogWarning(string message) => Warn(message);
}
