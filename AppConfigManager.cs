using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;

namespace NeuroBureau.Experiment;

internal static class Streamer
{
    public const int STREAM = 1;
    public const int RECORD = 2;
}

public sealed record AppConfig(
    string ExperimentsRoot,
    Hotkey NextHotkey,
    Hotkey ExitHotkey,
    int WriteDesktop,

    // режимы в config.*
    int DesktopMode,
    int CameraMode,
    string? CameraDeviceName,

    int AudioMode,
    string? AudioDeviceName
)
{
    public bool RecordDesktop => (DesktopMode & Streamer.RECORD) != 0;
    public bool RecordCamera => (CameraMode & Streamer.RECORD) != 0;
    public bool RecordAudio => (AudioMode & Streamer.RECORD) != 0;
}

public static class AppConfigManager
{
    public static AppConfig Load()
    {
        var cfgPath = FindConfigPath();

        var defaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Experiments"
        );

        var nextDef = new Hotkey(ModifierKeys.None, Key.Space);
        var exitDef = new Hotkey(ModifierKeys.None, Key.Escape);

        if (!File.Exists(cfgPath))
            return new AppConfig(defaultRoot, nextDef, exitDef, -1, 0, 0, null, 0, null);

        try
        {
            var obj = JsonNode.Parse(File.ReadAllText(cfgPath)) as JsonObject;
            if (obj == null) return new AppConfig(defaultRoot, nextDef, exitDef, -1, 0, 0, null, 0, null);

            var root = ((string?)obj["exp-path"] ?? (string?)obj["exp_path"] ?? defaultRoot).Trim();

            var nextKey = (string?)obj["key-next"] ?? (string?)obj["key_next"];
            var nextMod = (string?)obj["modifier_next"];
            var next = HotkeyUtil.Parse(nextMod, NormalizeLegacyKey(nextKey), nextDef);

            var exitKey = (string?)obj["key-stop"] ?? (string?)obj["key_exit"] ?? (string?)obj["key_abort"];
            var exitMod = (string?)obj["modifier_exit"] ?? (string?)obj["modifier_abort"];
            var exit = HotkeyUtil.Parse(exitMod, NormalizeLegacyKey(exitKey), exitDef);

            int writeDesktop = -1;
            var wd = obj["write-desktop"] ?? obj["write_desktop"];
            if (wd != null)
            {
                if (wd is JsonValue v && v.TryGetValue<int>(out var i)) writeDesktop = i;
                else if (int.TryParse((string?)wd, out var j)) writeDesktop = j;
            }

            // читаем строго из config.*
            var cfgObj = obj["config"] as JsonObject;

            int desktopMode = ReadInt(cfgObj?["desktop"], 0);
            int cameraMode = ReadInt(cfgObj?["camera"], 0);
            int audioMode = ReadInt(cfgObj?["audio"], 0);

            string? cameraDeviceName = (string?)cfgObj?["camera_device"];
            string? audioDeviceName = (string?)cfgObj?["audio_device"];

            return new AppConfig(root, next, exit, writeDesktop,
                desktopMode, cameraMode, cameraDeviceName,
                audioMode, audioDeviceName);
        }
        catch
        {
            return new AppConfig(defaultRoot, nextDef, exitDef, -1, 0, 0, null, 0, null);
        }
    }

    private static int ReadInt(JsonNode? n, int def = 0)
    {
        if (n == null) return def;

        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var si)) return si;
        }

        if (int.TryParse((string?)n, out var j)) return j;
        return def;
    }

    private static string? NormalizeLegacyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return key;
        if (string.Equals(key, "Esc", StringComparison.OrdinalIgnoreCase)) return "Escape";
        return key;
    }

    public static void Save(string experimentsRoot, Hotkey next, Hotkey exit)
        => Save(experimentsRoot, next, exit, writeDesktop: -1,
            recordDesktop: false, recordCamera: false, cameraDeviceName: null,
            recordAudio: false, audioDeviceName: null);

    public static void Save(
        string experimentsRoot,
        Hotkey next,
        Hotkey exit,
        int writeDesktop,
        bool recordDesktop,
        bool recordCamera,
        string? cameraDeviceName,
        bool recordAudio,
        string? audioDeviceName)
    {
        if (string.IsNullOrWhiteSpace(experimentsRoot))
            throw new ArgumentException("Папка экспериментов пустая.");

        experimentsRoot = experimentsRoot.Trim();

        var cfgPath = FindConfigPath();
        var dir = Path.GetDirectoryName(cfgPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        JsonObject rootObj;
        try
        {
            rootObj = File.Exists(cfgPath)
                ? (JsonNode.Parse(File.ReadAllText(cfgPath)) as JsonObject) ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            rootObj = new JsonObject();
        }

        // --- ключи: сохраняем стиль, если файл уже legacy ---
        var expKey =
            rootObj.ContainsKey("exp_path") ? "exp_path" :
            rootObj.ContainsKey("exp-path") ? "exp-path" :
            "exp-path";

        var keyNextKey =
            rootObj.ContainsKey("key_next") ? "key_next" :
            rootObj.ContainsKey("key-next") ? "key-next" :
            "key-next";

        var keyStopKey =
            rootObj.ContainsKey("key_abort") ? "key_abort" :
            rootObj.ContainsKey("key_exit") ? "key_exit" :
            rootObj.ContainsKey("key-stop") ? "key-stop" :
            "key-stop";

        var modNextKey =
            rootObj.ContainsKey("modifier_next") ? "modifier_next" :
            "modifier_next";

        var modStopKey =
            rootObj.ContainsKey("modifier_exit") ? "modifier_exit" :
            rootObj.ContainsKey("modifier_abort") ? "modifier_abort" :
            "modifier_abort";

        // --- базовые поля ---
        rootObj[expKey] = experimentsRoot;
        rootObj[keyNextKey] = HotkeyUtil.SerializeKey(next.Key);
        rootObj[keyStopKey] = HotkeyUtil.SerializeKey(exit.Key);
        rootObj[modNextKey] = HotkeyUtil.SerializeModifiers(next.Modifiers);
        rootObj[modStopKey] = HotkeyUtil.SerializeModifiers(exit.Modifiers);

        // монитор
        var wdKey =
            rootObj.ContainsKey("write-desktop") ? "write-desktop" :
            rootObj.ContainsKey("write_desktop") ? "write_desktop" :
            "write_desktop";
        rootObj[wdKey] = writeDesktop;

        // --- ВАЖНОЕ: config.desktop / config.camera (строго RECORD=2) ---
        var cfgObj = rootObj["config"] as JsonObject;
        if (cfgObj == null)
        {
            cfgObj = new JsonObject();
            rootObj["config"] = cfgObj;
        }

        cfgObj["desktop"] = recordDesktop ? Streamer.RECORD : 0; // строго 2 или 0
        cfgObj["camera"] = recordCamera ? Streamer.RECORD : 0; // строго 2 или 0
        cfgObj["audio"] = recordAudio ? Streamer.RECORD : 0; // строго 2 или 0

        if (recordCamera && !string.IsNullOrWhiteSpace(cameraDeviceName))
            cfgObj["camera_device"] = cameraDeviceName;
        else
            cfgObj.Remove("camera_device");

        if (recordAudio && !string.IsNullOrWhiteSpace(audioDeviceName))
            cfgObj["audio_device"] = audioDeviceName;
        else
            cfgObj.Remove("audio_device");

        File.WriteAllText(cfgPath, rootObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ====== analysis-visualization ======

    public static AnalysisVisualizationSettings LoadAnalysisVisualizationSettings()
    {
        var cfgPath = FindConfigPath();
        var settings = new AnalysisVisualizationSettings();

        if (!File.Exists(cfgPath)) return settings;

        try
        {
            var obj = JsonNode.Parse(File.ReadAllText(cfgPath)) as JsonObject;
            if (obj == null) return settings;

            if (obj["analysis-visualization"] is not JsonObject visObj) return settings;

            // Фиксации / базовая визуализация
            settings.MinRadius = ReadDouble(visObj, "min-radius", settings.MinRadius);
            settings.MaxRadius = ReadDouble(visObj, "max-radius", settings.MaxRadius);
            settings.MaxDurationSec = ReadDouble(visObj, "max-duration-sec", settings.MaxDurationSec);
            settings.LineWidth = ReadDouble(visObj, "line-width", settings.LineWidth);
            settings.Alpha = ReadDouble(visObj, "alpha", settings.Alpha);
            settings.FontFamily = ReadString(visObj, "font-family", settings.FontFamily);
            settings.FontSize = ReadDouble(visObj, "font-size", settings.FontSize);

            // Bee Swarm
            settings.BeeRadius = ReadDouble(visObj, "bee-radius", settings.BeeRadius);
            settings.BeeLineWidth = ReadDouble(visObj, "bee-line-width", settings.BeeLineWidth);

            // Heatmap
            settings.HeatmapRadius = ReadDouble(visObj, "heatmap-radius", settings.HeatmapRadius);
            settings.HeatmapInitialOpacity = ReadDouble(visObj, "heatmap-alpha", settings.HeatmapInitialOpacity);
            settings.HeatmapThreshold = ReadDouble(visObj, "heatmap-threshold", settings.HeatmapThreshold);

            var hfStr = ReadString(visObj, "heatmap-function", settings.HeatmapFunction.ToString());
            if (Enum.TryParse<HeatmapFalloff>(hfStr, ignoreCase: true, out var hf))
                settings.HeatmapFunction = hf;

            var htStr = ReadString(visObj, "heatmap-type", settings.HeatmapMapType.ToString());
            if (Enum.TryParse<HeatmapType>(htStr, ignoreCase: true, out var ht))
                settings.HeatmapMapType = ht;

            // КГР фильтр
            settings.KgrFilterEnabled = ReadBool(visObj, "kgr-filter-enabled", settings.KgrFilterEnabled);
            settings.KgrUseMedianFilter = ReadBool(visObj, "kgr-use-median", settings.KgrUseMedianFilter);
            settings.KgrMedianWindowSec = ReadDouble(visObj, "kgr-median-window-sec", settings.KgrMedianWindowSec);
            settings.KgrUseEmaFilter = ReadBool(visObj, "kgr-use-ema", settings.KgrUseEmaFilter);

            settings.KgrSrEmaTauSec = ReadDouble(visObj, "kgr-sr-tau-sec", settings.KgrSrEmaTauSec);
            settings.KgrScEmaTauSec = ReadDouble(visObj, "kgr-sc-tau-sec", settings.KgrScEmaTauSec);
            settings.KgrHrEmaTauSec = ReadDouble(visObj, "kgr-hr-tau-sec", settings.KgrHrEmaTauSec);
            settings.KgrPpgEmaTauSec = ReadDouble(visObj, "kgr-ppg-tau-sec", settings.KgrPpgEmaTauSec);

            settings.KgrClampHr = ReadBool(visObj, "kgr-clamp-hr", settings.KgrClampHr);
            settings.KgrHrMin = ReadDouble(visObj, "kgr-hr-min", settings.KgrHrMin);
            settings.KgrHrMax = ReadDouble(visObj, "kgr-hr-max", settings.KgrHrMax);
            settings.KgrHrMaxDeltaPerSec = ReadDouble(visObj, "kgr-hr-max-delta-per-sec", settings.KgrHrMaxDeltaPerSec);

            settings.Normalize();
            return settings;
        }
        catch
        {
            return settings;
        }
    }

        public static void SaveAnalysisVisualizationSettings(AnalysisVisualizationSettings settings)
    {
        var cfgPath = FindConfigPath();
        var dir = Path.GetDirectoryName(cfgPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        JsonObject rootObj;
        try
        {
            rootObj = File.Exists(cfgPath)
                ? (JsonNode.Parse(File.ReadAllText(cfgPath)) as JsonObject) ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            rootObj = new JsonObject();
        }

        rootObj["analysis-visualization"] = new JsonObject
        {
            ["min-radius"] = settings.MinRadius,
            ["max-radius"] = settings.MaxRadius,
            ["max-duration-sec"] = settings.MaxDurationSec,
            ["line-width"] = settings.LineWidth,
            ["alpha"] = settings.Alpha,
            ["font-family"] = settings.FontFamily,
            ["font-size"] = settings.FontSize,
        };

        File.WriteAllText(cfgPath, rootObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ====== multi-export (настройки окна мультиэкспорта) ======

    public static MultiExportSettings LoadMultiExportSettings()
    {
        var cfgPath = FindConfigPath();
        var s = new MultiExportSettings();

        if (!File.Exists(cfgPath)) return s;

        try
        {
            var obj = JsonNode.Parse(File.ReadAllText(cfgPath)) as JsonObject;
            if (obj == null) return s;

            if (obj["multi-export"] is not JsonObject me) return s;

            s.OutputDir = ReadString(me, "output-dir", s.OutputDir);
            s.FilenameTemplate = ReadString(me, "filename-template", s.FilenameTemplate);

            var modeStr = ReadString(me, "mode", s.Mode.ToString());
            if (Enum.TryParse<MultiExportMode>(modeStr, ignoreCase: true, out var parsedMode))
                s.Mode = parsedMode;

            s.ExportSource = ReadBool(me["export-source"], s.ExportSource);
            s.ExportRaw = ReadBool(me["export-raw"], s.ExportRaw);
            s.ExportActions = ReadBool(me["export-actions"], s.ExportActions);
            s.ExportAoi = ReadBool(me["export-aoi"], s.ExportAoi);
            s.ExportGazeImage = ReadBool(me["export-gaze-image"], s.ExportGazeImage);
            s.ExportHeatImage = ReadBool(me["export-heat-image"], s.ExportHeatImage);
            s.ExportEdf = ReadBool(me["export-edf"], s.ExportEdf);

            return s;
        }
        catch
        {
            return s;
        }
    }

    public static void SaveMultiExportSettings(MultiExportSettings settings)
    {
        var cfgPath = FindConfigPath();
        var dir = Path.GetDirectoryName(cfgPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        JsonObject rootObj;
        try
        {
            rootObj = File.Exists(cfgPath)
                ? (JsonNode.Parse(File.ReadAllText(cfgPath)) as JsonObject) ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            rootObj = new JsonObject();
        }

        rootObj["multi-export"] = new JsonObject
        {
            ["output-dir"] = settings.OutputDir,
            ["filename-template"] = settings.FilenameTemplate,
            ["mode"] = settings.Mode.ToString(),

            ["export-source"] = settings.ExportSource,
            ["export-raw"] = settings.ExportRaw,
            ["export-actions"] = settings.ExportActions,
            ["export-aoi"] = settings.ExportAoi,
            ["export-gaze-image"] = settings.ExportGazeImage,
            ["export-heat-image"] = settings.ExportHeatImage,
            ["export-edf"] = settings.ExportEdf,
        };

        File.WriteAllText(cfgPath, rootObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool ReadBool(JsonNode? n, bool def = false)
    {
        if (n == null) return def;

        if (n is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<int>(out var i)) return i != 0;
            if (v.TryGetValue<string>(out var s))
            {
                if (bool.TryParse(s, out var pb)) return pb;
                if (int.TryParse(s, out var pi)) return pi != 0;
            }
        }

        if (bool.TryParse((string?)n, out var b2)) return b2;
        if (int.TryParse((string?)n, out var i2)) return i2 != 0;
        return def;
    }

    private static string FindConfigPath()

    {
        var localDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "tracker"
        );
        var roamingDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "tracker"
        );

        var candidates = new[]
        {
            Path.Combine(localDir, "config.json"),
            Path.Combine(localDir, "config"),
            Path.Combine(roamingDir, "config.json"),
            Path.Combine(roamingDir, "config"),
        };

        foreach (var p in candidates)
            if (File.Exists(p)) return p;

        return Path.Combine(localDir, "config.json");
    }

       private static double ReadDouble(JsonObject obj, string key, double fallback)
    {
        var v = obj[key];
        if (v == null) return fallback;
        if (v is JsonValue val && val.TryGetValue<double>(out var d)) return d;
        if (double.TryParse((string?)v, out var parsed)) return parsed;
        return fallback;
    }

    private static bool ReadBool(JsonObject obj, string key, bool fallback)
    {
        var v = obj[key];
        return ReadBool(v, fallback);
    }

    private static string ReadString(JsonObject obj, string key, string fallback)
    {
        var v = obj[key];
        if (v == null) return fallback;
        var s = (string?)v;
        return string.IsNullOrWhiteSpace(s) ? fallback : s;
    }
}


