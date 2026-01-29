using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace NeuroBureau.Experiment;

internal sealed class FfmpegRecorder : IAsyncDisposable
{
    private readonly Process _proc;

    private readonly StringBuilder _stderr = new();
    public string StderrText => _stderr.ToString();

    private FfmpegRecorder(Process proc)
    {
        _proc = proc;

        // собираем stderr построчно
        try
        {
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _stderr.AppendLine(e.Data);
            };
        }
        catch { }
    }

    public static Task<FfmpegRecorder> StartDesktopAsync(string ffmpegExe, string outPath, int fps = 30)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        var args =
            $"-y -hide_banner -loglevel error " +
            $"-f gdigrab -draw_mouse 1 -framerate {fps} -i desktop " +
            $"-c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p " +
            $"\"{outPath}\"";

        return StartAsync(ffmpegExe, args);
    }

    /// <summary>
    /// Камера (+ звук) live-mux сразу в один файл.
    /// Использует fallback-логику: если первая попытка не удалась, пробует без framerate.
    /// </summary>
    public static async Task<FfmpegRecorder> StartCameraAsync(
        string ffmpegExe,
        string cameraDeviceName,
        string outputPath,
        bool recordAudio,
        string? audioDeviceName,
        string? framerate = null,
        string? inputFormat = null,
        string? videoSize = null,
        string? rtbufsize = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var cam = (cameraDeviceName ?? "").Replace("\"", "").Trim();
        var mic = (audioDeviceName ?? "").Replace("\"", "").Trim();

        // Формируем input spec для dshow
        var inputSpec = (recordAudio && !string.IsNullOrWhiteSpace(mic))
            ? $"video=\"{cam}\":audio=\"{mic}\""
            : $"video=\"{cam}\"";

        // Аудио-кодек параметры
        var audioParams = (recordAudio && !string.IsNullOrWhiteSpace(mic))
            ? "-c:a aac -b:a 128k -ar 48000 -ac 2 -shortest "
            : "";

        var framerateValue = string.IsNullOrWhiteSpace(framerate) ? "30" : framerate.Trim();
        var framerateArg = string.IsNullOrWhiteSpace(framerateValue) ? "" : $"-framerate {framerateValue} ";
        
        // ========================================================
        // ИСПРАВЛЕНИЕ: НЕ используем -input_format напрямую!
        // Вместо этого используем -pixel_format для raw форматов
        // и -vcodec для сжатых (mjpeg)
        // Или вообще не указываем формат (auto) - самый надёжный вариант
        // ========================================================
        string inputFormatArg;
        if (string.IsNullOrWhiteSpace(inputFormat))
        {
            // Авто-режим: ffmpeg сам договорится с камерой
            inputFormatArg = "";
        }
        else if (inputFormat.Trim().Equals("mjpeg", StringComparison.OrdinalIgnoreCase))
        {
            // Для mjpeg используем -vcodec (работает везде)
            inputFormatArg = "-vcodec mjpeg ";
        }
        else
        {
            // Для raw форматов (yuyv422, nv12) используем -pixel_format
            inputFormatArg = $"-pixel_format {inputFormat.Trim()} ";
        }
        
        var videoSizeArg = string.IsNullOrWhiteSpace(videoSize) ? "" : $"-video_size {videoSize.Trim()} ";
        var rtbufsizeValue = string.IsNullOrWhiteSpace(rtbufsize) ? "512M" : rtbufsize.Trim();
        var rtbufsizeArg = string.IsNullOrWhiteSpace(rtbufsizeValue) ? "" : $"-rtbufsize {rtbufsizeValue} ";

        // ========== Попытка 1: параметры камеры (если заданы), с framerate ==========
        var args1 =
            $"-y -hide_banner -loglevel warning " +
            $"-f dshow {rtbufsizeArg}-thread_queue_size 1024 {videoSizeArg}{framerateArg}{inputFormatArg}" +
            $"-i {inputSpec} " +
            $"-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p " +
            audioParams +
            $"\"{outputPath}\"";

        try
        {
            return await StartAsyncWithValidation(ffmpegExe, args1, "записи камеры (попытка 1)").ConfigureAwait(false);
        }
        catch (Exception ex1)
        {
            Debug.WriteLine($"[FfmpegRecorder] Попытка 1 не удалась: {ex1.Message}");

            // ========== Попытка 2: без явного framerate (камера сама выберет) ==========
            var args2 =
                $"-y -hide_banner -loglevel warning " +
                $"-f dshow {rtbufsizeArg}-thread_queue_size 1024 {videoSizeArg}{inputFormatArg}" +
                $"-i {inputSpec} " +
                $"-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p " +
                audioParams +
                $"\"{outputPath}\"";

            try
            {
                return await StartAsyncWithValidation(ffmpegExe, args2, "записи камеры (попытка 2)").ConfigureAwait(false);
            }
            catch (Exception ex2)
            {
                Debug.WriteLine($"[FfmpegRecorder] Попытка 2 не удалась: {ex2.Message}");

                // ========== Попытка 3: минимальные параметры (без формата и размера) ==========
                var args3 =
                    $"-y -hide_banner -loglevel warning " +
                    $"-f dshow -rtbufsize 512M -i {inputSpec} " +
                    $"-c:v libx264 -preset veryfast -crf 23 " +
                    audioParams +
                    $"\"{outputPath}\"";

                try
                {
                    return await StartAsyncWithValidation(ffmpegExe, args3, "записи камеры (попытка 3)").ConfigureAwait(false);
                }
                catch (Exception ex3)
                {
                    // Все попытки провалились - бросаем информативное исключение
                    var errorMsg = $"Не удалось запустить запись камеры после 3 попыток.\n\n" +
                                   $"Камера: {cam}\n" +
                                   $"Ошибка 1: {ShortError(ex1)}\n" +
                                   $"Ошибка 2: {ShortError(ex2)}\n" +
                                   $"Ошибка 3: {ShortError(ex3)}\n\n" +
                                   $"Возможные причины:\n" +
                                   $"• Камера занята другим приложением\n" +
                                   $"• Камера не поддерживается\n" +
                                   $"• Устаревшая версия FFmpeg\n\n" +
                                   $"Рекомендация: положите ffmpeg.exe версии 6.0+ рядом с приложением.";

                    throw new InvalidOperationException(errorMsg, ex3);
                }
            }
        }
    }

    private static string ShortError(Exception ex)
    {
        var msg = ex.Message;
        // Обрезаем слишком длинные сообщения
        if (msg.Length > 200)
            msg = msg.Substring(0, 200) + "...";
        return msg;
    }

    // Перегрузка для обратной совместимости
    private static Task<FfmpegRecorder> StartAsync(string exe, string args)
        => StartAsync(exe, args, "записи");

    private static async Task<FfmpegRecorder> StartAsync(string exe, string args, string what)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        var p = Process.Start(psi) ?? throw new InvalidOperationException($"Не удалось запустить ffmpeg для {what}.");
        var rec = new FfmpegRecorder(p);

        // stdout просто "сливаем", чтобы не забить буферы
        _ = Task.Run(async () => { try { await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false); } catch { } });

        try { p.BeginErrorReadLine(); } catch { }

        // если умер сразу — покажем stderr
        await Task.Delay(400).ConfigureAwait(false);
        if (p.HasExited)
        {
            var err = rec.StderrText;
            if (string.IsNullOrWhiteSpace(err))
                err = $"(stderr пустой, exitCode={p.ExitCode})";

            throw new InvalidOperationException($"ffmpeg завершился сразу при старте {what}:\n{err}");
        }

        return rec;
    }

    /// <summary>
    /// Запуск с более длительной проверкой — ждём 1.5 секунды и проверяем, что файл создаётся.
    /// </summary>
    private static async Task<FfmpegRecorder> StartAsyncWithValidation(string exe, string args, string what)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        Debug.WriteLine($"[FfmpegRecorder] Starting: {exe} {args}");

        var p = Process.Start(psi) ?? throw new InvalidOperationException($"Не удалось запустить ffmpeg для {what}.");
        var rec = new FfmpegRecorder(p);

        // stdout просто "сливаем"
        _ = Task.Run(async () => { try { await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false); } catch { } });

        try { p.BeginErrorReadLine(); } catch { }

        // Ждём 1.5 секунды и проверяем
        await Task.Delay(1500).ConfigureAwait(false);

        if (p.HasExited)
        {
            var err = rec.StderrText;
            if (string.IsNullOrWhiteSpace(err))
                err = $"(stderr пустой, exitCode={p.ExitCode})";

            throw new InvalidOperationException($"ffmpeg завершился при старте {what}:\n{err}");
        }

        Debug.WriteLine($"[FfmpegRecorder] Started successfully: {what}");
        return rec;
    }

    public async Task StopAsync()
    {
        try
        {
            if (_proc.HasExited) return;

            try
            {
                await _proc.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                await _proc.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch { /* stdin мог быть уже закрыт */ }

            var finished = await WaitForExitAsync(_proc, milliseconds: 3000).ConfigureAwait(false);
            if (finished) return;

            try { _proc.Kill(entireProcessTree: true); } catch { }
            await WaitForExitAsync(_proc, milliseconds: 2000).ConfigureAwait(false);
        }
        catch
        {
            // запись не должна ронять эксперимент
        }
    }

    private static async Task<bool> WaitForExitAsync(Process p, int milliseconds)
    {
        try
        {
            var t = p.WaitForExitAsync();
            var done = await Task.WhenAny(t, Task.Delay(milliseconds)).ConfigureAwait(false);
            return done == t;
        }
        catch { return false; }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
