using System;
using System.IO;

namespace NeuroBureau.Experiment;

internal static class TrackerPaths
{
    public static string[] CandidateTrackerDirs() => new[]
    {
        // 1) СНАЧАЛА Local (как у тебя по факту лежит конфиг)
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tracker"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tracker"),
    };

    public static string FindExistingOrDefault(string fileName)
    {
        foreach (var dir in CandidateTrackerDirs())
        {
            var p = Path.Combine(dir, fileName);
            if (File.Exists(p)) return p;

            // 2) legacy: если просят config.json, но на диске есть "config" без расширения
            if (string.Equals(fileName, "config.json", StringComparison.OrdinalIgnoreCase))
            {
                var legacy = Path.Combine(dir, "config");
                if (File.Exists(legacy)) return legacy;
            }
        }

        // default: Local
        // default: Local
        return Path.Combine(CandidateTrackerDirs()[0], fileName);

    }

    public static void EnsureParentDir(string filePath)
    {
        var d = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(d)) Directory.CreateDirectory(d);
    }
}
