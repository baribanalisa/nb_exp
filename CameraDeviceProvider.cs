using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NeuroBureau.Experiment;

internal static class CameraDeviceProvider
{
    public static string? FindFfmpegExe()
    {
        var local1 = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local1)) return local1;

        var local2 = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(local2)) return local2;

        try
        {
            var psi = new ProcessStartInfo("where", "ffmpeg")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;

            var line = p.StandardOutput.ReadLine();
            p.WaitForExit(1500);

            if (!string.IsNullOrWhiteSpace(line) && File.Exists(line.Trim()))
                return line.Trim();
        }
        catch { }

        return null;
    }

    public static async Task<List<string>> GetVideoDevicesAsync(string ffmpegExe)
    {
        var all = await GetAllQuotedDevicesAsync(ffmpegExe);
        var res = new List<string>();

        foreach (var d in all)
            if (!IsLikelyAudio(d)) res.Add(d);

        // fallback: если фильтр “съел всё” — верни как было (чтобы не пусто)
        return res.Count > 0 ? res : all;
    }

    public static async Task<List<string>> GetAudioDevicesAsync(string ffmpegExe)
    {
        var all = await GetAllQuotedDevicesAsync(ffmpegExe);
        var res = new List<string>();

        foreach (var d in all)
            if (IsLikelyAudio(d)) res.Add(d);

        return res;
    }

    private static bool IsLikelyAudio(string name)
    {
        // англ + рус: микрофоны/аудио/realtek/динамики/наушники и т.п.
        var s = name.ToLowerInvariant();
        return s.Contains("microphone") ||
               s.Contains("mic") ||
               s.Contains("audio") ||
               s.Contains("realtek") ||
               s.Contains("speakers") ||
               s.Contains("headphones") ||
               s.Contains("stereo mix") ||
               s.Contains("микроф") ||
               s.Contains("аудио") ||
               s.Contains("динамик") ||
               s.Contains("науш");
    }

    private static async Task<List<string>> GetAllQuotedDevicesAsync(string ffmpegExe)
    {
        // Важно: list_devices обычно пишет в stderr, но берём и stdout тоже
        var psi = new ProcessStartInfo(ffmpegExe, "-hide_banner -list_devices true -f dshow -i dummy")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi);
        if (p == null) return new List<string>();

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        try { p.WaitForExit(2000); } catch { }

        var text = stdout + "\n" + stderr;

        var rx = new Regex("\"([^\"]+)\"", RegexOptions.Compiled);
        var result = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.Contains("\"")) continue;
            if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase)) continue;

            var m = rx.Match(line);
            if (!m.Success) continue;

            var name = m.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!result.Contains(name))
                result.Add(name);
        }

        return result;
    }
}
