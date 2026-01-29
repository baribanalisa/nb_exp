using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace NeuroBureau.Experiment;

internal sealed record CameraProfileCandidate(int Width, int Height, int FrameRate, string? InputFormat)
{
    public override string ToString() => InputFormat == null 
        ? $"{Width}x{Height}@{FrameRate} (auto)" 
        : $"{Width}x{Height}@{FrameRate} ({InputFormat})";
}

internal static class CameraProfileProbe
{
    private sealed record ProbeResult(int ExitCode, string StdOut, string StdErr);

    public static async Task<CameraProfile> ProbeAsync(string ffmpegExe, CameraDeviceInfo device)
    {
        if (string.IsNullOrWhiteSpace(ffmpegExe))
            throw new ArgumentException("Не указан путь к ffmpeg.", nameof(ffmpegExe));
        if (device == null)
            throw new ArgumentNullException(nameof(device));
        if (string.IsNullOrWhiteSpace(device.FriendlyName))
            throw new ArgumentException("Не указано имя камеры.", nameof(device));

        var cam = device.FriendlyName.Replace("\"", "").Trim();
        var capabilities = await ReadCapabilitiesAsync(ffmpegExe, cam).ConfigureAwait(false);

        var candidates = BuildCandidates();
        var errors = new StringBuilder();

        foreach (var candidate in candidates)
        {
            // ========================================================
            // ИСПРАВЛЕНИЕ: НЕ используем -input_format напрямую!
            // Вместо этого используем -pixel_format для raw форматов
            // и -vcodec для сжатых (mjpeg)
            // Или вообще не указываем формат (auto) - самый надёжный вариант
            // ========================================================
            
            string formatArg;
            if (string.IsNullOrEmpty(candidate.InputFormat))
            {
                // Авто-режим: ffmpeg сам договорится с камерой
                formatArg = "";
            }
            else if (candidate.InputFormat == "mjpeg")
            {
                // Для mjpeg используем -vcodec (работает везде)
                formatArg = "-vcodec mjpeg ";
            }
            else
            {
                // Для raw форматов (yuyv422, nv12) используем -pixel_format
                formatArg = $"-pixel_format {candidate.InputFormat} ";
            }

            var args =
                $"-hide_banner -loglevel error " +
                $"-f dshow -video_size {candidate.Width}x{candidate.Height} " +
                $"-framerate {candidate.FrameRate} {formatArg}" +
                $"-i video=\"{cam}\" -t 2 -f null -";

            var result = await RunAsync(ffmpegExe, args).ConfigureAwait(false);
            if (result.ExitCode == 0 && !HasError(result.StdErr))
                return new CameraProfile
                {
                    DeviceId = string.IsNullOrWhiteSpace(device.AlternativeName)
                        ? device.FriendlyName
                        : device.AlternativeName,
                    FriendlyName = device.FriendlyName,
                    InputFormat = candidate.InputFormat ?? "", // пустая строка = авто
                    VideoSize = $"{candidate.Width}x{candidate.Height}",
                    Framerate = candidate.FrameRate.ToString()
                };

            errors
                .AppendLine($">>> {candidate}")
                .AppendLine($"exitCode={result.ExitCode}")
                .AppendLine(result.StdErr.Trim())
                .AppendLine();
        }

        var message = new StringBuilder();
        message.AppendLine("Не удалось подобрать рабочие параметры камеры.");
        message.AppendLine("Не удалось подобрать рабочий профиль камеры.");
        message.AppendLine();
        message.AppendLine("Список доступных опций ffmpeg dshow:");
        message.AppendLine(capabilities.Trim());
        message.AppendLine();
        message.AppendLine("Результаты пробных запусков:");
        message.AppendLine(errors.ToString().Trim());

        throw new InvalidOperationException(message.ToString().Trim());
    }

    private static async Task<string> ReadCapabilitiesAsync(string ffmpegExe, string cam)
    {
        var args = $"-hide_banner -list_options true -f dshow -i video=\"{cam}\"";
        var result = await RunAsync(ffmpegExe, args).ConfigureAwait(false);
        var combined = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.StdOut))
            combined.AppendLine(result.StdOut.Trim());
        if (!string.IsNullOrWhiteSpace(result.StdErr))
            combined.AppendLine(result.StdErr.Trim());

        if (combined.Length == 0)
            combined.AppendLine($"(ffmpeg не вернул вывод, exitCode={result.ExitCode})");

        return combined.ToString();
    }

    private static bool HasError(string stdErr)
    {
        if (string.IsNullOrWhiteSpace(stdErr))
            return false;

        return stdErr.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static List<CameraProfileCandidate> BuildCandidates()
    {
        // ИСПРАВЛЕНИЕ: Начинаем с авто-режима (null), потом mjpeg, потом raw форматы
        // Авто-режим работает на всех версиях ffmpeg
        var formats = new string?[] { null, "mjpeg", "yuyv422", "nv12" };
        
        var resolutions = new (int width, int height, int fps)[]
        {
            (1280, 720, 30),
            (1280, 720, 25),
            (1280, 720, 20),
            (1280, 720, 15),
            (640, 480, 30),
            (640, 480, 15),
        };

        var list = new List<CameraProfileCandidate>();
        
        // Сначала пробуем авто-режим для всех разрешений
        foreach (var (width, height, fps) in resolutions)
        {
            list.Add(new CameraProfileCandidate(width, height, fps, null)); // auto
        }
        
        // Потом конкретные форматы (если авто не сработал)
        foreach (var (width, height, fps) in resolutions)
        {
            foreach (var format in formats)
            {
                if (format != null) // авто уже добавлен выше
                    list.Add(new CameraProfileCandidate(width, height, fps, format));
            }
        }

        return list;
    }

    private static async Task<ProbeResult> RunAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Не удалось запустить ffmpeg.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().ConfigureAwait(false);

        var stdOut = await stdOutTask.ConfigureAwait(false);
        var stdErr = await stdErrTask.ConfigureAwait(false);

        return new ProbeResult(process.ExitCode, stdOut, stdErr);
    }
}
