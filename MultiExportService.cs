// File: MultiExportService.cs
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using ClosedXML.Excel;

namespace NeuroBureau.Experiment;

public sealed class MultiExportService
{
    private readonly string _expDir;
    private readonly ExperimentFile _exp;
    private readonly string _resultsDir;

    private readonly string _trackerUid;
    private readonly string? _mouseKbdUid;
    private readonly string? _shimmerUid;
    private readonly bool _hasEeg;

    private readonly FilenameTemplateResolver _resolver = new();

    private const int TrackerRecordSize = 160; // TrackerData.Size
    private const int MkRecordSize = 32;

    public MultiExportService(string expDir, ExperimentFile exp)
    {
        _expDir = expDir;
        _exp = exp;
        _resultsDir = Path.Combine(expDir, "results");

        var devices = exp.Devices ?? new List<DeviceFile>();

        _trackerUid = devices.FirstOrDefault(d => (d.DevType ?? "").IndexOf("tracker", StringComparison.OrdinalIgnoreCase) >= 0)?.Uid
                      ?? "tracker";

        _mouseKbdUid = devices.FirstOrDefault(d => (d.DevType ?? "").IndexOf("mouse", StringComparison.OrdinalIgnoreCase) >= 0)?.Uid;
        _shimmerUid = devices.FirstOrDefault(d => (d.DevType ?? "").IndexOf("shimmer", StringComparison.OrdinalIgnoreCase) >= 0)?.Uid;
        _hasEeg = devices.Any(d => (d.DevType ?? "").IndexOf("eeg", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public void Export(
        MultiExportOptions options,
        IReadOnlyList<StimulFile> stimuli,
        IReadOnlyList<MultiExportResult> results,
        Action<string>? progress,
        CancellationToken ct)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (stimuli == null) throw new ArgumentNullException(nameof(stimuli));
        if (results == null) throw new ArgumentNullException(nameof(results));

        if (stimuli.Count == 0) throw new InvalidOperationException("Не выбраны стимулы.");
        if (results.Count == 0) throw new InvalidOperationException("Не выбраны результаты.");
        if (string.IsNullOrWhiteSpace(options.OutputDir)) throw new InvalidOperationException("Не задана папка экспорта.");

        Directory.CreateDirectory(options.OutputDir);

        if (!_resolver.TryValidate(options.FilenameTemplate, _exp, out var err))
            throw new InvalidOperationException("Шаблон имени файла: " + err);

        if (options.Mode == MultiExportMode.AllInOne && (options.ExportRaw || options.ExportSource))
            throw new InvalidOperationException("В режиме «Все в одном» запрещены сырые/исходные данные.");

        if (options.ExportEdf && (!_hasEeg || options.Mode != MultiExportMode.SeparateFiles))
            throw new InvalidOperationException("EDF доступен только в режиме «Отдельные файлы» и только если в эксперименте есть ЭЭГ.");

        var now = DateTime.Now;
        var resultByUid = results.ToDictionary(r => r.Uid, r => r, StringComparer.OrdinalIgnoreCase);

        void Report(string s) => progress?.Invoke(s);

        Report($"Мультиэкспорт: режим={options.Mode}, стимулов={stimuli.Count}, результатов={results.Count}");

        switch (options.Mode)
        {
            case MultiExportMode.SeparateFiles:
                ExportSeparateFiles(options, stimuli, results, resultByUid, now, Report, ct);
                break;

            case MultiExportMode.FilePerStimul:
                ExportFilePerStimul(options, stimuli, results, resultByUid, now, Report, ct);
                break;

            case MultiExportMode.FilePerResult:
                ExportFilePerResult(options, stimuli, results, resultByUid, now, Report, ct);
                break;

            case MultiExportMode.AllInOne:
                ExportAllInOne(options, stimuli, results, resultByUid, now, Report, ct);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(options.Mode), options.Mode, "Неизвестный режим");
        }

        Report("Мультиэкспорт: готово");
    }

    private void ExportSeparateFiles(
        MultiExportOptions options,
        IReadOnlyList<StimulFile> stimuli,
        IReadOnlyList<MultiExportResult> results,
        Dictionary<string, MultiExportResult> resultByUid,
        DateTime now,
        Action<string> report,
        CancellationToken ct)
    {
        int total = stimuli.Count * results.Count;
        int done = 0;

        foreach (var st in stimuli)
        {
            foreach (var rr in results)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                report($"Экспорт {done}/{total}: stim={st.Uid}, result={rr.Uid}");

                if (options.ExportSource)
                    ExportSourceFiles(options, now, rr, st, report);

                if (options.ExportRaw)
                    ExportRawGaze(options, now, rr, st);

                if (options.ExportActions)
                    ExportActions(options, now, rr, st);

                if (options.ExportAoi)
                    ExportAoi(options, now, rr, st);

                if (options.ExportGazeImage)
                    ExportPrebuiltImageIfExists(options, now, rr, st, "gaze", report);

                if (options.ExportHeatImage)
                    ExportPrebuiltImageIfExists(options, now, rr, st, "heat", report);

                if (options.ExportEdf)
                    ExportEdfIfExists(options, now, rr, report);
            }
        }
    }

    private void ExportFilePerStimul(
        MultiExportOptions options,
        IReadOnlyList<StimulFile> stimuli,
        IReadOnlyList<MultiExportResult> results,
        Dictionary<string, MultiExportResult> resultByUid,
        DateTime now,
        Action<string> report,
        CancellationToken ct)
    {
        int done = 0;

        foreach (var st in stimuli)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            report($"Экспорт (FilePerStimul) {done}/{stimuli.Count}: stim={st.Uid}");

            if (options.ExportSource)
            {
                foreach (var rr in results)
                    ExportSourceFiles(options, now, rr, st, report);
            }

            if (options.ExportRaw)
                ExportRawGaze_AggregatedPerStimul(options, now, results, st);

            if (options.ExportActions)
                ExportActions_AggregatedPerStimul(options, now, results, st);

            if (options.ExportAoi)
            {
                ExportAoi(options, now, results[0], st);
            }
        }
    }

    private void ExportFilePerResult(
        MultiExportOptions options,
        IReadOnlyList<StimulFile> stimuli,
        IReadOnlyList<MultiExportResult> results,
        Dictionary<string, MultiExportResult> resultByUid,
        DateTime now,
        Action<string> report,
        CancellationToken ct)
    {
        int done = 0;

        foreach (var rr in results)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            report($"Экспорт (FilePerResult) {done}/{results.Count}: result={rr.Uid}");

            if (options.ExportSource)
            {
                foreach (var st in stimuli)
                    ExportSourceFiles(options, now, rr, st, report);
            }

            if (options.ExportRaw)
                ExportRawGaze_AggregatedPerResult(options, now, rr, stimuli);

            if (options.ExportActions)
                ExportActions_AggregatedPerResult(options, now, rr, stimuli);

            if (options.ExportAoi)
                ExportAoi_AggregatedPerResult(options, now, rr, stimuli);
        }
    }

    private void ExportAllInOne(
        MultiExportOptions options,
        IReadOnlyList<StimulFile> stimuli,
        IReadOnlyList<MultiExportResult> results,
        Dictionary<string, MultiExportResult> resultByUid,
        DateTime now,
        Action<string> report,
        CancellationToken ct)
    {
        if (options.ExportAoi)
            ExportAoi_AllInOne(options, now, results, stimuli);

        if (options.ExportGazeImage || options.ExportHeatImage)
        {
            foreach (var st in stimuli)
            {
                foreach (var rr in results)
                {
                    ct.ThrowIfCancellationRequested();
                    if (options.ExportGazeImage)
                        ExportPrebuiltImageIfExists(options, now, rr, st, "gaze", report);
                    if (options.ExportHeatImage)
                        ExportPrebuiltImageIfExists(options, now, rr, st, "heat", report);
                }
            }
        }
    }

    // ===== Source =====

    private void ExportSourceFiles(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st, Action<string> report)
    {
        CopyIfExists(Path.Combine(_resultsDir, rr.Uid, st.Uid, _trackerUid), "source_tracker", "bin");

        if (!string.IsNullOrWhiteSpace(_mouseKbdUid))
            CopyIfExists(Path.Combine(_resultsDir, rr.Uid, st.Uid, _mouseKbdUid!), "source_actions", "bin");

        if (!string.IsNullOrWhiteSpace(_shimmerUid))
            CopyIfExists(Path.Combine(_resultsDir, rr.Uid, st.Uid, _shimmerUid!), "source_gsr", "bin");

        void CopyIfExists(string src, string type, string ext)
        {
            if (!File.Exists(src)) return;
            var name = BuildFileName(options, now, rr, st, type, ext);
            var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
    }

    // ===== Raw Gaze =====

    private void ExportRawGaze(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st)
    {
        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, rr, st, "raw_gaze", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        var src = Path.Combine(_resultsDir, rr.Uid, st.Uid, _trackerUid);
        if (!File.Exists(src)) return;

        var data = ReadTrackerData(src);

        if (options.DataFormat == ExportDataFormat.XLSX)
            WriteTrackerDataXlsx(dst, data, includeStimulColumn: false, includeResultColumn: false);
        else
            WriteTrackerDataCsv(dst, data, includeStimulColumn: false, includeResultColumn: false);
    }

    private void ExportRawGaze_AggregatedPerStimul(MultiExportOptions options, DateTime now, IReadOnlyList<MultiExportResult> results, StimulFile st)
    {
        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, results[0], st, "raw_gaze_per_stimul", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        var allData = new List<(string resultUid, TrackerRecord r)>();

        foreach (var rr in results)
        {
            var src = Path.Combine(_resultsDir, rr.Uid, st.Uid, _trackerUid);
            if (!File.Exists(src)) continue;

            foreach (var r in ReadTrackerData(src))
                allData.Add((rr.Uid, r));
        }

        if (options.DataFormat == ExportDataFormat.XLSX)
            WriteTrackerDataXlsx(dst, allData, includeResultColumn: true);
        else
            WriteTrackerDataCsv(dst, allData, includeResultColumn: true);
    }

    private void ExportRawGaze_AggregatedPerResult(MultiExportOptions options, DateTime now, MultiExportResult rr, IReadOnlyList<StimulFile> stimuli)
    {
        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, rr, stimuli[0], "raw_gaze_per_result", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        var allData = new List<(string stimUid, TrackerRecord r)>();

        foreach (var st in stimuli)
        {
            var src = Path.Combine(_resultsDir, rr.Uid, st.Uid, _trackerUid);
            if (!File.Exists(src)) continue;

            foreach (var r in ReadTrackerData(src))
                allData.Add((st.Uid, r));
        }

        if (options.DataFormat == ExportDataFormat.XLSX)
            WriteTrackerDataXlsx(dst, allData, includeStimulColumn: true);
        else
            WriteTrackerDataCsv(dst, allData, includeStimulColumn: true);
    }

    // ===== Actions =====

    private void ExportActions(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st)
    {
        if (string.IsNullOrWhiteSpace(_mouseKbdUid)) return;

        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, rr, st, "actions", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        var src = Path.Combine(_resultsDir, rr.Uid, st.Uid, _mouseKbdUid!);
        if (!File.Exists(src)) return;

        var data = ReadActionsData(src);

        if (options.DataFormat == ExportDataFormat.XLSX)
            WriteActionsDataXlsx(dst, data, includeStimulColumn: false, includeResultColumn: false);
        else
            WriteActionsDataCsv(dst, data, includeStimulColumn: false, includeResultColumn: false);
    }

    private void ExportActions_AggregatedPerStimul(MultiExportOptions options, DateTime now, IReadOnlyList<MultiExportResult> results, StimulFile st)
    {
        if (string.IsNullOrWhiteSpace(_mouseKbdUid)) return;

        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, results[0], st, "actions_per_stimul", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        var allData = new List<(string resultUid, ActionRecord r)>();

        foreach (var rr in results)
        {
            var src = Path.Combine(_resultsDir, rr.Uid, st.Uid, _mouseKbdUid!);
            if (!File.Exists(src)) continue;

            foreach (var r in ReadActionsData(src))
                allData.Add((rr.Uid, r));
        }

        if (options.DataFormat == ExportDataFormat.XLSX)
            WriteActionsDataXlsx(dst, allData, includeResultColumn: true);
        else
            WriteActionsDataCsv(dst, allData, includeResultColumn: true);
    }

    private void ExportActions_AggregatedPerResult(MultiExportOptions options, DateTime now, MultiExportResult rr, IReadOnlyList<StimulFile> stimuli)
    {
        if (string.IsNullOrWhiteSpace(_mouseKbdUid)) return;

        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, rr, stimuli[0], "actions_per_result", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        var allData = new List<(string stimUid, ActionRecord r)>();

        foreach (var st in stimuli)
        {
            var src = Path.Combine(_resultsDir, rr.Uid, st.Uid, _mouseKbdUid!);
            if (!File.Exists(src)) continue;

            foreach (var r in ReadActionsData(src))
                allData.Add((st.Uid, r));
        }

        if (options.DataFormat == ExportDataFormat.XLSX)
            WriteActionsDataXlsx(dst, allData, includeStimulColumn: true);
        else
            WriteActionsDataCsv(dst, allData, includeStimulColumn: true);
    }

    // ===== AOI (CSV/XLSX вместо JSON) =====

    private void ExportAoi(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st)
    {
        var aoiPath = FindAoiJson(st.Uid, preferredResultUid: rr.Uid);
        if (aoiPath == null) return;

        var aoiList = LoadAoiElements(aoiPath);
        if (aoiList == null || aoiList.Count == 0) return;

        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, rr, st, "aoi", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        if (options.DataFormat == ExportDataFormat.XLSX)
            WriteAoiDataXlsx(dst, aoiList, st.Uid);
        else
            WriteAoiDataCsv(dst, aoiList, st.Uid);
    }

    private void ExportAoi_AggregatedPerResult(MultiExportOptions options, DateTime now, MultiExportResult rr, IReadOnlyList<StimulFile> stimuli)
    {
        var allAoi = new List<(string stimUid, List<AoiElement> aois)>();

        foreach (var st in stimuli)
        {
            var aoiPath = FindAoiJson(st.Uid, preferredResultUid: rr.Uid);
            if (aoiPath == null) continue;

            var aoiList = LoadAoiElements(aoiPath);
            if (aoiList != null && aoiList.Count > 0)
                allAoi.Add((st.Uid, aoiList));
        }

        if (allAoi.Count == 0) return;

        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, rr, stimuli[0], "aoi_per_result", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        if (options.DataFormat == ExportDataFormat.XLSX)
            WriteAoiDataXlsx_Aggregated(dst, allAoi);
        else
            WriteAoiDataCsv_Aggregated(dst, allAoi);
    }

    private void ExportAoi_AllInOne(MultiExportOptions options, DateTime now, IReadOnlyList<MultiExportResult> results, IReadOnlyList<StimulFile> stimuli)
    {
        var allAoi = new List<(string stimUid, List<AoiElement> aois)>();

        foreach (var st in stimuli)
        {
            var aoiPath = FindAoiJson(st.Uid, preferredResultUid: results.Count > 0 ? results[0].Uid : null);
            if (aoiPath == null) continue;

            var aoiList = LoadAoiElements(aoiPath);
            if (aoiList != null && aoiList.Count > 0)
                allAoi.Add((st.Uid, aoiList));
        }

        if (allAoi.Count == 0) return;

        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, results[0], stimuli[0], "aoi_all", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        if (options.DataFormat == ExportDataFormat.XLSX)
            WriteAoiDataXlsx_Aggregated(dst, allAoi);
        else
            WriteAoiDataCsv_Aggregated(dst, allAoi);
    }

    // ===== AOI Writing Methods =====

    private void WriteAoiDataCsv(string path, List<AoiElement> aoiList, string stimUid)
    {
        using var sw = new StreamWriter(path);
        sw.WriteLine("stimul_uid;aoi_uid;aoi_name;aoi_type;color;line_width;points");

        foreach (var aoi in aoiList)
        {
            var points = string.Join(" ", aoi.NormalizedPoints.Select(p => $"{p.X.ToString(CultureInfo.InvariantCulture)},{p.Y.ToString(CultureInfo.InvariantCulture)}"));
            sw.Write(stimUid); sw.Write(';');
            sw.Write(aoi.Uid); sw.Write(';');
            sw.Write(EscapeCsv(aoi.Name)); sw.Write(';');
            sw.Write(aoi.Type.ToString()); sw.Write(';');
            sw.Write(aoi.ColorHex); sw.Write(';');
            sw.Write(aoi.LineWidth.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.WriteLine(points);
        }
    }

    private void WriteAoiDataCsv_Aggregated(string path, List<(string stimUid, List<AoiElement> aois)> allAoi)
    {
        using var sw = new StreamWriter(path);
        sw.WriteLine("stimul_uid;aoi_uid;aoi_name;aoi_type;color;line_width;points");

        foreach (var (stimUid, aoiList) in allAoi)
        {
            foreach (var aoi in aoiList)
            {
                var points = string.Join(" ", aoi.NormalizedPoints.Select(p => $"{p.X.ToString(CultureInfo.InvariantCulture)},{p.Y.ToString(CultureInfo.InvariantCulture)}"));
                sw.Write(stimUid); sw.Write(';');
                sw.Write(aoi.Uid); sw.Write(';');
                sw.Write(EscapeCsv(aoi.Name)); sw.Write(';');
                sw.Write(aoi.Type.ToString()); sw.Write(';');
                sw.Write(aoi.ColorHex); sw.Write(';');
                sw.Write(aoi.LineWidth.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
                sw.WriteLine(points);
            }
        }
    }

    private void WriteAoiDataXlsx(string path, List<AoiElement> aoiList, string stimUid)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("AOI");

        ws.Cell(1, 1).Value = "stimul_uid";
        ws.Cell(1, 2).Value = "aoi_uid";
        ws.Cell(1, 3).Value = "aoi_name";
        ws.Cell(1, 4).Value = "aoi_type";
        ws.Cell(1, 5).Value = "color";
        ws.Cell(1, 6).Value = "line_width";
        ws.Cell(1, 7).Value = "points";

        int row = 2;
        foreach (var aoi in aoiList)
        {
            var points = string.Join(" ", aoi.NormalizedPoints.Select(p => $"{p.X.ToString(CultureInfo.InvariantCulture)},{p.Y.ToString(CultureInfo.InvariantCulture)}"));
            ws.Cell(row, 1).Value = stimUid;
            ws.Cell(row, 2).Value = aoi.Uid;
            ws.Cell(row, 3).Value = aoi.Name;
            ws.Cell(row, 4).Value = aoi.Type.ToString();
            ws.Cell(row, 5).Value = aoi.ColorHex;
            ws.Cell(row, 6).Value = aoi.LineWidth;
            ws.Cell(row, 7).Value = points;
            row++;
        }

        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    private void WriteAoiDataXlsx_Aggregated(string path, List<(string stimUid, List<AoiElement> aois)> allAoi)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("AOI");

        ws.Cell(1, 1).Value = "stimul_uid";
        ws.Cell(1, 2).Value = "aoi_uid";
        ws.Cell(1, 3).Value = "aoi_name";
        ws.Cell(1, 4).Value = "aoi_type";
        ws.Cell(1, 5).Value = "color";
        ws.Cell(1, 6).Value = "line_width";
        ws.Cell(1, 7).Value = "points";

        int row = 2;
        foreach (var (stimUid, aoiList) in allAoi)
        {
            foreach (var aoi in aoiList)
            {
                var points = string.Join(" ", aoi.NormalizedPoints.Select(p => $"{p.X.ToString(CultureInfo.InvariantCulture)},{p.Y.ToString(CultureInfo.InvariantCulture)}"));
                ws.Cell(row, 1).Value = stimUid;
                ws.Cell(row, 2).Value = aoi.Uid;
                ws.Cell(row, 3).Value = aoi.Name;
                ws.Cell(row, 4).Value = aoi.Type.ToString();
                ws.Cell(row, 5).Value = aoi.ColorHex;
                ws.Cell(row, 6).Value = aoi.LineWidth;
                ws.Cell(row, 7).Value = points;
                row++;
            }
        }

        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    private List<AoiElement>? LoadAoiElements(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<AoiElement>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    // ===== Helper methods for tracker and actions data =====

    private record TrackerRecord(double time, double x, double y, double z, int valid, double lx, double ly, double rx, double ry, double lp, double rp, int lopen, int ropen, double leyex, double leyey, double leyez, double reyex, double reyey, double reyez);
    private record ActionRecord(double time, uint mouse, uint key, double x, double y);

    private List<TrackerRecord> ReadTrackerData(string path)
    {
        var result = new List<TrackerRecord>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[TrackerData.Size];

        while (true)
        {
            int read = fs.Read(buf, 0, buf.Length);
            if (read == 0 || read != buf.Length) break;

            var r = TrackerData.FromBytes(buf);
            result.Add(new TrackerRecord(r.time, r.x, r.y, r.z, r.valid, r.lx, r.ly, r.rx, r.ry, r.lp, r.rp, r.lopen, r.ropen, r.leyex, r.leyey, r.leyez, r.reyex, r.reyey, r.reyez));
        }

        return result;
    }

    private List<ActionRecord> ReadActionsData(string path)
    {
        var result = new List<ActionRecord>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> buf = stackalloc byte[MkRecordSize];

        while (true)
        {
            int n = fs.Read(buf);
            if (n == 0 || n != MkRecordSize) break;

            double time = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(0, 8)));
            uint mouse = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(8, 4));
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(12, 4));
            double x = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(16, 8)));
            double y = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(24, 8)));

            result.Add(new ActionRecord(time, mouse, key, x, y));
        }

        return result;
    }

    private void WriteTrackerDataCsv(string path, IEnumerable<TrackerRecord> data, bool includeStimulColumn = false, bool includeResultColumn = false)
    {
        using var sw = new StreamWriter(path);
        sw.WriteLine("time_sec;x;y;z;valid;lx;ly;rx;ry;lp;rp;lopen;ropen;leyex;leyey;leyez;reyex;reyey;reyez");

        foreach (var r in data)
        {
            sw.Write(r.time.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.x.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.y.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.z.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.valid.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.lx.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.ly.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.rx.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.ry.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.lp.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.rp.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.lopen.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.ropen.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.leyex.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.leyey.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.leyez.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.reyex.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.reyey.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.WriteLine(r.reyez.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void WriteTrackerDataCsv(string path, IEnumerable<(string uid, TrackerRecord r)> data, bool includeStimulColumn = false, bool includeResultColumn = false)
    {
        using var sw = new StreamWriter(path);
        var header = includeResultColumn ? "result_uid;" : (includeStimulColumn ? "stimul_uid;" : "");
        sw.WriteLine(header + "time_sec;x;y;z;valid;lx;ly;rx;ry;lp;rp;lopen;ropen;leyex;leyey;leyez;reyex;reyey;reyez");

        foreach (var (uid, r) in data)
        {
            if (includeResultColumn || includeStimulColumn)
            {
                sw.Write(uid); sw.Write(';');
            }
            sw.Write(r.time.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.x.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.y.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.z.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.valid.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.lx.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.ly.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.rx.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.ry.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.lp.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.rp.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.lopen.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.ropen.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.leyex.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.leyey.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.leyez.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.reyex.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.reyey.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.WriteLine(r.reyez.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void WriteTrackerDataXlsx(string path, IEnumerable<TrackerRecord> data, bool includeStimulColumn = false, bool includeResultColumn = false)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("RawGaze");

        var headers = new[] { "time_sec", "x", "y", "z", "valid", "lx", "ly", "rx", "ry", "lp", "rp", "lopen", "ropen", "leyex", "leyey", "leyez", "reyex", "reyey", "reyez" };
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];

        int row = 2;
        foreach (var r in data)
        {
            ws.Cell(row, 1).Value = r.time;
            ws.Cell(row, 2).Value = r.x;
            ws.Cell(row, 3).Value = r.y;
            ws.Cell(row, 4).Value = r.z;
            ws.Cell(row, 5).Value = r.valid;
            ws.Cell(row, 6).Value = r.lx;
            ws.Cell(row, 7).Value = r.ly;
            ws.Cell(row, 8).Value = r.rx;
            ws.Cell(row, 9).Value = r.ry;
            ws.Cell(row, 10).Value = r.lp;
            ws.Cell(row, 11).Value = r.rp;
            ws.Cell(row, 12).Value = r.lopen;
            ws.Cell(row, 13).Value = r.ropen;
            ws.Cell(row, 14).Value = r.leyex;
            ws.Cell(row, 15).Value = r.leyey;
            ws.Cell(row, 16).Value = r.leyez;
            ws.Cell(row, 17).Value = r.reyex;
            ws.Cell(row, 18).Value = r.reyey;
            ws.Cell(row, 19).Value = r.reyez;
            row++;
        }

        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    private void WriteTrackerDataXlsx(string path, IEnumerable<(string uid, TrackerRecord r)> data, bool includeStimulColumn = false, bool includeResultColumn = false)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("RawGaze");

        int col = 1;
        if (includeResultColumn)
            ws.Cell(1, col++).Value = "result_uid";
        else if (includeStimulColumn)
            ws.Cell(1, col++).Value = "stimul_uid";

        var headers = new[] { "time_sec", "x", "y", "z", "valid", "lx", "ly", "rx", "ry", "lp", "rp", "lopen", "ropen", "leyex", "leyey", "leyez", "reyex", "reyey", "reyez" };
        foreach (var h in headers)
            ws.Cell(1, col++).Value = h;

        int row = 2;
        foreach (var (uid, r) in data)
        {
            col = 1;
            if (includeResultColumn || includeStimulColumn)
                ws.Cell(row, col++).Value = uid;

            ws.Cell(row, col++).Value = r.time;
            ws.Cell(row, col++).Value = r.x;
            ws.Cell(row, col++).Value = r.y;
            ws.Cell(row, col++).Value = r.z;
            ws.Cell(row, col++).Value = r.valid;
            ws.Cell(row, col++).Value = r.lx;
            ws.Cell(row, col++).Value = r.ly;
            ws.Cell(row, col++).Value = r.rx;
            ws.Cell(row, col++).Value = r.ry;
            ws.Cell(row, col++).Value = r.lp;
            ws.Cell(row, col++).Value = r.rp;
            ws.Cell(row, col++).Value = r.lopen;
            ws.Cell(row, col++).Value = r.ropen;
            ws.Cell(row, col++).Value = r.leyex;
            ws.Cell(row, col++).Value = r.leyey;
            ws.Cell(row, col++).Value = r.leyez;
            ws.Cell(row, col++).Value = r.reyex;
            ws.Cell(row, col++).Value = r.reyey;
            ws.Cell(row, col++).Value = r.reyez;
            row++;
        }

        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    private void WriteActionsDataCsv(string path, IEnumerable<ActionRecord> data, bool includeStimulColumn = false, bool includeResultColumn = false)
    {
        using var sw = new StreamWriter(path);
        sw.WriteLine("time_sec;mouse_button;keyboard_code;x;y");

        foreach (var r in data)
        {
            sw.Write(r.time.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.mouse.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.key.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.x.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.WriteLine(r.y.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void WriteActionsDataCsv(string path, IEnumerable<(string uid, ActionRecord r)> data, bool includeStimulColumn = false, bool includeResultColumn = false)
    {
        using var sw = new StreamWriter(path);
        var header = includeResultColumn ? "result_uid;" : (includeStimulColumn ? "stimul_uid;" : "");
        sw.WriteLine(header + "time_sec;mouse_button;keyboard_code;x;y");

        foreach (var (uid, r) in data)
        {
            if (includeResultColumn || includeStimulColumn)
            {
                sw.Write(uid); sw.Write(';');
            }
            sw.Write(r.time.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.mouse.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.key.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(r.x.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.WriteLine(r.y.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void WriteActionsDataXlsx(string path, IEnumerable<ActionRecord> data, bool includeStimulColumn = false, bool includeResultColumn = false)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Actions");

        ws.Cell(1, 1).Value = "time_sec";
        ws.Cell(1, 2).Value = "mouse_button";
        ws.Cell(1, 3).Value = "keyboard_code";
        ws.Cell(1, 4).Value = "x";
        ws.Cell(1, 5).Value = "y";

        int row = 2;
        foreach (var r in data)
        {
            ws.Cell(row, 1).Value = r.time;
            ws.Cell(row, 2).Value = r.mouse;
            ws.Cell(row, 3).Value = r.key;
            ws.Cell(row, 4).Value = r.x;
            ws.Cell(row, 5).Value = r.y;
            row++;
        }

        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    private void WriteActionsDataXlsx(string path, IEnumerable<(string uid, ActionRecord r)> data, bool includeStimulColumn = false, bool includeResultColumn = false)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Actions");

        int col = 1;
        if (includeResultColumn)
            ws.Cell(1, col++).Value = "result_uid";
        else if (includeStimulColumn)
            ws.Cell(1, col++).Value = "stimul_uid";

        ws.Cell(1, col++).Value = "time_sec";
        ws.Cell(1, col++).Value = "mouse_button";
        ws.Cell(1, col++).Value = "keyboard_code";
        ws.Cell(1, col++).Value = "x";
        ws.Cell(1, col++).Value = "y";

        int row = 2;
        foreach (var (uid, r) in data)
        {
            col = 1;
            if (includeResultColumn || includeStimulColumn)
                ws.Cell(row, col++).Value = uid;

            ws.Cell(row, col++).Value = r.time;
            ws.Cell(row, col++).Value = r.mouse;
            ws.Cell(row, col++).Value = r.key;
            ws.Cell(row, col++).Value = r.x;
            ws.Cell(row, col++).Value = r.y;
            row++;
        }

        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    // ===== Images =====

    private void ExportPrebuiltImageIfExists(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st, string type, Action<string> report)
    {
        var imagePath = FindPrebuiltImage(rr.Uid, st.Uid, type);
        if (imagePath == null) return;

        var ext = Path.GetExtension(imagePath).TrimStart('.');
        var name = BuildFileName(options, now, rr, st, type, ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        File.Copy(imagePath, dst, overwrite: true);
    }

    private string? FindPrebuiltImage(string resultUid, string stimUid, string type)
    {
        var patterns = new[]
        {
            Path.Combine(_resultsDir, resultUid, stimUid, $"{type}.png"),
            Path.Combine(_resultsDir, resultUid, stimUid, $"{type}.jpg"),
            Path.Combine(_resultsDir, resultUid, stimUid, $"{type}_image.png"),
            Path.Combine(_resultsDir, resultUid, stimUid, $"{type}_image.jpg"),
        };

        return patterns.FirstOrDefault(File.Exists);
    }

    private void ExportEdfIfExists(MultiExportOptions options, DateTime now, MultiExportResult rr, Action<string> report)
    {
        var edfPath = Path.Combine(_resultsDir, rr.Uid, "eeg.edf");
        if (!File.Exists(edfPath)) return;

        var name = BuildFileName(options, now, rr, null!, "eeg", "edf");
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        File.Copy(edfPath, dst, overwrite: true);
    }

    // ===== Helpers =====

    private string? FindAoiJson(string stimUid, string? preferredResultUid)
    {
        // Сначала ищем в папке результата
        if (!string.IsNullOrWhiteSpace(preferredResultUid))
        {
            var path = Path.Combine(_resultsDir, preferredResultUid, stimUid, "aoi.json");
            if (File.Exists(path)) return path;
        }

        // Потом в общей папке стимулов
        var stimPath = Path.Combine(_expDir, "stimuli", stimUid, "aoi.json");
        if (File.Exists(stimPath)) return stimPath;

        // Ищем в любом результате
        if (Directory.Exists(_resultsDir))
        {
            foreach (var rdir in Directory.EnumerateDirectories(_resultsDir))
            {
                var path = Path.Combine(rdir, stimUid, "aoi.json");
                if (File.Exists(path)) return path;
            }
        }

        return null;
    }

    private string BuildFileName(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile? st, string type, string ext)
    {
        return _resolver.Resolve(options.FilenameTemplate, _exp, now, rr.Uid, rr.Result, st?.Uid ?? "", type, ext);
    }

    private static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        int i = 1;
        while (true)
        {
            var newPath = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(newPath)) return newPath;
            i++;
        }
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(';') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
