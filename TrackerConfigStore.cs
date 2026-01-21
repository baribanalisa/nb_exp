using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NeuroBureau.Experiment;

public sealed class TrackerConfigStore
{
    private readonly string _path;

    public TrackerConfigStore(string path) => _path = path;

    public static TrackerConfigStore OpenDefault()
        => new(TrackerPaths.FindExistingOrDefault("config.json"));

    public JsonObject LoadOrCreate()
    {
        try
        {
            if (File.Exists(_path))
            {
                var node = JsonNode.Parse(File.ReadAllText(_path),
                    new JsonNodeOptions { PropertyNameCaseInsensitive = true });

                if (node is JsonObject obj) return obj;
            }
        }
        catch { /* игнор */ }

        return new JsonObject();
    }

    public void UpdateUiSettings(string expPath, string? creatorIp, string keyNext, string keyStop)
    {
        var obj = LoadOrCreate();

        // сохраняем как в “старом” формате
        obj["exp-path"] = expPath;
        obj["key-next"] = keyNext;
        obj["key-stop"] = keyStop;

        if (!string.IsNullOrWhiteSpace(creatorIp))
            obj["creator-ip"] = creatorIp;

        Save(obj);
    }

    private void Save(JsonObject obj)
    {
        TrackerPaths.EnsureParentDir(_path);

        File.WriteAllText(_path, obj.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false // чтобы не раздувать форматирование
        }));
    }
}
