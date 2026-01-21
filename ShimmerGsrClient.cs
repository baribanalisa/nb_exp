using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;

namespace NeuroBureau.Experiment;

public sealed class ShimmerGsrClient : IAsyncDisposable
{
    private readonly string _btName;
    private readonly int _port;
    private readonly string _measParamsJson; // JSON-строка (как в exp.json device.params)
    private readonly string _shimmerDir;

    private readonly HttpClient _http;
    private UdpClient? _udp;
    private Process? _proc;

    private readonly object _streamLock = new();
    private FileStream? _stream;

    private CancellationTokenSource? _udpCts;
    private Task? _udpTask;
    private bool _streamingStarted;
    private bool _processStarted;
    private int _stopping; // защита от двойного StopAsync
    
    // legacy ABI размер записи GSRData на Win x64 (double*6 + byte + reserved[32] + padding до 8)


    // [Предположение] иногда отправитель шлёт "плотно" без паддинга (81 байт)
    private const int PackedGsrRecordSize = 81;
    private static readonly byte[] Pad7 = new byte[LegacyGsrRecordSize - PackedGsrRecordSize];
    private string? _udpLogPath;
    private int _udpLogLines;
    private readonly object _udpLogLock = new();
    private int _udpLogLeft = 200; // логируем только первые 200 пакетов

    private string UdpLogPath => Path.Combine(_shimmerDir, "udp_debug.log");

    private static bool LooksLikeText(byte[] b)
    {
        int n = Math.Min(b.Length, 64);
        if (n == 0) return true;

        int printable = 0;
        for (int i = 0; i < n; i++)
        {
            byte x = b[i];
            if (x == 9 || x == 10 || x == 13 || (x >= 32 && x <= 126)) printable++;
        }
        return printable >= (int)(n * 0.90);
    }

    private void LogUdpPacket(byte[] b)
    {
        lock (_udpLogLock)
        {
            if (_udpLogLeft <= 0) return;
            _udpLogLeft--;

            int n = Math.Min(b.Length, 16);
            var hex = BitConverter.ToString(b, 0, n);
            var asText = Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 64))
                .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

            File.AppendAllText(UdpLogPath,
                $"{DateTime.Now:HH:mm:ss.fff} len={b.Length} text?={LooksLikeText(b)} head={hex} ascii='{asText}'\n");
        }
    }

    private const int LegacyGsrRecordSize = 88; // как ждёт анализатор (старый формат)
    private const int WireGsrRecordSize   = 48; // то, что сейчас реально приходит по UDP (6 double)

    private void WriteGsrDatagram(byte[] buf)
    {
        lock (_streamLock)
        {
            var s = _stream;
            if (s == null) return;

            void Log(string decision)
            {
                if (_udpLogPath == null) return;

                _udpLogLines++;
                if (_udpLogLines <= 50 || (_udpLogLines % 200) == 0)
                {
                    try
                    {
                        File.AppendAllText(_udpLogPath,
                            $"{DateTime.Now:HH:mm:ss.fff}\t{buf.Length}\t{decision}\n");
                    }
                    catch { }
                }
            }

            // 1) Уже legacy-формат (88*N)
            if (buf.Length % LegacyGsrRecordSize == 0)
            {
                s.Write(buf, 0, buf.Length);
                Log("write_legacy");
                return;
            }

            // 2) Текущий wire-формат (48*N) -> упаковываем в 88
            if (buf.Length % WireGsrRecordSize == 0)
            {
                int n = buf.Length / WireGsrRecordSize;
                var rec = new byte[LegacyGsrRecordSize]; // переиспользуем один буфер

                for (int i = 0; i < n; i++)
                {
                    Array.Clear(rec, 0, rec.Length);
                    Buffer.BlockCopy(buf, i * WireGsrRecordSize, rec, 0, WireGsrRecordSize);

                    // rec[48] = 0; // battery (необязательно)
                    // остальное нули = reserved + паддинг

                    s.Write(rec, 0, rec.Length);
                }

                Log($"pad48_to_88_x{n}");
                return;
            }

            // 3) Всё остальное — опасно, лучше дропнуть
            Log("drop_unexpected_len");
        }
    }





    public ShimmerGsrClient(string btName, int port, string measParamsJson, string shimmerDir)
    {
        _btName = btName ?? throw new ArgumentNullException(nameof(btName));
        _port = port;
        _measParamsJson = measParamsJson ?? throw new ArgumentNullException(nameof(measParamsJson));
        _shimmerDir = shimmerDir ?? throw new ArgumentNullException(nameof(shimmerDir));

        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{_port}/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    public void SetStream(FileStream? fs)
    {
        lock (_streamLock)
        {
            _stream = fs;

            _udpLogLines = 0;
            _udpLogPath = (fs != null) ? (fs.Name + ".udp.log") : null;

            if (_udpLogPath != null)
            {
                try { File.WriteAllText(_udpLogPath, "time\tlen\tdecision\n"); } catch { }
            }
        }
    }


    public async Task ReadyAsync(CancellationToken ct)
    {
        _streamingStarted = false;
        _stopping = 0; // если вдруг кто-то переиспользует объект

        BindUdp();
        StartUdpLoop();
        StartProgram();

        await WaitPortOpenAsync(ct);
        
        await PostExpectOkAsync("params", _measParamsJson, ct);
        await Task.Delay(500, ct);
        await PostExpectOkAsync("connect", JsonSerializer.Serialize(new { name = _btName }), ct);
        await Task.Delay(3000, ct);
        await PostExpectOkAsync("start", JsonSerializer.Serialize(new { dummy = "" }), ct);

        _streamingStarted = true;
    }



    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return;

        // 1) Сначала рубим UDP так, чтобы ReceiveAsync точно проснулся
        try { _udpCts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null;

        try
        {
            if (_udpTask != null)
                await _udpTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch { /* ignore */ }

        // 2) stop допустим только если start реально был
        if (_streamingStarted)
        {
            try { await PostExpectOkAsync("stop", JsonSerializer.Serialize(new { dummy = "" }), CancellationToken.None); }
            catch { }
        }

        // 3) kill пробуем всегда (в худшем случае упадёт и мы добьём процесс ниже)
        try { await PostExpectOkAsync("kill", JsonSerializer.Serialize(new { dummy = "" }), CancellationToken.None); }
        catch { }

        // 4) Гарантированно прибиваем процесс и ждём, чтобы DLL отпустились
        try
        {
            if (_proc != null && !_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(2000);
            }
        }
        catch { }

        try { _proc?.Dispose(); } catch { }
        _proc = null;

        try { _udpCts?.Dispose(); } catch { }
        _udpCts = null;

        _streamingStarted = false;
    }




    private void BindUdp()
    {
        _udp?.Dispose();

        // строго loopback
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, _port));
    }
    private async Task WaitPortOpenAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var tcp = new TcpClient();
                var connectTask = tcp.ConnectAsync(IPAddress.Loopback, _port);
                var done = await Task.WhenAny(connectTask, Task.Delay(250, ct));
                if (done == connectTask && tcp.Connected)
                    return;
            }
            catch
            {
                // игнор — просто ретрай
            }

            await Task.Delay(200, ct);
        }

        throw new InvalidOperationException("Shimmer HTTP port did not open in time.");
    }

    private void StartUdpLoop()
    {
        _udpCts?.Cancel();
        _udpCts = new CancellationTokenSource();

        var ct = _udpCts.Token;
        _udpTask = Task.Run(async () =>
        {
            if (_udp == null) return;

            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try
                {
#if NET8_0_OR_GREATER
                    r = await _udp.ReceiveAsync(ct);
#else
                    r = await _udp.ReceiveAsync();
                    if (ct.IsCancellationRequested) break;
#endif
                }
                catch
                {
                    if (ct.IsCancellationRequested) break;
                    await Task.Delay(10, ct).ContinueWith(_ => { });
                    continue;
                }
                Debug.WriteLine($"Shimmer UDP len={r.Buffer.Length}");

                // Пишем СЫРЫЕ UDP-байты как есть
                //WriteUdpAsLegacy(r.Buffer);
                LogUdpPacket(r.Buffer);

                if (LooksLikeText(r.Buffer))
                    continue;

                WriteGsrDatagram(r.Buffer); 

            }
        }, ct);
    }
    private const int LegacySize = 88;

    private void WriteUdpAsLegacy(byte[] buf)
    {
        lock (_streamLock)
        {
            var s = _stream;
            if (s == null) return;

            // Если вдруг уже legacy (88) — пишем как есть
            if (buf.Length == LegacySize)
            {
                s.Write(buf, 0, buf.Length);
                return;
            }

            // Главный кейс у тебя: 48 байт
            if (buf.Length == 48)
            {
                // 1) попробуем как 2 записи по 24 байта (6 float каждая)
                if (TryWrite48AsTwoFloatRecords(s, buf))
                    return;

                // 2) иначе как 6 double (одна запись)
                if (TryWrite48AsSixDoubles(s, buf))
                    return;

                // если вообще не распознали — лучше не портить файл
                return;
            }

            // На будущее: если начнут приходить другие длины — пока игнорируем
            // (иначе снова будет "сдвиг столбцов")
        }
    }

    private static bool TryWrite48AsTwoFloatRecords(FileStream s, byte[] buf)
    {
        ReadOnlySpan<byte> b = buf;

        // 2 * 24
        if (b.Length != 48) return false;

        if (!TryRead24Floats(b.Slice(0, 24), out var r1)) return false;
        if (!TryRead24Floats(b.Slice(24, 24), out var r2)) return false;

        if (!LooksPlausible(r1) || !LooksPlausible(r2)) return false;

        WriteLegacyRecord(s, r1.time, r1.hr, r1.sr, r1.sc, r1.range, r1.ppg, battery: 0);
        WriteLegacyRecord(s, r2.time, r2.hr, r2.sr, r2.sc, r2.range, r2.ppg, battery: 0);
        return true;
    }

    private static bool TryWrite48AsSixDoubles(FileStream s, byte[] buf)
    {
        ReadOnlySpan<byte> b = buf;
        if (b.Length != 48) return false;

        double time  = ReadF64LE(b, 0);
        double hr    = ReadF64LE(b, 8);
        double sr    = ReadF64LE(b, 16);
        double sc    = ReadF64LE(b, 24);
        double range = ReadF64LE(b, 32);
        double ppg   = ReadF64LE(b, 40);

        // минимальная вменяемость: не NaN/Inf и HR в разумных пределах
        if (!IsFinite(time) || !IsFinite(hr) || !IsFinite(sr) || !IsFinite(sc) || !IsFinite(range) || !IsFinite(ppg))
            return false;
        if (hr < -1 || hr > 300) return false;

        WriteLegacyRecord(s, time, hr, sr, sc, range, ppg, battery: 0);
        return true;
    }

    private readonly struct Rec
    {
        public readonly double time, hr, sr, sc, range, ppg;
        public Rec(double t, double hr, double sr, double sc, double range, double ppg)
            => (time, this.hr, this.sr, this.sc, this.range, this.ppg) = (t, hr, sr, sc, range, ppg);
    }

    private static bool TryRead24Floats(ReadOnlySpan<byte> b, out Rec rec)
    {
        rec = default;
        if (b.Length != 24) return false;

        double time  = ReadF32LE(b, 0);
        double hr    = ReadF32LE(b, 4);
        double sr    = ReadF32LE(b, 8);
        double sc    = ReadF32LE(b, 12);
        double range = ReadF32LE(b, 16);
        double ppg   = ReadF32LE(b, 20);

        rec = new Rec(time, hr, sr, sc, range, ppg);
        return true;
    }

    private static bool LooksPlausible(Rec r)
    {
        if (!IsFinite(r.time) || !IsFinite(r.hr) || !IsFinite(r.sr) || !IsFinite(r.sc) || !IsFinite(r.range) || !IsFinite(r.ppg))
            return false;
        if (r.hr < -1 || r.hr > 300) return false;

        // остальное не зажимаю жёстко, потому что диапазоны зависят от режима Shimmer
        return true;
    }

    private static bool IsFinite(double v) => !(double.IsNaN(v) || double.IsInfinity(v));

    private static float ReadF32LE(ReadOnlySpan<byte> b, int offset)
    {
        int i = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(offset, 4));
        return BitConverter.Int32BitsToSingle(i);
    }

    private static double ReadF64LE(ReadOnlySpan<byte> b, int offset)
    {
        long i = BinaryPrimitives.ReadInt64LittleEndian(b.Slice(offset, 8));
        return BitConverter.Int64BitsToDouble(i);
    }

    private static void WriteLegacyRecord(FileStream s, double time, double hr, double sr, double sc, double range, double ppg, byte battery)
    {
        Span<byte> rec = stackalloc byte[LegacySize];
        int o = 0;

        WriteF64LE(rec, ref o, time);
        WriteF64LE(rec, ref o, hr);
        WriteF64LE(rec, ref o, sr);
        WriteF64LE(rec, ref o, sc);
        WriteF64LE(rec, ref o, range);
        WriteF64LE(rec, ref o, ppg);

        rec[o++] = battery;

        // reserved[32]
        rec.Slice(o, 32).Clear(); o += 32;

        // padding до 88
        rec.Slice(o).Clear();

        s.Write(rec);
    }

    private static void WriteF64LE(Span<byte> b, ref int o, double v)
    {
        long bits = BitConverter.DoubleToInt64Bits(v);
        BinaryPrimitives.WriteInt64LittleEndian(b.Slice(o, 8), bits);
        o += 8;
    }

    private void StartProgram()
    {
        if (_proc != null && !_proc.HasExited)
            throw new InvalidOperationException("Shimmer.exe already running.");

        var exePath = Path.Combine(_shimmerDir, "Shimmer.exe");
        if (!File.Exists(exePath))
            throw new FileNotFoundException("Shimmer.exe not found", exePath);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = _shimmerDir,
            Arguments = $"gsr {_port}",      // НЕ хардкодь 4000 — у тебя порт уже в _port
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // чтобы exe видел dll рядом (на всякий)
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        psi.Environment["PATH"] = _shimmerDir + ";" + path;

        try
        {
            _proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (System.ComponentModel.Win32Exception wex)
        {
            throw new InvalidOperationException(
                $"Start failed. Exe={exePath} Dir={_shimmerDir} Args='{psi.Arguments}' " +
                $"Win32Code={wex.NativeErrorCode} Msg={wex.Message}", wex);
        }
    }




    private async Task WaitHttpAsync(CancellationToken ct)
    {
        // простой ретрай: Shimmer.exe иногда поднимает HTTP не мгновенно
        for (int i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await _http.GetAsync("", ct);
                // нам не важен код, важен факт, что TCP/HTTP жив
                return;
            }
            catch
            {
                await Task.Delay(200, ct);
            }
        }

        throw new InvalidOperationException("Shimmer HTTP endpoint did not become ready.");
    }

    private async Task PostExpectOkAsync(string service, string jsonBody, CancellationToken ct)
    {
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(service, content, ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Shimmer HTTP {service}: HTTP {(int)resp.StatusCode}");

        var s = await resp.Content.ReadAsStringAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(s);
            if (!doc.RootElement.TryGetProperty("result", out var r) || r.GetInt32() != 0)
                throw new InvalidOperationException($"Shimmer HTTP {service}: result != 0, body={s}");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Shimmer HTTP {service}: invalid JSON, body={s}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }
}
