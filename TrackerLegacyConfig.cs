using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NeuroBureau.Experiment;

internal static class TrackerLegacyConfig
{
    public static void UpdateOnly(string expPath, string keyNext, string keyStop, string? creatorIp = null)
    {
        var path = TrackerPaths.FindExistingOrDefault("config.json");
        TrackerPaths.EnsureParentDir(path);

        JsonObject obj;

        try
        {
            obj = File.Exists(path)
                ? (JsonNode.Parse(File.ReadAllText(path)) as JsonObject) ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            obj = new JsonObject();
        }

        // ✅ меняем только нужные поля (как в твоём существующем файле)
        obj["exp-path"] = expPath;
        obj["key-next"] = NormalizeKeyForTracker(keyNext);
        obj["key-stop"] = NormalizeKeyForTracker(keyStop);

        if (!string.IsNullOrWhiteSpace(creatorIp))
            obj["creator-ip"] = creatorIp;

        File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    private static string NormalizeKeyForTracker(string key)
    {
        // В твоём legacy-конфиге: "Esc", а WPF обычно даёт "Escape"
        if (string.Equals(key, "Escape", System.StringComparison.OrdinalIgnoreCase)) return "Esc";
        if (string.Equals(key, "Return", System.StringComparison.OrdinalIgnoreCase)) return "Enter";
        return key;
    }
}
 