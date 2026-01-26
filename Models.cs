using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeuroBureau.Experiment;

public sealed class ExperimentFile
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    [JsonPropertyName("create-time")] public long CreateTime { get; set; }
    [JsonPropertyName("mod-time")] public long ModTime { get; set; }

    [JsonPropertyName("stimuls")] public List<StimulFile> Stimuls { get; set; } = new();
    [JsonPropertyName("devices")] public List<DeviceFile> Devices { get; set; } = new();

    [JsonPropertyName("characteristics")] public List<CharacteristicDef> Characteristics { get; set; } = new();

    // чтобы не терять неизвестные поля (если они появятся)
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class DeviceFile
{
    [JsonPropertyName("name")] public string? Name { get; set; }

    // "PathFinder", "MouseKeyboard", "ShimmerGSR"
    [JsonPropertyName("dev-type")] public string? DevType { get; set; }

    [JsonPropertyName("uid")] public string Uid { get; set; } = "";

    // ВАЖНО: в старом формате params — это СТРОКА с JSON внутри
    // пример: "{\"samplingRate\":128,...}"
    [JsonPropertyName("params")] public string? Params { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class StimulFile
{
    [JsonPropertyName("uid")] public string Uid { get; set; } = "";
    [JsonPropertyName("kind")] public int? Kind { get; set; }
    [JsonPropertyName("filename")] public string? Filename { get; set; }

    [JsonPropertyName("write-data")] public bool WriteData { get; set; } = true;
    [JsonPropertyName("next-timeout")] public bool NextTimeout { get; set; } = false;
    [JsonPropertyName("timeout")] public int TimeoutMs { get; set; } = 0;

    [JsonPropertyName("rgba")] public string? Rgba { get; set; }
    [JsonPropertyName("scale")] public bool Scale { get; set; } = true;

    [JsonPropertyName("random-group")] public string? RandomGroup { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CharacteristicDef
{
    [JsonPropertyName("char-type")] public string? CharType { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class VerificationResult
{
    [JsonPropertyName("accuracy")] public double Accuracy { get; set; } = 0.0;
    [JsonPropertyName("precision")] public double Precision { get; set; } = 0.0;
    [JsonPropertyName("visibility")] public double Visibility { get; set; } = 0.0;
    [JsonPropertyName("L")] public double L { get; set; } = 0.0;
}

public sealed class ResultFile
{
    [JsonPropertyName("rgba")] public string Rgba { get; set; } = "rgb(0,0,0)";
    [JsonPropertyName("time")] public long TimeUnix { get; set; }
    [JsonPropertyName("duration")] public long DurationSec { get; set; }

    [JsonPropertyName("chars-data")] public List<CharValue> CharsData { get; set; } = new();

    [JsonPropertyName("screen-width")] public int ScreenWidthPx { get; set; }
    [JsonPropertyName("screen-height")] public int ScreenHeightPx { get; set; }
    [JsonPropertyName("screen-width-mm")] public int ScreenWidthMm { get; set; }
    [JsonPropertyName("screen-height-mm")] public int ScreenHeightMm { get; set; }

    [JsonPropertyName("verify-result")] public object VerifyResult { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CharValue
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("val")] public string Val { get; set; } = "";
}
public sealed record SelectedResult(string ResultUid, string? Rgba);

public static class StimulusKinds
{
    // В проекте уже используется kind==0 как "калибровка".
    public const int Calibration = 0;

    // Обычные файловые стимулы (исторически трактуются как kind!=0).
    // Не полагаемся на это значение в логике, но оставляем для документации.
    public const int FileStimulus = 1;

    // Новый тип: запись экрана в results/<resultUid>/<stimUid>/stimul.mkv
    public const int ScreenRecord = 3;

    // Ранние версии использовали значение 2 для SCREEN_RECORD.
    public const int LegacyScreenRecord = 2;

    public static bool IsScreenRecord(int kind)
        => kind == ScreenRecord || kind == LegacyScreenRecord;
}
