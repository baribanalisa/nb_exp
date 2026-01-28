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

        // <- теперь компилится (есть перегрузка StartAsync(exe,args))
        return StartAsync(ffmpegExe, args);
    }

    /// <summary>
    /// Камера (+ звук) live-mux сразу в один файл (как в твоей консольной команде).
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

        var fps = string.IsNullOrWhiteSpace(framerate) ? "30" : framerate.Trim();
        var format = string.IsNullOrWhiteSpace(inputFormat) ? null : inputFormat.Trim();
        var size = string.IsNullOrWhiteSpace(videoSize) ? null : videoSize.Trim();
        var rtbuf = string.IsNullOrWhiteSpace(rtbufsize) ? "256M" : rtbufsize.Trim();


        // ВАЖНО: делаем ровно как в PowerShell: один dshow input video:audio
        var inputSpec = (recordAudio && !string.IsNullOrWhiteSpace(mic))
            ? $"video=\"{cam}\":audio=\"{mic}\""
            : $"video=\"{cam}\"";

        var inputFormatArg = string.IsNullOrWhiteSpace(format) ? "" : $"-input_format {format} ";
        var videoSizeArg = string.IsNullOrWhiteSpace(size) ? "" : $"-video_size {size} ";

        var args =
            $"-y -hide_banner -loglevel error " +
            $"-f dshow -rtbufsize {rtbuf} -framerate {fps} {inputFormatArg}{videoSizeArg}-i {inputSpec} " +
            $"-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p " +
            (recordAudio && !string.IsNullOrWhiteSpace(mic)
                ? "-c:a aac -b:a 128k -ar 48000 -ac 2 -shortest "
                : "") +
            $"\"{outputPath}\"";

        return await StartAsync(ffmpegExe, args, "записи камеры").ConfigureAwait(false);
    }

    // Перегрузка, чтобы не чинить все места, где было StartAsync(exe,args)
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

        // stdout просто “сливаем”, чтобы не забить буферы
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
