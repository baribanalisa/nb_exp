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

public readonly record struct ShimmerDataPoint(
    double Time,
    double HeartRate,
    double SkinResistance,
    double SkinConductance,
    double Range,
    double Ppg,
    int BatteryPercent // 0..100, -1 = неизвестно
);

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

    // Shimmer: последнее известное значение батареи (0..100), -1 = неизвестно
    private int _lastBatteryPercent = -1;

    public event Action<ShimmerDataPoint>? DataReceived;

    
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

    private void UpdateLastBatteryPercent(int batteryPercent)
    {
        if (batteryPercent < 0 || batteryPercent > 100) return;
        Volatile.Write(ref _lastBatteryPercent, batteryPercent);
    }

    private int GetLastBatteryPercent() => Volatile.Read(ref _lastBatteryPercent);

    private static byte BatteryPercentToLegacyByte(int batteryPercent)
        => (batteryPercent >= 0 && batteryPercent <= 100) ? (byte)batteryPercent : (byte)0;

    private static int BatteryVoltageToPercent(double voltage)
    {
        // грубая линейная аппроксимация Li-Ion (окно 3.3..4.2В)
        const double vMin = 3.3;
        const double vMax = 4.2;

        double x = (voltage - vMin) / (vMax - vMin);
        if (x < 0) x = 0;
        if (x > 1) x = 1;

        return (int)Math.Round(x * 100.0);
    }

    private static int NormalizeBatteryToPercent(double raw)
    {
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return -1;

        // доля 0..1
        if (raw >= 0.0 && raw <= 1.0)
            return (int)Math.Round(raw * 100.0);

        // уже проценты 0..100
        if (raw >= 0.0 && raw <= 100.0)
            return (int)Math.Round(raw);

        // 8-bit шкала 0..255
        if (raw >= 0.0 && raw <= 255.0)
            return (int)Math.Round(raw / 255.0 * 100.0);

        // напряжение в вольтах (типичный диапазон Li-Ion)
        if (raw >= 2.8 && raw <= 4.5)
            return BatteryVoltageToPercent(raw);

        return -1;
    }

    private static bool TryConvertJsonNumber(JsonElement el, out double value)
    {
        value = double.NaN;
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return el.TryGetDouble(out value);

            case JsonValueKind.String:
            {
                var s = el.GetString();
                return s != null && double.TryParse(
                    s,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value);
            }

            default:
                return false;
        }
    }

    private static bool TryGetBatteryValue(JsonElement root, out double raw)
    {
        raw = double.NaN;

        string[] keys =
        {
            "battery", "batt", "bat",
            "batteryPercent", "battery_percent", "batteryPct", "battery_pct",
            "batteryVoltage", "battery_voltage",
            "vbatt", "vbat"
        };

        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var prop))
            {
                if (TryConvertJsonNumber(prop, out raw))
                    return true;
            }
        }

        return false;
    }

    private static bool TryExtractNumberAfterIndex(string s, int startIndex, out double value)
    {
        value = double.NaN;

        if (string.IsNullOrEmpty(s))
            return false;

        if (startIndex < 0) startIndex = 0;
        if (startIndex >= s.Length) startIndex = 0;

        int i = startIndex;
        while (i < s.Length && !(char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.'))
            i++;

        if (i >= s.Length)
            return false;

        int j = i;
        while (j < s.Length)
        {
            char c = s[j];
            if (char.IsDigit(c) || c == '-' || c == '+' || c == '.' || c == ',' || c == 'e' || c == 'E')
                j++;
            else
                break;
        }

        var numStr = s.Substring(i, j - i).Trim();
        numStr = numStr.TrimEnd('%').Replace(',', '.');

        return double.TryParse(
            numStr,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }

    private static bool TryParseBatteryPercentFromText(byte[] buf, out int batteryPercent)
    {
        batteryPercent = -1;
        if (buf.Length == 0) return false;

        string s;
        try { s = Encoding.UTF8.GetString(buf).Trim(); }
        catch { s = Encoding.ASCII.GetString(buf).Trim(); }

        if (string.IsNullOrWhiteSpace(s))
            return false;

        // JSON: {"battery":90} / {"batteryVoltage":3.9} / ...
        if (s.Length >= 2 && s[0] == '{' && s[^1] == '}')
        {
            try
            {
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;

                if (TryGetBatteryValue(root, out var rawJson))
                {
                    int pct = NormalizeBatteryToPercent(rawJson);
                    if (pct >= 0 && pct <= 100)
                    {
                        batteryPercent = pct;
                        return true;
                    }
                }
            }
            catch
            {
                // не JSON — пробуем дальше
            }
        }

        // Текст/kv: "battery=90", "batt: 90%", "bat 3.9V" и т.п.
        string lower = s.ToLowerInvariant();
        int idx = lower.IndexOf("battery", StringComparison.Ordinal);
        if (idx < 0) idx = lower.IndexOf("batt", StringComparison.Ordinal);
        if (idx < 0) idx = lower.IndexOf("bat", StringComparison.Ordinal);
        if (idx < 0) idx = 0;

        if (TryExtractNumberAfterIndex(s, idx, out var rawText))
        {
            int pct = NormalizeBatteryToPercent(rawText);
            if (pct >= 0 && pct <= 100)
            {
                batteryPercent = pct;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ищет battery в любом месте JSON (включая вложенные объекты/массивы).
    /// Возвращает проценты 0..100.
    /// </summary>
    private static bool TryFindBatteryPercentRecursive(JsonElement el, out int batteryPercent)
    {
        batteryPercent = -1;

        static bool IsBatteryKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var k = name.Trim().ToLowerInvariant();

            return k is "battery" or "batt" or "bat"
                or "batterypercent" or "battery_percent" or "batterypct" or "battery_pct"
                or "batteryvoltage" or "battery_voltage"
                or "vbatt" or "vbat";
        }

        static bool TryParseBatteryFromObject(JsonElement obj, out int pct)
        {
            pct = -1;
            if (obj.ValueKind != JsonValueKind.Object) return false;

            foreach (var prop in obj.EnumerateObject())
            {
                if (!IsBatteryKey(prop.Name))
                    continue;

                // Число или строка-число
                if (TryConvertJsonNumber(prop.Value, out var raw))
                {
                    int p = NormalizeBatteryToPercent(raw);
                    if (p >= 0 && p <= 100)
                    {
                        pct = p;
                        return true;
                    }
                }

                // Строка может быть вида "3.9V" / "90%" — попробуем вытащить число
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var s = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && TryExtractNumberAfterIndex(s!, 0, out var raw2))
                    {
                        int p = NormalizeBatteryToPercent(raw2);
                        if (p >= 0 && p <= 100)
                        {
                            pct = p;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        if (TryParseBatteryFromObject(el, out var direct))
        {
            batteryPercent = direct;
            return true;
        }

        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (TryFindBatteryPercentRecursive(p.Value, out var found))
                    {
                        batteryPercent = found;
                        return true;
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    if (TryFindBatteryPercentRecursive(item, out var found))
                    {
                        batteryPercent = found;
                        return true;
                    }
                }
                break;
        }

        return false;
    }

    private const int LegacyGsrRecordSize = 88; // как ждёт анализатор (старый формат)


    private const int WireGsrRecordSize   = 48; // то, что сейчас реально приходит по UDP (6 double)

    private void WriteGsrDatagram(byte[] buf)
    {
        lock (_streamLock)
        {
            var s = _stream;

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

            // 1) Уже legacy-формат (88*N): пишем как есть, плюс извлекаем батарею/данные
            if (buf.Length % LegacyGsrRecordSize == 0)
            {
                if (s != null)
                {
                    s.Write(buf, 0, buf.Length);
                }

                int n = buf.Length / LegacyGsrRecordSize;
                for (int i = 0; i < n; i++)
                {
                    RaiseDataReceivedEventLegacy(buf, i * LegacyGsrRecordSize, LegacyGsrRecordSize);
                }

                Log("write_legacy");
                return;
            }

            // 1.1) Иногда отправитель шлёт "плотно" без паддинга (81*N)
            if (buf.Length % PackedGsrRecordSize == 0)
            {
                int n = buf.Length / PackedGsrRecordSize;

                for (int i = 0; i < n; i++)
                {
                    int off = i * PackedGsrRecordSize;

                    if (s != null)
                    {
                        s.Write(buf, off, PackedGsrRecordSize);
                        if (Pad7.Length > 0)
                            s.Write(Pad7, 0, Pad7.Length);
                    }

                    RaiseDataReceivedEventLegacy(buf, off, PackedGsrRecordSize);
                }

                Log($"pad81_to_88_x{n}");
                return;
            }

            // 2) Текущий wire-формат (48*N) -> упаковываем в 88 (и дописываем батарею, если удалось узнать)
            if (buf.Length % WireGsrRecordSize == 0)
            {
                int n = buf.Length / WireGsrRecordSize;
                var rec = new byte[LegacyGsrRecordSize]; // переиспользуем один буфер

                for (int i = 0; i < n; i++)
                {
                    Array.Clear(rec, 0, rec.Length);
                    Buffer.BlockCopy(buf, i * WireGsrRecordSize, rec, 0, WireGsrRecordSize);

                    rec[48] = BatteryPercentToLegacyByte(GetLastBatteryPercent()); // battery (0..100) или 0 если неизвестно

                    if (s != null)
                    {
                        s.Write(rec, 0, rec.Length);
                    }

                    // Извлекаем данные для события (из первых 48 байт)
                    RaiseDataReceivedEvent(buf, i * WireGsrRecordSize, WireGsrRecordSize);
                }

                Log($"pad48_to_88_x{n}");
                return;
            }

            // 3) Всё остальное — опасно, лучше дропнуть
            Log("drop_unexpected_len");
        }
    }


    private void RaiseDataReceivedEvent(byte[] buf, int offset, int length)
    {
        try
        {
            if (DataReceived == null || length != WireGsrRecordSize) return;

            ReadOnlySpan<byte> b = new ReadOnlySpan<byte>(buf, offset, length);

            double time = ReadF64LE(b, 0);
            double hr = ReadF64LE(b, 8);
            double sr = ReadF64LE(b, 16);
            double sc = ReadF64LE(b, 24);
            double range = ReadF64LE(b, 32);
            double ppg = ReadF64LE(b, 40);

            if (!IsFinite(time) || !IsFinite(hr) || !IsFinite(sr) || !IsFinite(sc) || !IsFinite(range) || !IsFinite(ppg))
                return;

            int batteryPercent = GetLastBatteryPercent();
            var dataPoint = new ShimmerDataPoint(time, hr, sr, sc, range, ppg, batteryPercent);
            DataReceived?.Invoke(dataPoint);
        }
        catch
        {
            // Игнорируем ошибки в обработчиках событий
        }
    }

    private void RaiseDataReceivedEventLegacy(byte[] buf, int offset, int length)
    {
        try
        {
            // должны быть 6 double (48) + byte батареи (минимум 49)
            if (length < (WireGsrRecordSize + 1)) return;

            // батарея: байт в позиции 48 (после 6 double)
            int raw = buf[offset + WireGsrRecordSize];
            int pct = NormalizeBatteryToPercent(raw);
            if (pct >= 0 && pct <= 100)
                UpdateLastBatteryPercent(pct);

            if (DataReceived == null) return;

            ReadOnlySpan<byte> b = new ReadOnlySpan<byte>(buf, offset, WireGsrRecordSize);

            double time = ReadF64LE(b, 0);
            double hr = ReadF64LE(b, 8);
            double sr = ReadF64LE(b, 16);
            double sc = ReadF64LE(b, 24);
            double range = ReadF64LE(b, 32);
            double ppg = ReadF64LE(b, 40);

            if (!IsFinite(time) || !IsFinite(hr) || !IsFinite(sr) || !IsFinite(sc) || !IsFinite(range) || !IsFinite(ppg))
                return;

            int batteryPercent = (pct >= 0 && pct <= 100) ? pct : GetLastBatteryPercent();
            var dataPoint = new ShimmerDataPoint(time, hr, sr, sc, range, ppg, batteryPercent);
            DataReceived?.Invoke(dataPoint);
        }
        catch
        {
            // Игнорируем ошибки в обработчиках событий
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

        await WaitPortOpenAsync(ct).ConfigureAwait(false);

        // Best-effort: некоторые сборки Shimmer.exe кладут battery в GET /
        try
        {
            using var resp = await _http.GetAsync("", ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (TryFindBatteryPercentRecursive(doc.RootElement, out var pct))
                            UpdateLastBatteryPercent(pct);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        await PostExpectOkAsync("params", _measParamsJson, ct).ConfigureAwait(false);
        await Task.Delay(500, ct).ConfigureAwait(false);
        await PostExpectOkAsync("connect", JsonSerializer.Serialize(new { name = _btName }), ct).ConfigureAwait(false);
        await Task.Delay(3000, ct).ConfigureAwait(false);
        await PostExpectOkAsync("start", JsonSerializer.Serialize(new { dummy = "" }), ct).ConfigureAwait(false);

        _streamingStarted = true;
    }


    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return;

        // 1) Сначала рубим UDP так, чтобы ReceiveAsync точно проснулся и порт освободился
        try { _udpCts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null;

        try
        {
            if (_udpTask != null)
                await _udpTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch { /* ignore */ }

        // 2) Пытаемся корректно остановить Shimmer.exe через HTTP, но строго с коротким таймаутом.
        // Важно: Dispose/закрытие окна не должен зависать на HttpClient.Timeout (60s).
        if (_streamingStarted)
        {
            try
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                await PostExpectOkAsync("stop", JsonSerializer.Serialize(new { dummy = "" }), stopCts.Token).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }

        try
        {
            using var killCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            await PostExpectOkAsync("kill", JsonSerializer.Serialize(new { dummy = "" }), killCts.Token).ConfigureAwait(false);
        }
        catch { /* ignore */ }

        // 3) Гарантированно прибиваем процесс и ждём, чтобы порт/BT реально освободились
        try
        {
            if (_proc != null && !_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(2000);
            }
        }
        catch { /* ignore */ }

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
                var done = await Task.WhenAny(connectTask, Task.Delay(250, ct)).ConfigureAwait(false);
                if (done == connectTask && tcp.Connected)
                    return;
            }
            catch
            {
                // игнор — просто ретрай
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("HTTP-порт Shimmer не открылся вовремя.");
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
                    r = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
#else
                    r = await _udp.ReceiveAsync().ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;
#endif
                }
                catch
                {
                    if (ct.IsCancellationRequested) break;

                    try { await Task.Delay(10, ct).ConfigureAwait(false); } catch { }
                    continue;
                }

                Debug.WriteLine($"Shimmer UDP len={r.Buffer.Length}");

                // Пишем СЫРЫЕ UDP-байты как есть
                //WriteUdpAsLegacy(r.Buffer);
                LogUdpPacket(r.Buffer);

                if (LooksLikeText(r.Buffer))
                {
                    if (TryParseBatteryPercentFromText(r.Buffer, out var pct))
                        UpdateLastBatteryPercent(pct);

                    continue;
                }

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

        double time = ReadF64LE(b, 0);
        double hr = ReadF64LE(b, 8);
        double sr = ReadF64LE(b, 16);
        double sc = ReadF64LE(b, 24);
        double range = ReadF64LE(b, 32);
        double ppg = ReadF64LE(b, 40);

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

        double time = ReadF32LE(b, 0);
        double hr = ReadF32LE(b, 4);
        double sr = ReadF32LE(b, 8);
        double sc = ReadF32LE(b, 12);
        double range = ReadF32LE(b, 16);
        double ppg = ReadF32LE(b, 20);

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
            throw new InvalidOperationException("Shimmer.exe уже запущен.");

        var exePath = Path.Combine(_shimmerDir, "Shimmer.exe");
        if (!File.Exists(exePath))
            throw new FileNotFoundException("Shimmer.exe не найден", exePath);

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
            _proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start вернул null");
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
                using var resp = await _http.GetAsync("", ct).ConfigureAwait(false);
                // нам не важен код, важен факт, что TCP/HTTP жив
                return;
            }
            catch
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("HTTP-эндпоинт Shimmer не стал готовым.");
    }

    private async Task PostExpectOkAsync(string service, string jsonBody, CancellationToken ct)
    {
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(service, content, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Shimmer HTTP {service}: HTTP {(int)resp.StatusCode}");

        var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;

            if (!root.TryGetProperty("result", out var r))
                throw new InvalidOperationException($"Shimmer HTTP {service}: нет поля result, body={s}");

            int resultCode;
            if (r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var n))
                resultCode = n;
            else if (r.ValueKind == JsonValueKind.String && int.TryParse(r.GetString(), out var ns))
                resultCode = ns;
            else
                throw new InvalidOperationException($"Shimmer HTTP {service}: неверный тип result, body={s}");

            if (resultCode != 0)
                throw new InvalidOperationException($"Shimmer HTTP {service}: result != 0, body={s}");

            // Best-effort: батарея может приходить в ответах params/connect/start/...
            if (TryFindBatteryPercentRecursive(root, out var pct))
                UpdateLastBatteryPercent(pct);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Shimmer HTTP {service}: некорректный JSON, body={s}");
        }
    }


    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _http.Dispose();
    }
}
