using System;

namespace NeuroBureau.Experiment;

public sealed class ExperimentListItem
{
    public string UidFolder { get; set; } = "";
    public string ExpDir { get; set; } = "";
    public string ExpJsonPath { get; set; } = "";
    public string DevicesText { get; set; } = "";

    public string? Name { get; set; }
    public string? Description { get; set; }

    public long CreateTime { get; set; }
    public long ModTime { get; set; }

    public int ResultsCount { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? $"{UidFolder}  (results: {ResultsCount})"
        : $"{Name}  (results: {ResultsCount})";

    public string ModTimeText => FormatUnix(ModTime);
    public string CreateTimeText => FormatUnix(CreateTime);

    private static string FormatUnix(long ts)
    {
        // [Предположение] В exp.json время может быть в секундах или миллисекундах Unix.
        // Поэтому делаем авто-детект по размеру числа.
        if (ts <= 0) return "";

        try
        {
            DateTimeOffset dto =
                ts > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(ts) : DateTimeOffset.FromUnixTimeSeconds(ts);

            return dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            return ts.ToString();
        }
    }
}
