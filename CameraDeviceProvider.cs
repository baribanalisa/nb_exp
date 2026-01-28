using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NeuroBureau.Experiment;

internal static class CameraDeviceProvider
{
    /// <summary>
    /// Ищет ffmpeg.exe: сначала рядом с приложением, потом в PATH.
    /// </summary>
    public static string? FindFfmpegExe(bool allowFfmpegFromPath = true)
    {
        // 1. Рядом с приложением
        var local1 = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local1)) return local1;

        // 2. В подпапке ffmpeg
        var local2 = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(local2)) return local2;

        // 3. В подпапке bin
        var local3 = Path.Combine(AppContext.BaseDirectory, "bin", "ffmpeg.exe");
        if (File.Exists(local3)) return local3;

        // 4. В PATH
        if (!allowFfmpegFromPath)
            return null;

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

    /// <summary>
    /// Получает версию FFmpeg (например "6.1.1" или "n7.0").
    /// </summary>
    public static async Task<string?> GetFfmpegVersionAsync(string ffmpegExe)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpegExe, "-version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p == null) return null;

            var output = await p.StandardOutput.ReadToEndAsync();
            p.WaitForExit(2000);

            // Ищем версию: "ffmpeg version 6.1.1" или "ffmpeg version n7.0-..."
            var match = Regex.Match(output, @"ffmpeg version (\S+)", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value;

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Проверяет, является ли версия FFmpeg достаточно новой (>= 5.0).
    /// </summary>
    public static bool IsVersionSupported(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;

        // Пробуем извлечь major версию
        var match = Regex.Match(version, @"^n?(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var major))
        {
            return major >= 5; // FFmpeg 5.0+ поддерживает все нужные опции
        }

        return true; // Если не распознали - даём шанс
    }

    /// <summary>
    /// Получает список видео-устройств (камер).
    /// </summary>
    public static async Task<List<CameraDeviceInfo>> GetVideoDevicesAsync(string ffmpegExe)
    {
        var all = await GetAllDeviceInfosAsync(ffmpegExe);
        var res = new List<CameraDeviceInfo>();

        foreach (var d in all)
            if (!IsLikelyAudio(d.FriendlyName)) res.Add(d);

        // fallback: если фильтр "съел всё" — верни как было
        return res.Count > 0 ? res : all;
    }

    /// <summary>
    /// Получает список аудио-устройств (микрофонов).
    /// </summary>
    public static async Task<List<string>> GetAudioDevicesAsync(string ffmpegExe)
    {
        var all = await GetAllDeviceInfosAsync(ffmpegExe);
        var res = new List<string>();

        foreach (var d in all)
            if (IsLikelyAudio(d.FriendlyName))
                res.Add(d.FriendlyName);

        return res;
    }

    /// <summary>
    /// Простая проверка работоспособности камеры.
    /// НЕ перебирает форматы - просто проверяет, что камера отвечает.
    /// </summary>
    public static async Task<CameraCheckResult> CheckCameraSimpleAsync(string ffmpegExe, string cameraName)
    {
        try
        {
            // Просто пробуем открыть камеру на 1 секунду без записи
            // Используем -t 1 для ограничения времени
            var args = $"-hide_banner -f dshow -i video=\"{cameraName}\" -t 0.5 -f null -";

            var psi = new ProcessStartInfo(ffmpegExe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p == null)
                return new CameraCheckResult(false, "Не удалось запустить FFmpeg");

            var stderrTask = p.StandardError.ReadToEndAsync();

            // Ждём завершения (с таймаутом 5 секунд)
            var completed = p.WaitForExit(5000);
            if (!completed)
            {
                try { p.Kill(); } catch { }
                return new CameraCheckResult(false, "Таймаут при проверке камеры");
            }

            var stderr = await stderrTask;

            // Если есть "Error opening input" - камера не работает
            if (stderr.Contains("Error opening input", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("Could not find video device", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            {
                return new CameraCheckResult(false, ExtractShortError(stderr));
            }

            // Если exitCode=0 или есть признаки успешного открытия - камера работает
            // FFmpeg может вернуть ненулевой код даже при успешном открытии (из-за -t 0.5)
            if (stderr.Contains("Stream #0", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("Input #0", StringComparison.OrdinalIgnoreCase))
            {
                return new CameraCheckResult(true, "OK");
            }

            // Неопределённый результат - считаем что работает (проверим при реальной записи)
            return new CameraCheckResult(true, "Предварительно OK");
        }
        catch (Exception ex)
        {
            return new CameraCheckResult(false, ex.Message);
        }
    }

    private static string ExtractShortError(string stderr)
    {
        // Извлекаем первую строку с ошибкой
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Could not", StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 100)
                    trimmed = trimmed.Substring(0, 100) + "...";
                return trimmed;
            }
        }

        // Если конкретная ошибка не найдена - первые 100 символов
        if (stderr.Length > 100)
            return stderr.Substring(0, 100) + "...";
        return stderr;
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

    private static async Task<List<CameraDeviceInfo>> GetAllDeviceInfosAsync(string ffmpegExe)
    {
        // list_devices пишет в stderr
        var psi = new ProcessStartInfo(ffmpegExe, "-hide_banner -list_devices true -f dshow -i dummy")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi);
        if (p == null) return new List<CameraDeviceInfo>();

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        try { p.WaitForExit(2000); } catch { }

        var text = stdout + "\n" + stderr;

        var rx = new Regex("\"([^\"]+)\"", RegexOptions.Compiled);
        var result = new List<CameraDeviceInfo>();
        var currentIndex = -1;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.Contains("\"")) continue;

            var m = rx.Match(line);
            if (!m.Success) continue;

            var name = m.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase))
            {
                if (currentIndex >= 0 && currentIndex < result.Count)
                {
                    var existing = result[currentIndex];
                    if (string.IsNullOrWhiteSpace(existing.AlternativeName))
                        result[currentIndex] = existing with { AlternativeName = name };
                }
                continue;
            }

            var existingIndex = result.FindIndex(d =>
                string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                currentIndex = existingIndex;
                continue;
            }

            result.Add(new CameraDeviceInfo(name, null));
            currentIndex = result.Count - 1;
        }

        return result;
    }
}

/// <summary>
/// Результат проверки камеры.
/// </summary>
public readonly record struct CameraCheckResult(bool Success, string Message);
