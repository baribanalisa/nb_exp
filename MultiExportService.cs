// File: MultiExportService.cs
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

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

    public MultiExportService(string expDir, ExperimentFile exp)
    {
        _expDir = expDir;
        _exp = exp;

        _resultsDir = Path.Combine(_expDir, "results");

        _trackerUid =
            exp.Devices.FirstOrDefault(d => IsEyeTrackerType(d.DevType))?.Uid
            ?? throw new InvalidOperationException("В exp.json не найден ай-трекер (тип устройства PathFinder/Gazepoint/...).");

        _mouseKbdUid =
            exp.Devices.FirstOrDefault(d => string.Equals(d.DevType, "MouseKeyboard", StringComparison.OrdinalIgnoreCase))?.Uid;

        _shimmerUid =
            exp.Devices.FirstOrDefault(d => string.Equals(d.DevType, "ShimmerGSR", StringComparison.OrdinalIgnoreCase))?.Uid;

        _hasEeg = exp.Devices.Any(d => (d.DevType ?? "").IndexOf("eeg", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public Task ExportAsync(
        MultiExportOptions options,
        IReadOnlyList<StimulFile> stimuli,
        IReadOnlyList<MultiExportResult> results,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() => ExportSync(options, stimuli, results, progress, ct), ct);
    }

    private void ExportSync(
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

        // Валидация шаблона
        if (!_resolver.TryValidate(options.FilenameTemplate, _exp, out var err))
            throw new InvalidOperationException("Шаблон имени файла: " + err);

        // Ограничения режимов (дублируем защиту VM)
        if (options.Mode == MultiExportMode.AllInOne && (options.ExportRaw || options.ExportSource))
            throw new InvalidOperationException("В режиме «Все в одном» запрещены сырые/исходные данные.");

        if (options.ExportEdf && (!_hasEeg || options.Mode != MultiExportMode.SeparateFiles))
            throw new InvalidOperationException("EDF доступен только в режиме «Отдельные файлы» и только если в эксперименте есть ЭЭГ.");

        var now = DateTime.Now;

        // Подготовим lookup по UID результатов
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
                    ExportRawGazeCsv(options, now, rr, st);

                if (options.ExportActions)
                    ExportActionsCsv(options, now, rr, st);

                if (options.ExportAoi)
                    ExportAoiJson(options, now, rr, st);

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
                ExportRawGazeCsv_AggregatedPerStimul(options, now, results, st);

            if (options.ExportActions)
                ExportActionsCsv_AggregatedPerStimul(options, now, results, st);

            if (options.ExportAoi)
            {
                // AOI на стимул один раз
                ExportAoiJson(options, now, results[0], st);
            }

            // EDF по плану не пишем
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
                ExportRawGazeCsv_AggregatedPerResult(options, now, rr, stimuli);

            if (options.ExportActions)
                ExportActionsCsv_AggregatedPerResult(options, now, rr, stimuli);

            if (options.ExportAoi)
                ExportAoiJson_AggregatedPerResult(options, now, rr, stimuli);
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
        // По требованиям: Raw/Source запрещены, остаётся AOI и/или картинки (если есть)
        if (options.ExportAoi)
            ExportAoiJson_AllInOne(options, now, results, stimuli);

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

    // ===== Source (копирование бинарников устройств) =====

    private void ExportSourceFiles(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st, Action<string> report)
    {
        // tracker
        CopyIfExists(
            src: Path.Combine(_resultsDir, rr.Uid, st.Uid, _trackerUid),
            type: "source_tracker",
            ext: "bin");

        // mouse/keyboard
        if (!string.IsNullOrWhiteSpace(_mouseKbdUid))
        {
            CopyIfExists(
                src: Path.Combine(_resultsDir, rr.Uid, st.Uid, _mouseKbdUid!),
                type: "source_actions",
                ext: "bin");
        }

        // shimmer gsr
        if (!string.IsNullOrWhiteSpace(_shimmerUid))
        {
            CopyIfExists(
                src: Path.Combine(_resultsDir, rr.Uid, st.Uid, _shimmerUid!),
                type: "source_gsr",
                ext: "bin");
        }

        void CopyIfExists(string src, string type, string ext)
        {
            if (!File.Exists(src)) return;

            var name = BuildFileName(options, now, rr, st, type, ext);
            var dst = Path.Combine(options.OutputDir, name);
            dst = EnsureUniquePath(dst);

            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
    }

    // ===== Raw gaze CSV =====

    private void ExportRawGazeCsv(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st)
    {
        var src = Path.Combine(_resultsDir, rr.Uid, st.Uid, _trackerUid);
        if (!File.Exists(src)) return;

        var name = BuildFileName(options, now, rr, st, "raw_gaze", "csv");
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sw = new StreamWriter(dst);

        sw.WriteLine("time_sec;x;y;z;valid;lx;ly;rx;ry;lp;rp;lopen;ropen;leyex;leyey;leyez;reyex;reyey;reyez");

        var buf = new byte[TrackerData.Size];
        while (true)
        {
            int read = fs.Read(buf, 0, buf.Length);
            if (read == 0) break;
            if (read != buf.Length) break;

            var r = MemoryMarshal.Read<TrackerData>(buf);

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

    private void ExportRawGazeCsv_AggregatedPerStimul(
        MultiExportOptions options, DateTime now, IReadOnlyList<MultiExportResult> results, StimulFile st)
    {
        var name = BuildFileName(options, now, results[0], st, "raw_gaze_per_stimul", "csv");
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        using var sw = new StreamWriter(dst);
        sw.WriteLine("result_uid;time_sec;x;y;z;valid;lx;ly;rx;ry;lp;rp;lopen;ropen;leyex;leyey;leyez;reyex;reyey;reyez");

        foreach (var rr in results)
        {
            var src = Path.Combine(_resultsDir, rr.Uid, st.Uid, _trackerUid);
            if (!File.Exists(src)) continue;

            using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[TrackerData.Size];

            while (true)
            {
                int read = fs.Read(buf, 0, buf.Length);
                if (read == 0) break;
                if (read != buf.Length) break;

                var r = MemoryMarshal.Read<TrackerData>(buf);

                sw.Write(rr.Uid); sw.Write(';');
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
    }

    private void ExportRawGazeCsv_AggregatedPerResult(
        MultiExportOptions options, DateTime now, MultiExportResult rr, IReadOnlyList<StimulFile> stimuli)
    {
        var name = BuildFileName(options, now, rr, stimuli[0], "raw_gaze_per_result", "csv");
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        using var sw = new StreamWriter(dst);
        sw.WriteLine("stimul_uid;time_sec;x;y;z;valid;lx;ly;rx;ry;lp;rp;lopen;ropen;leyex;leyey;leyez;reyex;reyey;reyez");

        foreach (var st in stimuli)
        {
            var src = Path.Combine(_resultsDir, rr.Uid, st.Uid, _trackerUid);
            if (!File.Exists(src)) continue;

            using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[TrackerData.Size];

            while (true)
            {
                int read = fs.Read(buf, 0, buf.Length);
                if (read == 0) break;
                if (read != buf.Length) break;

                var r = MemoryMarshal.Read<TrackerData>(buf);

                sw.Write(st.Uid); sw.Write(';');
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
    }

    // ===== Actions CSV (мышь/клава) =====

    private const int MkRecordSize = 48;

    private void ExportActionsCsv(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st)
    {
        if (string.IsNullOrWhiteSpace(_mouseKbdUid)) return;

        var src = Path.Combine(_resultsDir, rr.Uid, st.Uid, _mouseKbdUid!);
        if (!File.Exists(src)) return;

        var name = BuildFileName(options, now, rr, st, "actions", "csv");
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sw = new StreamWriter(dst);

        sw.WriteLine("time_sec;mouse_button;keyboard_code;x;y");

        Span<byte> buf = stackalloc byte[MkRecordSize];

        while (true)
        {
            int n = fs.Read(buf);
            if (n == 0) break;
            if (n != MkRecordSize) break;

            double time = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(0, 8)));
            uint mouse = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(8, 4));
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(12, 4));
            double x = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(16, 8)));
            double y = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(24, 8)));

            sw.Write(time.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(mouse.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(key.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.Write(x.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.WriteLine(y.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void ExportActionsCsv_AggregatedPerStimul(
        MultiExportOptions options, DateTime now, IReadOnlyList<MultiExportResult> results, StimulFile st)
    {
        if (string.IsNullOrWhiteSpace(_mouseKbdUid)) return;

        var name = BuildFileName(options, now, results[0], st, "actions_per_stimul", "csv");
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        using var sw = new StreamWriter(dst);
        sw.WriteLine("result_uid;time_sec;mouse_button;keyboard_code;x;y");

        Span<byte> buf = stackalloc byte[MkRecordSize];

        foreach (var rr in results)
        {
            var src = Path.Combine(_resultsDir, rr.Uid, st.Uid, _mouseKbdUid!);
            if (!File.Exists(src)) continue;

            using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            while (true)
            {
                int n = fs.Read(buf);
                if (n == 0) break;
                if (n != MkRecordSize) break;

                double time = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(0, 8)));
                uint mouse = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(8, 4));
                uint key = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(12, 4));
                double x = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(16, 8)));
                double y = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(24, 8)));

                sw.Write(rr.Uid); sw.Write(';');
                sw.Write(time.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
                sw.Write(mouse.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
                sw.Write(key.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
                sw.Write(x.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
                sw.WriteLine(y.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private void ExportActionsCsv_AggregatedPerResult(
        MultiExportOptions options, DateTime now, MultiExportResult rr, IReadOnlyList<StimulFile> stimuli)
    {
        if (string.IsNullOrWhiteSpace(_mouseKbdUid)) return;

        var name = BuildFileName(options, now, rr, stimuli[0], "actions_per_result", "csv");
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        using var sw = new StreamWriter(dst);
        sw.WriteLine("stimul_uid;time_sec;mouse_button;keyboard_code;x;y");

        Span<byte> buf = stackalloc byte[MkRecordSize];

        foreach (var st in stimuli)
        {
            var src = Path.Combine(_resultsDir, rr.Uid, st.Uid, _mouseKbdUid!);
            if (!File.Exists(src)) continue;

            using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            while (true)
            {
                int n = fs.Read(buf);
                if (n == 0) break;
                if (n != MkRecordSize) break;

                double time = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(0, 8)));
                uint mouse = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(8, 4));
                uint key = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(12, 4));
                double x = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(16, 8)));
                double y = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(24, 8)));

                sw.Write(st.Uid); sw.Write(';');
                sw.Write(time.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
                sw.Write(mouse.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
                sw.Write(key.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
                sw.Write(x.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
                sw.WriteLine(y.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    // ===== AOI (json) =====

    private void ExportAoiJson(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st)
    {
        var aoiPath = FindAoiJson(st.Uid, preferredResultUid: rr.Uid);
        if (aoiPath == null) return;

        var name = BuildFileName(options, now, rr, st, "aoi", "json");
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        File.Copy(aoiPath, dst, overwrite: true);
    }

    private void ExportAoiJson_AggregatedPerResult(MultiExportOptions options, DateTime now, MultiExportResult rr, IReadOnlyList<StimulFile> stimuli)
    {
        var root = new JsonObject();

        foreach (var st in stimuli)
        {
            var aoiPath = FindAoiJson(st.Uid, preferredResultUid: rr.Uid);
            if (aoiPath == null) continue;

            try
            {
                var node = JsonNode.Parse(File.ReadAllText(aoiPath));
                if (node != null)
                    root[st.Uid] = node;
            }
            catch
            {
                // пропускаем повреждённые AOI
            }
        }

        var name = BuildFileName(options, now, rr, stimuli[0], "aoi_per_result", "json");
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        File.WriteAllText(dst, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private void ExportAoiJson_AllInOne(MultiExportOptions options, DateTime now, IReadOnlyList<MultiExportResult> results, IReadOnlyList<StimulFile> stimuli)
    {
        // общий файл: по стимулу AOI (берём из первого попавшегося результата)
        var root = new JsonObject();

        foreach (var st in stimuli)
        {
            string? aoiPath = null;
            foreach (var rr in results)
            {
                aoiPath = FindAoiJson(st.Uid, preferredResultUid: rr.Uid);
                if (aoiPath != null) break;
            }

            if (aoiPath == null) continue;

            try
            {
                var node = JsonNode.Parse(File.ReadAllText(aoiPath));
                if (node != null)
                    root[st.Uid] = node;
            }
            catch { }
        }

        // используем первый результат/стим только как контекст имени
        var name = BuildFileName(options, now, results[0], stimuli[0], "aoi_all", "json");
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        File.WriteAllText(dst, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private string? FindAoiJson(string stimulUid, string preferredResultUid)
    {
        // 1) сначала в предпочитаемом результате
        var p1 = Path.Combine(_resultsDir, preferredResultUid, stimulUid, "aoi.json");
        if (File.Exists(p1)) return p1;

        // 2) иначе ищем по всем результатам
        if (!Directory.Exists(_resultsDir)) return null;

        foreach (var dir in Directory.EnumerateDirectories(_resultsDir))
        {
            var p = Path.Combine(dir, stimulUid, "aoi.json");
            if (File.Exists(p)) return p;
        }

        return null;
    }

    // ===== «картинки» (пока только копируем, если они уже где-то есть) =====

    private void ExportPrebuiltImageIfExists(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st, string kind, Action<string> report)
    {
        // Ищем файлы вида gaze.png / heat.png в папке стимулов результата (если кто-то заранее генерил).
        var dir = Path.Combine(_resultsDir, rr.Uid, st.Uid);
        if (!Directory.Exists(dir)) return;

        var candidates = new[]
        {
            Path.Combine(dir, $"{kind}.png"),
            Path.Combine(dir, $"{kind}.jpg"),
            Path.Combine(dir, $"{kind}.jpeg"),
        };

        var src = candidates.FirstOrDefault(File.Exists);
        if (src == null) return;

        var ext = Path.GetExtension(src).TrimStart('.');
        var name = BuildFileName(options, now, rr, st, kind + "_image", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        File.Copy(src, dst, overwrite: true);
        report($"Изображение скопировано: {Path.GetFileName(dst)}");
    }

    // ===== EDF (пока только копируем, если уже есть) =====

    private void ExportEdfIfExists(MultiExportOptions options, DateTime now, MultiExportResult rr, Action<string> report)
    {
        // Ищем любые *.edf внутри results/<uid>/ (мягко).
        var dir = Path.Combine(_resultsDir, rr.Uid);
        if (!Directory.Exists(dir)) return;

        var edfs = Directory.EnumerateFiles(dir, "*.edf", SearchOption.AllDirectories).ToList();
        if (edfs.Count == 0) return;

        foreach (var src in edfs)
        {
            var rel = Path.GetRelativePath(dir, src);
            var safeRel = rel.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');

            // контекст стимула тут не обязателен — берём первый
            var stDummy = _exp.Stimuls.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Uid)) ?? new StimulFile { Uid = "stim", Filename = "stim" };

            var name = BuildFileName(options, now, rr, stDummy, "edf_" + safeRel, "edf");
            var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

            File.Copy(src, dst, overwrite: true);
        }

        report($"EDF скопировано: {edfs.Count} файл(ов) для результата {rr.Uid}");
    }

    // ===== helpers =====

    private string BuildFileName(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st, string type, string extNoDot)
    {
        // Подсунем UID результата как «характеристику» __id_result
        // (чтобы FilenameTemplateResolver мог подставить %id_result% без изменений ResultFile)
        var rf = rr.File;
        rf.CharsData ??= new List<CharValue>();

        // временно добавим виртуальную пару
        var injected = new CharValue { Name = "__id_result", Val = rr.Uid };
        rf.CharsData.Add(injected);

        try
        {
            return _resolver.Resolve(options.FilenameTemplate, now, _exp, rf, st, type, extNoDot);
        }
        finally
        {
            // убираем (не оставляем мусор в модели)
            rf.CharsData.Remove(injected);
        }
    }

    private static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (int i = 2; i < 10_000; i++)
        {
            var p = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(p)) return p;
        }

        return path; // fallback
    }

    private static bool IsEyeTrackerType(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return false;
        return t.Equals("PathFinder", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Gazepoint", StringComparison.OrdinalIgnoreCase)
               || t.Contains("tracker", StringComparison.OrdinalIgnoreCase);
    }
}
