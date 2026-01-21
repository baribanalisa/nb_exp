using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroBureau.Experiment;

public sealed class TrackerClient
{
    private readonly HttpClient _http;

    public TrackerClient(string baseUrl = "http://127.0.0.1:4242")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    // В файле TrackerClient.cs замените метод SendRawCommandAsync на этот:

    public async Task<string> SendRawCommandAsync(string xml, CancellationToken ct = default)
    {
        var cmd = xml + " \r\n";
        var url = "/commands?command=" + Uri.EscapeDataString(cmd);

        // Пробуем 5 раз перед тем, как сдаться
        int maxRetries = 5;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                using var resp = await _http.PostAsync(url, content: null, ct);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct);
            }
            catch (Exception)
            {
                // Если это последняя попытка — выбрасываем ошибку
                if (i == maxRetries - 1) throw;

                // Ждем с нарастанием (200, 400, 600 мс...), если связь барахлит
                await Task.Delay(200 * (i + 1), ct);
            }
        }

        throw new InvalidOperationException("Unreachable code");
    }

    public Task EnableAsync(string id, bool enabled, CancellationToken ct = default) =>
        SendRawCommandAsync($"<SET ID=\"{id}\" STATE=\"{(enabled ? 1 : 0)}\" />", ct);

    public Task StartCalibrationAsync(CancellationToken ct = default) =>
        SendRawCommandAsync("<SET ID=\"CALIBRATE_START\" STATE=\"1\" />", ct);

    public async Task<int?> GetCalibrationStateAsync(CancellationToken ct = default)
    {
        var ans = await SendRawCommandAsync("<GET ID=\"CALIBRATE_START\" />", ct);
        // ответ типа: <ACK ID="CALIBRATE_START" STATE="0" ... />
        var state = TryGetAttr(ans, "STATE");
        if (state == null) return null;
        return int.TryParse(state, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public async Task<List<string>> GetRecordsAsync(int nRecords, int ackNumber, CancellationToken ct = default)
    {
        var url = $"/records?n_records={nRecords}&ack_number={ackNumber}";
        var s = await _http.GetStringAsync(url, ct);

        var lines = s.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        return new List<string>(lines);
    }
    public async Task<(bool ok, int lastAck)> WaitForAnyRecordAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + timeout;
        int lastAck = 0;

        while (DateTime.UtcNow < until)
        {
            try
            {
                var lines = await GetRecordsAsync(nRecords: 5, ackNumber: lastAck, ct: ct);
                foreach (var line in lines)
                {
                    if (!line.Contains("<REC", StringComparison.OrdinalIgnoreCase)) continue;

                    var cntS = TryGetAttr(line, "CNT");
                    var timeS = TryGetAttr(line, "TIME");
                    if (cntS == null || timeS == null) continue;

                    if (int.TryParse(cntS, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cnt))
                    {
                        lastAck = Math.Max(lastAck, cnt);
                        return (true, lastAck);
                    }
                }
            }
            catch
            {
                // трекер недоступен / сеть / таймаут — просто ждём до конца timeout
            }

            await Task.Delay(150, ct);
        }

        return (false, lastAck);
    }

    private static readonly Regex AttrRx = new(@"(\w+)\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    public static string? TryGetAttr(string xmlLine, string name)
    {
        foreach (Match m in AttrRx.Matches(xmlLine))
        {
            if (string.Equals(m.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase))
                return m.Groups[2].Value.Trim();
        }
        return null;
    }
}
