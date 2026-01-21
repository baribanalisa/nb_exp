using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NeuroBureau.Experiment;

public sealed class SensorsListFile
{
    [JsonPropertyName("list")]
    public List<SensorItem> List { get; set; } = new();
}

public sealed class SensorItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("dev-type")] public string DevType { get; set; } = "";
    [JsonPropertyName("uid")] public string Uid { get; set; } = "";

    // ✅ как в старом: поле отсутствует, если null
    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Params { get; set; }
}

// <exp>/sensors.json (биндинг)
public sealed class ExpBindConfigFile
{
    [JsonPropertyName("list")]
    public List<ExpBindItem> List { get; set; } = new();
}

public sealed class ExpBindItem
{
    [JsonPropertyName("device")] public string Device { get; set; } = ""; // имя device из exp.json
    [JsonPropertyName("name")] public string Name { get; set; } = "";     // имя сенсора из глобального sensors.json
}
