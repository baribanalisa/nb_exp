// File: MultiExportService.cs
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Media.Imaging;
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
    private readonly AnalysisDetectionSettings _detectSettings = new();
    private readonly AnalysisVisualizationSettings _visualSettings;
    private readonly StimulusHeatmapSettings _heatmapSettings;

    private readonly FilenameTemplateResolver _resolver = new();

    private const int MkRecordSize = 48;
    private const int ExcelMaxRows = 1_048_576;
    private const int ExcelAutoFitThreshold = 10_000;
    private const int ExcelMemoryRowLimit = 200_000;

    public MultiExportService(string expDir, ExperimentFile exp)
    {
        _expDir = expDir;
        _exp = exp;
        _resultsDir = Path.Combine(expDir, "results");

        var devices = exp.Devices ?? new List<DeviceFile>();

        static bool IsEyeTrackerType(string? t)
        {
            if (string.IsNullOrWhiteSpace(t)) return false;
            return t.Equals("PathFinder", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Gazepoint", StringComparison.OrdinalIgnoreCase)
                   || t.Contains("tracker", StringComparison.OrdinalIgnoreCase);
        }

        _trackerUid = devices.FirstOrDefault(d => IsEyeTrackerType(d.DevType))?.Uid
                      ?? "tracker";

        _mouseKbdUid = devices.FirstOrDefault(d => (d.DevType ?? "").IndexOf("mouse", StringComparison.OrdinalIgnoreCase) >= 0)?.Uid;
        _shimmerUid = devices.FirstOrDefault(d => (d.DevType ?? "").IndexOf("shimmer", StringComparison.OrdinalIgnoreCase) >= 0)?.Uid;
        _hasEeg = devices.Any(d => (d.DevType ?? "").IndexOf("eeg", StringComparison.OrdinalIgnoreCase) >= 0);

        _visualSettings = AppConfigManager.LoadAnalysisVisualizationSettings();
        _heatmapSettings = new StimulusHeatmapSettings
        {
            Function = _visualSettings.HeatmapFunction,
            Radius = _visualSettings.HeatmapRadius,
            InitialOpacity = _visualSettings.HeatmapInitialOpacity,
            Threshold = _visualSettings.HeatmapThreshold,
            MapType = _visualSettings.HeatmapMapType
        };
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

        void Report(string s) => progress?.Invoke(s);

        Report($"Мультиэкспорт: режим={options.Mode}, стимулов={stimuli.Count}, результатов={results.Count}");

        switch (options.Mode)
        {
            case MultiExportMode.SeparateFiles:
                ExportSeparateFiles(options, stimuli, results, now, Report, ct);
                break;

            case MultiExportMode.FilePerStimul:
                ExportFilePerStimul(options, stimuli, results, now, Report, ct);
                break;

            case MultiExportMode.FilePerResult:
                ExportFilePerResult(options, stimuli, results, now, Report, ct);
                break;

            case MultiExportMode.AllInOne:
                ExportAllInOne(options, stimuli, results, now, Report, ct);
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
        DateTime now,
        Action<string> report,
        CancellationToken ct)
    {
        int total = stimuli.Count * results.Count;
        int done = 0;

        // Диагностика опций
        report($"[DEBUG] ExportSource={options.ExportSource}, ExportRaw={options.ExportRaw}, ExportActions={options.ExportActions}, ExportAoi={options.ExportAoi}");
        report($"[DEBUG] ExportGazeImage={options.ExportGazeImage}, ExportHeatImage={options.ExportHeatImage}");
        report($"[DEBUG] DataFormat={options.DataFormat}, ImageFormat={options.ImageFormat}");
        report($"[DEBUG] OutputDir={options.OutputDir}");

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
                {
                    report($"[DEBUG] Вызываю ExportRawGaze для {rr.Uid}/{st.Uid}");
                    ExportRawGaze(options, now, rr, st);
                }

                if (options.ExportActions)
                {
                    report($"[DEBUG] Вызываю ExportActions для {rr.Uid}/{st.Uid}");
                    ExportActions(options, now, rr, st);
                }

                if (options.ExportAoi)
                {
                    report($"[DEBUG] Вызываю ExportAoi для {rr.Uid}/{st.Uid}");
                    ExportAoi(options, now, rr, st);
                }

                if (options.ExportTextMetrics)
                {
                    report($"[DEBUG] Вызываю ExportTextMetrics для {rr.Uid}/{st.Uid}");
                    ExportTextMetrics(options, now, rr, st);
                }

                if (options.ExportGazeImage)
                {
                    report($"[DEBUG] Вызываю ExportGazeImage для {rr.Uid}/{st.Uid}");
                    ExportGazeImage(options, now, rr, st, report);
                }

                if (options.ExportHeatImage)
                {
                    report($"[DEBUG] Вызываю ExportHeatImage для {rr.Uid}/{st.Uid}");
                    ExportHeatImage(options, now, rr, st, report);
                }

                if (options.ExportEdf)
                    ExportEdfIfExists(options, now, rr, st, report);
            }
        }
    }

    private void ExportFilePerStimul(
        MultiExportOptions options,
        IReadOnlyList<StimulFile> stimuli,
        IReadOnlyList<MultiExportResult> results,
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
                ExportAoi(options, now, results[0], st);

            if (options.ExportTextMetrics)
                ExportTextMetrics(options, now, results[0], st);

            // Генерируем изображения для каждого результата
            if (options.ExportGazeImage || options.ExportHeatImage)
            {
                foreach (var rr in results)
                {
                    if (options.ExportGazeImage)
                        ExportGazeImage(options, now, rr, st, report);
                    if (options.ExportHeatImage)
                        ExportHeatImage(options, now, rr, st, report);
                }
            }
        }
    }

    private void ExportFilePerResult(
        MultiExportOptions options,
        IReadOnlyList<StimulFile> stimuli,
        IReadOnlyList<MultiExportResult> results,
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

            if (options.ExportTextMetrics)
                ExportTextMetrics_AggregatedPerResult(options, now, rr, stimuli);

            // Генерируем изображения для каждого стимула
            if (options.ExportGazeImage || options.ExportHeatImage)
            {
                foreach (var st in stimuli)
                {
                    if (options.ExportGazeImage)
                        ExportGazeImage(options, now, rr, st, report);
                    if (options.ExportHeatImage)
                        ExportHeatImage(options, now, rr, st, report);
                }
            }
        }
    }

    private void ExportAllInOne(
        MultiExportOptions options,
        IReadOnlyList<StimulFile> stimuli,
        IReadOnlyList<MultiExportResult> results,
        DateTime now,
        Action<string> report,
        CancellationToken ct)
    {
        if (options.ExportAoi)
            ExportAoi_AllInOne(options, now, results, stimuli);

        if (options.ExportTextMetrics)
            ExportTextMetrics_AllInOne(options, now, results, stimuli);

        if (options.ExportGazeImage || options.ExportHeatImage)
        {
            foreach (var st in stimuli)
            {
                foreach (var rr in results)
                {
                    ct.ThrowIfCancellationRequested();
                    if (options.ExportGazeImage)
                        ExportGazeImage(options, now, rr, st, report);
                    if (options.ExportHeatImage)
                        ExportHeatImage(options, now, rr, st, report);
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
        
        // Диагностика
        System.Diagnostics.Debug.WriteLine($"[ExportRawGaze] src={src}, exists={File.Exists(src)}");
        
        if (!File.Exists(src))
        {
            System.Diagnostics.Debug.WriteLine($"[ExportRawGaze] Файл не найден: {src}");
            return;
        }

        if (options.DataFormat == ExportDataFormat.XLSX)
        {
            var estimatedRows = CountRowsByRecordSize(src, TrackerData.Size);
            EnsureXlsxCapacity(estimatedRows, "raw gaze");
            System.Diagnostics.Debug.WriteLine($"[ExportRawGaze] Оценка записей: {estimatedRows}");
            if (estimatedRows == 0)
            {
                System.Diagnostics.Debug.WriteLine("[ExportRawGaze] Нет данных!");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[ExportRawGaze] Создаю файл: {dst}");
            WriteTrackerDataXlsx(dst, EnumerateTrackerData(src), null);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[ExportRawGaze] Создаю файл: {dst}");
            WriteTrackerDataCsvFromFile(dst, src);
        }
            
        System.Diagnostics.Debug.WriteLine($"[ExportRawGaze] Файл создан: {File.Exists(dst)}");
    }

    private void ExportRawGaze_AggregatedPerStimul(MultiExportOptions options, DateTime now, IReadOnlyList<MultiExportResult> results, StimulFile st)
    {
        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, results[0], st, "raw_gaze_per_stimul", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        if (options.DataFormat == ExportDataFormat.XLSX)
        {
            var estimatedRows = results
                .Select(rr => Path.Combine(_resultsDir, rr.Uid, st.Uid, _trackerUid))
                .Where(File.Exists)
                .Select(path => CountRowsByRecordSize(path, TrackerData.Size))
                .Sum();

            EnsureXlsxCapacity(estimatedRows, "raw gaze");
            var sourceList = results
                .Select(rr => (uid: rr.Uid, path: Path.Combine(_resultsDir, rr.Uid, st.Uid, _trackerUid)))
                .Where(p => File.Exists(p.path))
                .ToList();
            if (sourceList.Count == 0) return;

            WriteTrackerDataXlsx(dst, EnumerateTrackerDataWithUid(sourceList), "result_uid");
        }
        else
        {
            var sources = results
                .Select(rr => (uid: rr.Uid, path: Path.Combine(_resultsDir, rr.Uid, st.Uid, _trackerUid)))
                .Where(p => File.Exists(p.path));
            var sourceList = sources.ToList();
            if (sourceList.Count == 0) return;
            WriteTrackerDataCsvFromFiles(dst, sourceList, "result_uid");
        }
    }

    private void ExportRawGaze_AggregatedPerResult(MultiExportOptions options, DateTime now, MultiExportResult rr, IReadOnlyList<StimulFile> stimuli)
    {
        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, rr, stimuli[0], "raw_gaze_per_result", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        if (options.DataFormat == ExportDataFormat.XLSX)
        {
            var estimatedRows = stimuli
                .Select(st => Path.Combine(_resultsDir, rr.Uid, st.Uid, _trackerUid))
                .Where(File.Exists)
                .Select(path => CountRowsByRecordSize(path, TrackerData.Size))
                .Sum();

            EnsureXlsxCapacity(estimatedRows, "raw gaze");
            var sourceList = stimuli
                .Select(st => (uid: st.Uid, path: Path.Combine(_resultsDir, rr.Uid, st.Uid, _trackerUid)))
                .Where(p => File.Exists(p.path))
                .ToList();
            if (sourceList.Count == 0) return;

            WriteTrackerDataXlsx(dst, EnumerateTrackerDataWithUid(sourceList), "stimul_uid");
        }
        else
        {
            var sources = stimuli
                .Select(st => (uid: st.Uid, path: Path.Combine(_resultsDir, rr.Uid, st.Uid, _trackerUid)))
                .Where(p => File.Exists(p.path));
            var sourceList = sources.ToList();
            if (sourceList.Count == 0) return;
            WriteTrackerDataCsvFromFiles(dst, sourceList, "stimul_uid");
        }
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

        if (options.DataFormat == ExportDataFormat.XLSX)
        {
            var estimatedRows = CountRowsByRecordSize(src, MkRecordSize);
            EnsureXlsxCapacity(estimatedRows, "actions");
            if (estimatedRows == 0) return;
            WriteActionsDataXlsx(dst, EnumerateActionRecords(src), null);
        }
        else
        {
            WriteActionsDataCsvFromFile(dst, src);
        }
    }

    private void ExportActions_AggregatedPerStimul(MultiExportOptions options, DateTime now, IReadOnlyList<MultiExportResult> results, StimulFile st)
    {
        if (string.IsNullOrWhiteSpace(_mouseKbdUid)) return;

        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, results[0], st, "actions_per_stimul", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        if (options.DataFormat == ExportDataFormat.XLSX)
        {
            var estimatedRows = results
                .Select(rr => Path.Combine(_resultsDir, rr.Uid, st.Uid, _mouseKbdUid!))
                .Where(File.Exists)
                .Select(path => CountRowsByRecordSize(path, MkRecordSize))
                .Sum();

            EnsureXlsxCapacity(estimatedRows, "actions");
            var sourceList = results
                .Select(rr => (uid: rr.Uid, path: Path.Combine(_resultsDir, rr.Uid, st.Uid, _mouseKbdUid!)))
                .Where(p => File.Exists(p.path))
                .ToList();
            if (sourceList.Count == 0) return;

            WriteActionsDataXlsx(dst, EnumerateActionRecordsWithUid(sourceList), "result_uid");
        }
        else
        {
            var sources = results
                .Select(rr => (uid: rr.Uid, path: Path.Combine(_resultsDir, rr.Uid, st.Uid, _mouseKbdUid!)))
                .Where(p => File.Exists(p.path));
            var sourceList = sources.ToList();
            if (sourceList.Count == 0) return;
            WriteActionsDataCsvFromFiles(dst, sourceList, "result_uid");
        }
    }

    private void ExportActions_AggregatedPerResult(MultiExportOptions options, DateTime now, MultiExportResult rr, IReadOnlyList<StimulFile> stimuli)
    {
        if (string.IsNullOrWhiteSpace(_mouseKbdUid)) return;

        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, rr, stimuli[0], "actions_per_result", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        if (options.DataFormat == ExportDataFormat.XLSX)
        {
            var estimatedRows = stimuli
                .Select(st => Path.Combine(_resultsDir, rr.Uid, st.Uid, _mouseKbdUid!))
                .Where(File.Exists)
                .Select(path => CountRowsByRecordSize(path, MkRecordSize))
                .Sum();

            EnsureXlsxCapacity(estimatedRows, "actions");
            var sourceList = stimuli
                .Select(st => (uid: st.Uid, path: Path.Combine(_resultsDir, rr.Uid, st.Uid, _mouseKbdUid!)))
                .Where(p => File.Exists(p.path))
                .ToList();
            if (sourceList.Count == 0) return;

            WriteActionsDataXlsx(dst, EnumerateActionRecordsWithUid(sourceList), "stimul_uid");
        }
        else
        {
            var sources = stimuli
                .Select(st => (uid: st.Uid, path: Path.Combine(_resultsDir, rr.Uid, st.Uid, _mouseKbdUid!)))
                .Where(p => File.Exists(p.path));
            var sourceList = sources.ToList();
            if (sourceList.Count == 0) return;
            WriteActionsDataCsvFromFiles(dst, sourceList, "stimul_uid");
        }
    }

    // ===== AOI (CSV/XLSX) =====

    private void ExportAoi(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st)
    {
        var aoiPath = FindAoiJson(st.Uid, preferredResultUid: rr.Uid);
        if (aoiPath == null) return;

        var aoiList = LoadAoiElements(aoiPath);
        if (aoiList == null || aoiList.Count == 0) return;

        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, rr, st, "aoi", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        var data = aoiList.Select(a => (st.Uid, a)).ToList();

        if (options.DataFormat == ExportDataFormat.XLSX)
            WriteAoiDataXlsx(dst, data);
        else
            WriteAoiDataCsv(dst, data);
    }

    private void ExportAoi_AggregatedPerResult(MultiExportOptions options, DateTime now, MultiExportResult rr, IReadOnlyList<StimulFile> stimuli)
    {
        var allAoi = new List<(string stimUid, AoiElement aoi)>();

        foreach (var st in stimuli)
        {
            var aoiPath = FindAoiJson(st.Uid, preferredResultUid: rr.Uid);
            if (aoiPath == null) continue;

            var aoiList = LoadAoiElements(aoiPath);
            if (aoiList != null)
            {
                foreach (var aoi in aoiList)
                    allAoi.Add((st.Uid, aoi));
            }
        }

        if (allAoi.Count == 0) return;

        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, rr, stimuli[0], "aoi_per_result", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        if (options.DataFormat == ExportDataFormat.XLSX)
            WriteAoiDataXlsx(dst, allAoi);
        else
            WriteAoiDataCsv(dst, allAoi);
    }

    private void ExportAoi_AllInOne(MultiExportOptions options, DateTime now, IReadOnlyList<MultiExportResult> results, IReadOnlyList<StimulFile> stimuli)
    {
        var allAoi = new List<(string stimUid, AoiElement aoi)>();

        foreach (var st in stimuli)
        {
            var aoiPath = FindAoiJson(st.Uid, preferredResultUid: results.Count > 0 ? results[0].Uid : null);
            if (aoiPath == null) continue;

            var aoiList = LoadAoiElements(aoiPath);
            if (aoiList != null)
            {
                foreach (var aoi in aoiList)
                    allAoi.Add((st.Uid, aoi));
            }
        }

        if (allAoi.Count == 0) return;

        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, results[0], stimuli[0], "aoi_all", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        if (options.DataFormat == ExportDataFormat.XLSX)
            WriteAoiDataXlsx(dst, allAoi);
        else
            WriteAoiDataCsv(dst, allAoi);
    }

    // ===== Gaze Image (генерация на лету) =====

    private void ExportGazeImage(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st, Action<string> report)
    {
        try
        {
            report($"Генерация карты движения взгляда для {st.Uid}...");
            
            var preferredExt = GetImageExtension(options.ImageFormat);
            var backgroundPath = GetStimulusImagePath(st);
            var hasBackground = !string.IsNullOrWhiteSpace(backgroundPath);
            var prebuilt = FindPrebuiltImage(rr.Uid, st.Uid, "gaze", preferredExt);
            
            if (prebuilt != null && !hasBackground)
            {
                var (imagePath, foundExt) = prebuilt.Value;
                var name = BuildFileName(options, now, rr, st, "gaze", preferredExt);
                var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

                if (string.Equals(foundExt, preferredExt, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(imagePath, dst, overwrite: true);
                    report($"✓ Копирование существующей карты движения: {Path.GetFileName(dst)}");
                    return;
                }

                using var img = Image.FromFile(imagePath);
                img.Save(dst, GetImageSaveFormat(options.ImageFormat));
                report($"✓ Конвертация существующей карты движения: {Path.GetFileName(dst)}");
                return;
            }

            var fixations = GetFixationsForStim(rr.Uid, st.Uid);
            if (fixations == null || fixations.Count == 0)
            {
                report("⚠ Невозможно сгенерировать карту движения: нет фиксаций");
                return;
            }

            var (stimW, stimH) = GetStimulusDimensions(st);
            if (stimW <= 0 || stimH <= 0)
            {
                report("⚠ Невозможно определить размеры стимула");
                return;
            }

            if (!hasBackground)
                report("⚠ Фоновое изображение стимула не найдено, карта будет без подложки");

            var bitmap = ImageGenerator.GenerateGazeMap(
                fixations,
                stimW,
                stimH,
                _visualSettings,
                System.Drawing.Color.Blue,
                backgroundPath);

            if (bitmap == null)
            {
                report("⚠ Ошибка генерации карты движения");
                return;
            }

            var fileName = BuildFileName(options, now, rr, st, "gaze", preferredExt);
            var dstPath = EnsureUniquePath(Path.Combine(options.OutputDir, fileName));
            
            SaveBitmapToFile(bitmap, dstPath, options.ImageFormat);
            report($"✓ Генерация карты движения: {Path.GetFileName(dstPath)} ({fixations.Count} фиксаций)");
        }
        catch (Exception ex)
        {
            report($"❌ Ошибка генерации карты движения: {ex.Message}");
        }
    }

    // ===== Heat Image (генерация на лету) =====

    private void ExportHeatImage(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st, Action<string> report)
    {
        try
        {
            report($"Генерация тепловой карты для {st.Uid}...");
            
            var preferredExt = GetImageExtension(options.ImageFormat);
            var backgroundPath = GetStimulusImagePath(st);
            var hasBackground = !string.IsNullOrWhiteSpace(backgroundPath);
            var prebuilt = FindPrebuiltImage(rr.Uid, st.Uid, "heat", preferredExt);
            
            if (prebuilt != null && !hasBackground)
            {
                var (imagePath, foundExt) = prebuilt.Value;
                var name = BuildFileName(options, now, rr, st, "heat", preferredExt);
                var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

                if (string.Equals(foundExt, preferredExt, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(imagePath, dst, overwrite: true);
                    report($"✓ Копирование существующей тепловой карты: {Path.GetFileName(dst)}");
                    return;
                }

                using var img = Image.FromFile(imagePath);
                img.Save(dst, GetImageSaveFormat(options.ImageFormat));
                report($"✓ Конвертация существующей тепловой карты: {Path.GetFileName(dst)}");
                return;
            }

            var samples = BuildHeatmapSamples(rr.Uid, st.Uid);
            if (samples == null || samples.Count == 0)
            {
                report("⚠ Невозможно сгенерировать тепловую карту: нет данных");
                return;
            }

            var (stimW, stimH) = GetStimulusDimensions(st);
            if (stimW <= 0 || stimH <= 0)
            {
                report("⚠ Невозможно определить размеры стимула");
                return;
            }

            if (!hasBackground)
                report("⚠ Фоновое изображение стимула не найдено, карта будет без подложки");

            var bitmap = ImageGenerator.GenerateHeatmap(
                samples,
                stimW,
                stimH,
                _heatmapSettings,
                backgroundPath);

            if (bitmap == null)
            {
                report("⚠ Ошибка генерации тепловой карты");
                return;
            }

            var fileName = BuildFileName(options, now, rr, st, "heat", preferredExt);
            var dstPath = EnsureUniquePath(Path.Combine(options.OutputDir, fileName));
            
            SaveBitmapToFile(bitmap, dstPath, options.ImageFormat);
            report($"✓ Генерация тепловой карты: {Path.GetFileName(dstPath)} ({samples.Count} сэмплов)");
        }
        catch (Exception ex)
        {
            report($"❌ Ошибка генерации тепловой карты: {ex.Message}");
        }
    }

    private (string path, string extension)? FindPrebuiltImage(string resultUid, string stimUid, string baseName, string preferredExt)
    {
        var dir = Path.Combine(_resultsDir, resultUid, stimUid);
        if (!Directory.Exists(dir)) return null;

        var knownExts = new List<string> { preferredExt, "png", "jpg", "jpeg", "bmp" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ext in knownExts)
        {
            if (!seen.Add(ext)) continue;
            var path = Path.Combine(dir, $"{baseName}.{ext}");
            if (File.Exists(path))
                return (path, ext);
        }

        return null;
    }

    private void SaveBitmapToFile(WriteableBitmap bitmap, string path, ExportImageFormat format)
    {
        BitmapEncoder encoder = format == ExportImageFormat.JPG
            ? new JpegBitmapEncoder { QualityLevel = 95 }
            : new PngBitmapEncoder();

        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        encoder.Save(fs);
    }

    private List<Fixation> GetFixationsForStim(string resultUid, string stimUid)
    {
        var raw = GetRawSamplesForStim(resultUid, stimUid);
        if (raw.Count == 0) return new List<Fixation>();

        var pre = AnalysisFixationPipeline.Preprocess(raw, _detectSettings);
        var (screenW, screenH, screenWmm, screenHmm) = GetScreenDimensions(resultUid);

        List<Fixation> fixScreen = _detectSettings.Algorithm == FixationAlgorithm.Ivt
            ? AnalysisFixationPipeline.DetectIvt(pre, screenW, screenH, screenWmm, screenHmm, _detectSettings)
            : AnalysisFixationPipeline.DetectIdt(pre, screenW, screenH, _detectSettings);

        var stimFile = _exp.Stimuls?.FirstOrDefault(s => s.Uid == stimUid);
        var (stimW, stimH) = GetStimulusDimensions(stimFile);
        var (offX, offY, fitW, fitH) = FitRect(screenW, screenH, stimW, stimH, stimFile?.Scale ?? false);

        return MapFixationsToStimRect(fixScreen, offX, offY, fitW, fitH);
    }

    private static List<Fixation> MapFixationsToStimRect(
        IReadOnlyList<Fixation> fixations,
        double offX,
        double offY,
        double fitW,
        double fitH)
    {
        var result = new List<Fixation>(fixations.Count);
        double maxX = offX + fitW;
        double maxY = offY + fitH;

        foreach (var fix in fixations)
        {
            if (fix.Xpx < offX || fix.Xpx > maxX || fix.Ypx < offY || fix.Ypx > maxY)
                continue;

            float x = (float)(fix.Xpx - offX);
            float y = (float)(fix.Ypx - offY);

            result.Add(new Fixation(fix.StartSec, fix.DurSec, x, y));
        }

        return result;
    }

    private List<RawGazeSample> GetRawSamplesForStim(string resultUid, string stimUid)
        => ReadRawSamplesForStim(resultUid, stimUid, _detectSettings.Eye);

    private List<RawGazeSample> ReadRawSamplesForStim(string resultUid, string stimUid, EyeSelection eye)
    {
        var p = Path.Combine(_expDir, "results", resultUid, stimUid, _trackerUid);
        if (!File.Exists(p)) return new();

        var list = new List<RawGazeSample>(4096);

        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> buf = stackalloc byte[TrackerData.Size];

        float t0 = float.NaN;
        float prevT = float.NegativeInfinity;

        while (true)
        {
            int n = fs.Read(buf);
            if (n == 0) break;
            if (n != TrackerData.Size) break;

            int valid = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(0, 4));
            float t = ReadF(buf, 4);
            if (!float.IsFinite(t)) continue;

            if (t <= prevT) continue;
            prevT = t;

            if (float.IsNaN(t0)) t0 = t;
            float timeSec = t - t0;
            if (!float.IsFinite(timeSec)) continue;

            (bool ok, float xn, float yn) = ReadEyeCoords(buf, valid, eye);
            bool openValid = ReadOpenValid(valid, eye);
            float distM = ReadDistanceM(buf, valid);

            if (ok)
            {
                if (!float.IsFinite(xn) || !float.IsFinite(yn)) ok = false;
                else if (xn < 0 || xn > 1 || yn < 0 || yn > 1) ok = false;
            }

            if (!float.IsFinite(distM) || distM < 0) distM = 0;

            list.Add(new RawGazeSample(timeSec: timeSec, xn: xn, yn: yn, distanceM: distM, valid: ok, openValid: openValid));
        }

        return list;

        static float ReadF(Span<byte> b, int off) =>
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(b.Slice(off, 4)));

        static (bool ok, float xn, float yn) ReadEyeCoords(Span<byte> b, int valid, EyeSelection eye)
        {
            bool ok;
            float xn, yn;

            switch (eye)
            {
                case EyeSelection.LeftEye:
                    ok = (valid & (int)TrackerDataValidity.LEFT_PUPIL_COORD_VALID) != 0;
                    xn = ReadF(b, 60);
                    yn = ReadF(b, 64);
                    break;
                case EyeSelection.RightEye:
                    ok = (valid & (int)TrackerDataValidity.RIGHT_PUPIL_COORD_VALID) != 0;
                    xn = ReadF(b, 52);
                    yn = ReadF(b, 56);
                    break;
                default:
                    ok = (valid & (int)TrackerDataValidity.COORD_VALID) != 0;
                    xn = ReadF(b, 8);
                    yn = ReadF(b, 12);
                    break;
            }

            return (ok, xn, yn);
        }

        static float ReadDistanceM(Span<byte> b, int valid)
        {
            bool lOk = (valid & (int)TrackerDataValidity.LEFT_PUPIL_3D_COORD_VALID) != 0;
            bool rOk = (valid & (int)TrackerDataValidity.RIGHT_PUPIL_3D_COORD_VALID) != 0;

            float lz = ReadF(b, 36);
            float rz = ReadF(b, 48);

            bool lGood = lOk && float.IsFinite(lz) && lz > 0;
            bool rGood = rOk && float.IsFinite(rz) && rz > 0;

            if (lGood && rGood) return (lz + rz) * 0.5f;
            if (lGood) return lz;
            if (rGood) return rz;
            return 0f;
        }

        static bool ReadOpenValid(int valid, EyeSelection eye)
        {
            bool l = (valid & (int)TrackerDataValidity.LEFT_OPEN_VALID) != 0;
            bool r = (valid & (int)TrackerDataValidity.RIGHT_OPEN_VALID) != 0;

            return eye switch
            {
                EyeSelection.LeftEye => l,
                EyeSelection.RightEye => r,
                _ => l && r,
            };
        }
    }

    private List<HeatmapSample>? BuildHeatmapSamples(string resultUid, string stimUid)
    {
        var raw = GetRawSamplesForStim(resultUid, stimUid);
        if (raw == null || raw.Count == 0) return null;

        var preprocessed = AnalysisFixationPipeline.Preprocess(raw, _detectSettings);
        
        var (screenW, screenH, _, _) = GetScreenDimensions(resultUid);
        
        var stimFile = _exp.Stimuls?.FirstOrDefault(s => s.Uid == stimUid);
        var (stimW, stimH) = GetStimulusDimensions(stimFile);

        var (offX, offY, fitW, fitH) = FitRect(screenW, screenH, stimW, stimH, stimFile?.Scale ?? false);

        var samples = new List<HeatmapSample>(preprocessed.Count);
        foreach (var sample in preprocessed)
        {
            if (!sample.Valid) continue;

            float screenX = sample.Xn * screenW;
            float screenY = sample.Yn * screenH;

            float stimX = (float)(screenX - offX);
            float stimY = (float)(screenY - offY);

            if (stimX < 0 || stimX >= fitW || stimY < 0 || stimY >= fitH)
                continue;

            samples.Add(new HeatmapSample(stimX, stimY));
        }

        return samples;
    }

    private (int width, int height) GetStimulusDimensions(StimulFile? st)
    {
        if (st == null) return (1920, 1080);

        try
        {
            var stimPath = ResolveStimulusPath(st.Uid, st.Filename);

            if (stimPath != null && File.Exists(stimPath))
            {
                if (IsImageFile(stimPath))
                {
                    using var img = Image.FromFile(stimPath);
                    return (img.Width, img.Height);
                }

                var (screenW, screenH, _, _) = GetScreenDimensions(st);
                return (screenW, screenH);
            }
        }
        catch
        {
        }

        var (w, h, _, _) = GetScreenDimensions(st);
        return (w, h);
    }

    private string? GetStimulusImagePath(StimulFile st)
    {
        var stimPath = ResolveStimulusPath(st.Uid, st.Filename);
        if (stimPath == null) return null;

        return IsImageFile(stimPath) ? stimPath : null;
    }

    private string? ResolveStimulusPath(string uid, string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;

        // Пробуем несколько вариантов путей (как в AnalysisWindow)
        // 1. {expDir}/{uid}/{filename}
        var p1 = Path.Combine(_expDir, uid, filename);
        if (File.Exists(p1)) return p1;

        // 2. {expDir}/stimuli/{uid}/{filename}
        var p2 = Path.Combine(_expDir, "stimuli", uid, filename);
        if (File.Exists(p2)) return p2;

        // 3. {expDir}/{filename}
        var p3 = Path.Combine(_expDir, filename);
        if (File.Exists(p3)) return p3;

        // 4. Рекурсивный поиск
        try
        {
            return Directory.EnumerateFiles(_expDir, filename, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private (int width, int height, int widthMm, int heightMm) GetScreenDimensions(StimulFile st)
    {
        try
        {
            if (Directory.Exists(_resultsDir))
            {
                foreach (var resultDir in Directory.EnumerateDirectories(_resultsDir))
                {
                    var resultJsonPath = Path.Combine(resultDir, "result.json");
                    if (!File.Exists(resultJsonPath)) continue;

                    var json = File.ReadAllText(resultJsonPath);
                    var result = JsonSerializer.Deserialize<ResultFile>(json);
                    
                    if (result != null)
                    {
                        int w = result.ScreenWidthPx > 0 ? result.ScreenWidthPx : 1920;
                        int h = result.ScreenHeightPx > 0 ? result.ScreenHeightPx : 1080;
                        int wmm = result.ScreenWidthMm > 0 ? result.ScreenWidthMm : 477;
                        int hmm = result.ScreenHeightMm > 0 ? result.ScreenHeightMm : 268;
                        return (w, h, wmm, hmm);
                    }
                }
            }
        }
        catch
        {
        }

        return (1920, 1080, 477, 268);
    }

    private (int screenW, int screenH, int screenWmm, int screenHmm) GetScreenDimensions(string resultUid)
    {
        try
        {
            var resultJsonPath = Path.Combine(_resultsDir, resultUid, "result.json");
            if (!File.Exists(resultJsonPath)) return (1920, 1080, 477, 268);

            var json = File.ReadAllText(resultJsonPath);
            var result = JsonSerializer.Deserialize<ResultFile>(json);
            
            if (result == null) return (1920, 1080, 477, 268);

            int w = result.ScreenWidthPx > 0 ? result.ScreenWidthPx : 1920;
            int h = result.ScreenHeightPx > 0 ? result.ScreenHeightPx : 1080;
            int wmm = result.ScreenWidthMm > 0 ? result.ScreenWidthMm : 477;
            int hmm = result.ScreenHeightMm > 0 ? result.ScreenHeightMm : 268;

            return (w, h, wmm, hmm);
        }
        catch
        {
            return (1920, 1080, 477, 268);
        }
    }

    private bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif";
    }

    private (double offX, double offY, double fitW, double fitH) FitRect(
        int containerW,
        int containerH,
        int contentW,
        int contentH,
        bool scaleToFit)
    {
        if (!scaleToFit)
        {
            double baseOffX = Math.Max(0, (containerW - contentW) / 2.0);
            double baseOffY = Math.Max(0, (containerH - contentH) / 2.0);
            return (baseOffX, baseOffY, contentW, contentH);
        }

        double scale = Math.Min(
            (double)containerW / contentW,
            (double)containerH / contentH);

        double fitW = contentW * scale;
        double fitH = contentH * scale;
        double offX = (containerW - fitW) / 2.0;
        double offY = (containerH - fitH) / 2.0;

        return (offX, offY, fitW, fitH);
    }


    // ===== EDF =====

    private void ExportEdfIfExists(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st, Action<string> report)
    {
        var edfPath = Path.Combine(_resultsDir, rr.Uid, "eeg.edf");
        if (!File.Exists(edfPath)) return;

        var name = BuildFileName(options, now, rr, st, "eeg", "edf");
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        File.Copy(edfPath, dst, overwrite: true);
    }

    // ===== Helpers =====

    private string? FindAoiJson(string stimUid, string? preferredResultUid)
    {
        if (!string.IsNullOrWhiteSpace(preferredResultUid))
        {
            var path = Path.Combine(_resultsDir, preferredResultUid, stimUid, "aoi.json");
            if (File.Exists(path)) return path;
        }

        var stimPath = Path.Combine(_expDir, "stimuli", stimUid, "aoi.json");
        if (File.Exists(stimPath)) return stimPath;

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

    private string BuildFileName(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st, string type, string ext)
    {
        return _resolver.Resolve(options.FilenameTemplate, now, _exp, rr.File, st, type, ext);
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

    private static string GetImageExtension(ExportImageFormat format)
        => format == ExportImageFormat.JPG ? "jpg" : "png";

    private static ImageFormat GetImageSaveFormat(ExportImageFormat format)
        => format == ExportImageFormat.JPG ? ImageFormat.Jpeg : ImageFormat.Png;

    private static void EnsureXlsxMemoryLimit(long rowCount, string datasetName)
    {
        if (rowCount <= ExcelMemoryRowLimit) return;
        throw new InvalidOperationException(
            $"Слишком много строк ({rowCount}) для XLSX ({datasetName}) по памяти. " +
            "Используйте CSV или режим с раздельными файлами.");
    }

    private static void EnsureXlsxCapacity(int rowCount, string datasetName)
    {
        EnsureXlsxCapacity((long)rowCount, datasetName);
    }

    private static void EnsureXlsxCapacity(long rowCount, string datasetName)
    {
        if (rowCount > ExcelMaxRows - 1)
            throw new InvalidOperationException(
                $"Слишком много строк ({rowCount}) для XLSX ({datasetName}). " +
                "Используйте CSV или режим с раздельными файлами.");

        EnsureXlsxMemoryLimit(rowCount, datasetName);
    }

    private static long CountRowsByRecordSize(string path, int recordSize)
    {
        var info = new FileInfo(path);
        if (info.Length <= 0 || recordSize <= 0) return 0;
        return info.Length / recordSize;
    }

    // ===== Tracker data reading =====

    private record ActionRecord(double time, uint mouse, uint key, double x, double y);

    // ===== CSV Writers =====

    private void WriteTrackerDataCsv(string path, IEnumerable<TrackerData> data, string? uidColumn)
    {
        using var sw = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
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

    private void WriteTrackerDataCsv(string path, IEnumerable<(string uid, TrackerData r)> data, string uidColumn)
    {
        using var sw = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        sw.WriteLine($"{uidColumn};time_sec;x;y;z;valid;lx;ly;rx;ry;lp;rp;lopen;ropen;leyex;leyey;leyez;reyex;reyey;reyez");

        foreach (var (uid, r) in data)
        {
            sw.Write(uid); sw.Write(';');
            WriteTrackerRow(sw, r);
        }
    }

    private void WriteTrackerDataCsvFromFile(string path, string sourcePath)
    {
        using var sw = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        sw.WriteLine("time_sec;x;y;z;valid;lx;ly;rx;ry;lp;rp;lopen;ropen;leyex;leyey;leyez;reyex;reyey;reyez");

        ForEachTrackerData(sourcePath, r => WriteTrackerRow(sw, r));
    }

    private void WriteTrackerDataCsvFromFiles(string path, IEnumerable<(string uid, string path)> sources, string uidColumn)
    {
        using var sw = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        sw.WriteLine($"{uidColumn};time_sec;x;y;z;valid;lx;ly;rx;ry;lp;rp;lopen;ropen;leyex;leyey;leyez;reyex;reyey;reyez");

        foreach (var (uid, sourcePath) in sources)
        {
            ForEachTrackerData(sourcePath, r => WriteTrackerRow(sw, r, uid));
        }
    }

    private static void WriteTrackerRow(StreamWriter sw, TrackerData r)
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

    private static void WriteTrackerRow(StreamWriter sw, TrackerData r, string uid)
    {
        sw.Write(uid); sw.Write(';');
        WriteTrackerRow(sw, r);
    }

    private void WriteActionsDataCsv(string path, IEnumerable<ActionRecord> data, string? uidColumn)
    {
        using var sw = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
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

    private void WriteActionsDataCsv(string path, IEnumerable<(string uid, ActionRecord r)> data, string uidColumn)
    {
        using var sw = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        sw.WriteLine($"{uidColumn};time_sec;mouse_button;keyboard_code;x;y");

        foreach (var (uid, r) in data)
        {
            sw.Write(uid); sw.Write(';');
            WriteActionsRow(sw, r);
        }
    }

    private void WriteActionsDataCsvFromFile(string path, string sourcePath)
    {
        using var sw = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        sw.WriteLine("time_sec;mouse_button;keyboard_code;x;y");

        ForEachActionRecord(sourcePath, r => WriteActionsRow(sw, r));
    }

    private void WriteActionsDataCsvFromFiles(string path, IEnumerable<(string uid, string path)> sources, string uidColumn)
    {
        using var sw = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        sw.WriteLine($"{uidColumn};time_sec;mouse_button;keyboard_code;x;y");

        foreach (var (uid, sourcePath) in sources)
        {
            ForEachActionRecord(sourcePath, r => WriteActionsRow(sw, r, uid));
        }
    }

    private static void WriteActionsRow(StreamWriter sw, ActionRecord r)
    {
        sw.Write(r.time.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
        sw.Write(r.mouse.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
        sw.Write(r.key.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
        sw.Write(r.x.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
        sw.WriteLine(r.y.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteActionsRow(StreamWriter sw, ActionRecord r, string uid)
    {
        sw.Write(uid); sw.Write(';');
        WriteActionsRow(sw, r);
    }

    private void WriteAoiDataCsv(string path, IEnumerable<(string stimUid, AoiElement aoi)> data)
    {
        using var sw = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        sw.WriteLine("stimul_uid;aoi_uid;aoi_name;aoi_type;color;line_width;points");

        foreach (var (stimUid, aoi) in data)
        {
            var points = string.Join(" ", aoi.NormalizedPoints.Select(p => $"{p.X.ToString(CultureInfo.InvariantCulture)},{p.Y.ToString(CultureInfo.InvariantCulture)}"));
            sw.Write(EscapeCsv(stimUid)); sw.Write(';');
            sw.Write(EscapeCsv(aoi.Uid)); sw.Write(';');
            sw.Write(EscapeCsv(aoi.Name)); sw.Write(';');
            sw.Write(aoi.Type.ToString()); sw.Write(';');
            sw.Write(aoi.ColorHex); sw.Write(';');
            sw.Write(aoi.LineWidth.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
            sw.WriteLine(points);
        }
    }

    // ===== XLSX Writers =====

    private void WriteTrackerDataXlsx(string path, IEnumerable<TrackerData> data, string? uidColumn)
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

        if (row - 2 <= ExcelAutoFitThreshold)
            ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    private void WriteTrackerDataXlsx(string path, IEnumerable<(string uid, TrackerData r)> data, string uidColumn)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("RawGaze");

        ws.Cell(1, 1).Value = uidColumn;
        var headers = new[] { "time_sec", "x", "y", "z", "valid", "lx", "ly", "rx", "ry", "lp", "rp", "lopen", "ropen", "leyex", "leyey", "leyez", "reyex", "reyey", "reyez" };
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 2).Value = headers[i];

        int row = 2;
        foreach (var (uid, r) in data)
        {
            ws.Cell(row, 1).Value = uid;
            ws.Cell(row, 2).Value = r.time;
            ws.Cell(row, 3).Value = r.x;
            ws.Cell(row, 4).Value = r.y;
            ws.Cell(row, 5).Value = r.z;
            ws.Cell(row, 6).Value = r.valid;
            ws.Cell(row, 7).Value = r.lx;
            ws.Cell(row, 8).Value = r.ly;
            ws.Cell(row, 9).Value = r.rx;
            ws.Cell(row, 10).Value = r.ry;
            ws.Cell(row, 11).Value = r.lp;
            ws.Cell(row, 12).Value = r.rp;
            ws.Cell(row, 13).Value = r.lopen;
            ws.Cell(row, 14).Value = r.ropen;
            ws.Cell(row, 15).Value = r.leyex;
            ws.Cell(row, 16).Value = r.leyey;
            ws.Cell(row, 17).Value = r.leyez;
            ws.Cell(row, 18).Value = r.reyex;
            ws.Cell(row, 19).Value = r.reyey;
            ws.Cell(row, 20).Value = r.reyez;
            row++;
        }

        if (row - 2 <= ExcelAutoFitThreshold)
            ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    private void WriteActionsDataXlsx(string path, IEnumerable<ActionRecord> data, string? uidColumn)
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

        if (row - 2 <= ExcelAutoFitThreshold)
            ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    private void WriteActionsDataXlsx(string path, IEnumerable<(string uid, ActionRecord r)> data, string uidColumn)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Actions");

        ws.Cell(1, 1).Value = uidColumn;
        ws.Cell(1, 2).Value = "time_sec";
        ws.Cell(1, 3).Value = "mouse_button";
        ws.Cell(1, 4).Value = "keyboard_code";
        ws.Cell(1, 5).Value = "x";
        ws.Cell(1, 6).Value = "y";

        int row = 2;
        foreach (var (uid, r) in data)
        {
            ws.Cell(row, 1).Value = uid;
            ws.Cell(row, 2).Value = r.time;
            ws.Cell(row, 3).Value = r.mouse;
            ws.Cell(row, 4).Value = r.key;
            ws.Cell(row, 5).Value = r.x;
            ws.Cell(row, 6).Value = r.y;
            row++;
        }

        if (row - 2 <= ExcelAutoFitThreshold)
            ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    private void WriteAoiDataXlsx(string path, IEnumerable<(string stimUid, AoiElement aoi)> data)
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
        foreach (var (stimUid, aoi) in data)
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

        if (row - 2 <= ExcelAutoFitThreshold)
            ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(';') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private static void ForEachTrackerData(string path, Action<TrackerData> onRecord)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[TrackerData.Size];

        while (true)
        {
            int read = fs.Read(buf, 0, buf.Length);
            if (read == 0 || read != buf.Length) break;

            var r = MemoryMarshal.Read<TrackerData>(buf);
            onRecord(r);
        }
    }

    private static IEnumerable<TrackerData> EnumerateTrackerData(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[TrackerData.Size];

        while (true)
        {
            int read = fs.Read(buf, 0, buf.Length);
            if (read == 0 || read != buf.Length) yield break;

            yield return MemoryMarshal.Read<TrackerData>(buf);
        }
    }

    private static IEnumerable<(string uid, TrackerData r)> EnumerateTrackerDataWithUid(IEnumerable<(string uid, string path)> sources)
    {
        foreach (var (uid, path) in sources)
        {
            foreach (var r in EnumerateTrackerData(path))
                yield return (uid, r);
        }
    }

    private static void ForEachActionRecord(string path, Action<ActionRecord> onRecord)
    {
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

            onRecord(new ActionRecord(time, mouse, key, x, y));
        }
    }

    private static IEnumerable<ActionRecord> EnumerateActionRecords(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[MkRecordSize];

        while (true)
        {
            int n = fs.Read(buf, 0, buf.Length);
            if (n == 0 || n != MkRecordSize) yield break;

            var span = buf.AsSpan();
            double time = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(span.Slice(0, 8)));
            uint mouse = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4));
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));
            double x = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(span.Slice(16, 8)));
            double y = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(span.Slice(24, 8)));

            yield return new ActionRecord(time, mouse, key, x, y);
        }
    }

    private static IEnumerable<(string uid, ActionRecord r)> EnumerateActionRecordsWithUid(IEnumerable<(string uid, string path)> sources)
    {
        foreach (var (uid, path) in sources)
        {
            foreach (var r in EnumerateActionRecords(path))
                yield return (uid, r);
        }
    }

    // ===== Text Metrics (CSV/XLSX) =====

    private void ExportTextMetrics(MultiExportOptions options, DateTime now, MultiExportResult rr, StimulFile st)
    {
        var textLayoutPath = FindTextLayoutJson(st.Uid, preferredResultUid: rr.Uid);
        if (textLayoutPath == null) return;

        var config = LoadTextLayoutConfig(textLayoutPath);
        if (config == null || string.IsNullOrWhiteSpace(config.Text)) return;

        var layout = TextLayoutEngine.ComputeLayout(config);
        if (layout.IsEmpty) return;

        // Получаем фиксации
        var fixations = GetFixationsForStimulus(rr.Uid, st.Uid);
        if (fixations.Count == 0) return;

        // Вычисляем метрики
        var settings = new TextAnalysisSettings { Layout = config };
        var analysis = ReadingMetricsCalculator.ComputeMetrics(fixations, layout, settings, 0, 0, 1920, 1080, 0.6f);

        if (analysis.WordMetrics == null || analysis.WordMetrics.Length == 0) return;

        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, rr, st, "text_metrics", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        if (options.DataFormat == ExportDataFormat.XLSX)
            WriteTextMetricsXlsx(dst, st.Uid, analysis.WordMetrics);
        else
            WriteTextMetricsCsv(dst, st.Uid, analysis.WordMetrics);
    }

    private void ExportTextMetrics_AggregatedPerResult(MultiExportOptions options, DateTime now, MultiExportResult rr, IReadOnlyList<StimulFile> stimuli)
    {
        var allMetrics = new List<(string stimUid, WordReadingMetrics metric)>();

        foreach (var st in stimuli)
        {
            var textLayoutPath = FindTextLayoutJson(st.Uid, preferredResultUid: rr.Uid);
            if (textLayoutPath == null) continue;

            var config = LoadTextLayoutConfig(textLayoutPath);
            if (config == null || string.IsNullOrWhiteSpace(config.Text)) continue;

            var layout = TextLayoutEngine.ComputeLayout(config);
            if (layout.IsEmpty) continue;

            var fixations = GetFixationsForStimulus(rr.Uid, st.Uid);
            if (fixations.Count == 0) continue;

            var settings = new TextAnalysisSettings { Layout = config };
            var analysis = ReadingMetricsCalculator.ComputeMetrics(fixations, layout, settings, 0, 0, 1920, 1080, 0.6f);

            if (analysis.WordMetrics != null)
            {
                foreach (var m in analysis.WordMetrics)
                    allMetrics.Add((st.Uid, m));
            }
        }

        if (allMetrics.Count == 0) return;

        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, rr, stimuli[0], "text_metrics_per_result", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        if (options.DataFormat == ExportDataFormat.XLSX)
            WriteTextMetricsAggregatedXlsx(dst, allMetrics);
        else
            WriteTextMetricsAggregatedCsv(dst, allMetrics);
    }

    private void ExportTextMetrics_AllInOne(MultiExportOptions options, DateTime now, IReadOnlyList<MultiExportResult> results, IReadOnlyList<StimulFile> stimuli)
    {
        var allMetrics = new List<(string stimUid, WordReadingMetrics metric)>();

        foreach (var st in stimuli)
        {
            var textLayoutPath = FindTextLayoutJson(st.Uid, preferredResultUid: results.Count > 0 ? results[0].Uid : null);
            if (textLayoutPath == null) continue;

            var config = LoadTextLayoutConfig(textLayoutPath);
            if (config == null || string.IsNullOrWhiteSpace(config.Text)) continue;

            var layout = TextLayoutEngine.ComputeLayout(config);
            if (layout.IsEmpty) continue;

            foreach (var rr in results)
            {
                var fixations = GetFixationsForStimulus(rr.Uid, st.Uid);
                if (fixations.Count == 0) continue;

                var settings = new TextAnalysisSettings { Layout = config };
                var analysis = ReadingMetricsCalculator.ComputeMetrics(fixations, layout, settings, 0, 0, 1920, 1080, 0.6f);

                if (analysis.WordMetrics != null)
                {
                    foreach (var m in analysis.WordMetrics)
                        allMetrics.Add((st.Uid, m));
                }
            }
        }

        if (allMetrics.Count == 0) return;

        var ext = options.DataFormat == ExportDataFormat.XLSX ? "xlsx" : "csv";
        var name = BuildFileName(options, now, results[0], stimuli[0], "text_metrics_all", ext);
        var dst = EnsureUniquePath(Path.Combine(options.OutputDir, name));

        if (options.DataFormat == ExportDataFormat.XLSX)
            WriteTextMetricsAggregatedXlsx(dst, allMetrics);
        else
            WriteTextMetricsAggregatedCsv(dst, allMetrics);
    }

    private string? FindTextLayoutJson(string stimUid, string? preferredResultUid)
    {
        // Ищем text_layout.json в папке результата
        if (!string.IsNullOrWhiteSpace(preferredResultUid))
        {
            var path = Path.Combine(_resultsDir, preferredResultUid, stimUid, "text_layout.json");
            if (File.Exists(path)) return path;
        }

        // Ищем в любом результате
        if (Directory.Exists(_resultsDir))
        {
            foreach (var resultDir in Directory.EnumerateDirectories(_resultsDir))
            {
                var path = Path.Combine(resultDir, stimUid, "text_layout.json");
                if (File.Exists(path)) return path;
            }
        }

        return null;
    }

    private TextLayoutConfig? LoadTextLayoutConfig(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TextLayoutConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch
        {
            return null;
        }
    }

    private List<Fixation> GetFixationsForStimulus(string resultUid, string stimUid)
    {
        var raw = GetRawSamplesForStim(resultUid, stimUid);
        if (raw.Count == 0) return new List<Fixation>();

        var preprocessed = AnalysisFixationPipeline.Preprocess(raw, _detectSettings);
        return AnalysisFixationPipeline.DetectIdt(preprocessed, 1920, 1080, _detectSettings);
    }

    private void WriteTextMetricsCsv(string path, string stimUid, WordReadingMetrics[] metrics)
    {
        using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);

        // Header
        writer.WriteLine("stimulus_id;word_index;word_text;line_index;fixation_count;ffd_sec;gd_sec;tfd_sec;go_past_sec;second_pass_sec;landing_pos;regressions_in;regressions_out;was_skipped;was_refixated");

        foreach (var m in metrics)
        {
            writer.WriteLine(string.Join(";",
                EscapeCsv(stimUid),
                m.WordIndex,
                EscapeCsv(m.WordText),
                m.LineIndex,
                m.FixationCount,
                m.FirstFixationDuration.ToString("F4", CultureInfo.InvariantCulture),
                m.GazeDuration.ToString("F4", CultureInfo.InvariantCulture),
                m.TotalFixationDuration.ToString("F4", CultureInfo.InvariantCulture),
                m.GoPastDuration.ToString("F4", CultureInfo.InvariantCulture),
                m.SecondPassDuration.ToString("F4", CultureInfo.InvariantCulture),
                m.InitialLandingPosition.ToString("F3", CultureInfo.InvariantCulture),
                m.NumberOfRegressionsIn,
                m.NumberOfRegressionsOut,
                m.WasSkipped ? "1" : "0",
                m.WasRefixated ? "1" : "0"
            ));
        }
    }

    private void WriteTextMetricsXlsx(string path, string stimUid, WordReadingMetrics[] metrics)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("TextMetrics");

        // Header
        ws.Cell(1, 1).Value = "stimulus_id";
        ws.Cell(1, 2).Value = "word_index";
        ws.Cell(1, 3).Value = "word_text";
        ws.Cell(1, 4).Value = "line_index";
        ws.Cell(1, 5).Value = "fixation_count";
        ws.Cell(1, 6).Value = "ffd_sec";
        ws.Cell(1, 7).Value = "gd_sec";
        ws.Cell(1, 8).Value = "tfd_sec";
        ws.Cell(1, 9).Value = "go_past_sec";
        ws.Cell(1, 10).Value = "second_pass_sec";
        ws.Cell(1, 11).Value = "landing_pos";
        ws.Cell(1, 12).Value = "regressions_in";
        ws.Cell(1, 13).Value = "regressions_out";
        ws.Cell(1, 14).Value = "was_skipped";
        ws.Cell(1, 15).Value = "was_refixated";

        int row = 2;
        foreach (var m in metrics)
        {
            ws.Cell(row, 1).Value = stimUid;
            ws.Cell(row, 2).Value = m.WordIndex;
            ws.Cell(row, 3).Value = m.WordText;
            ws.Cell(row, 4).Value = m.LineIndex;
            ws.Cell(row, 5).Value = m.FixationCount;
            ws.Cell(row, 6).Value = m.FirstFixationDuration;
            ws.Cell(row, 7).Value = m.GazeDuration;
            ws.Cell(row, 8).Value = m.TotalFixationDuration;
            ws.Cell(row, 9).Value = m.GoPastDuration;
            ws.Cell(row, 10).Value = m.SecondPassDuration;
            ws.Cell(row, 11).Value = m.InitialLandingPosition;
            ws.Cell(row, 12).Value = m.NumberOfRegressionsIn;
            ws.Cell(row, 13).Value = m.NumberOfRegressionsOut;
            ws.Cell(row, 14).Value = m.WasSkipped ? 1 : 0;
            ws.Cell(row, 15).Value = m.WasRefixated ? 1 : 0;
            row++;
        }

        if (row < ExcelAutoFitThreshold)
            ws.Columns().AdjustToContents();

        wb.SaveAs(path);
    }

    private void WriteTextMetricsAggregatedCsv(string path, List<(string stimUid, WordReadingMetrics metric)> data)
    {
        using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);

        writer.WriteLine("stimulus_id;word_index;word_text;line_index;fixation_count;ffd_sec;gd_sec;tfd_sec;go_past_sec;second_pass_sec;landing_pos;regressions_in;regressions_out;was_skipped;was_refixated");

        foreach (var (stimUid, m) in data)
        {
            writer.WriteLine(string.Join(";",
                EscapeCsv(stimUid),
                m.WordIndex,
                EscapeCsv(m.WordText),
                m.LineIndex,
                m.FixationCount,
                m.FirstFixationDuration.ToString("F4", CultureInfo.InvariantCulture),
                m.GazeDuration.ToString("F4", CultureInfo.InvariantCulture),
                m.TotalFixationDuration.ToString("F4", CultureInfo.InvariantCulture),
                m.GoPastDuration.ToString("F4", CultureInfo.InvariantCulture),
                m.SecondPassDuration.ToString("F4", CultureInfo.InvariantCulture),
                m.InitialLandingPosition.ToString("F3", CultureInfo.InvariantCulture),
                m.NumberOfRegressionsIn,
                m.NumberOfRegressionsOut,
                m.WasSkipped ? "1" : "0",
                m.WasRefixated ? "1" : "0"
            ));
        }
    }

    private void WriteTextMetricsAggregatedXlsx(string path, List<(string stimUid, WordReadingMetrics metric)> data)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("TextMetrics");

        // Header
        ws.Cell(1, 1).Value = "stimulus_id";
        ws.Cell(1, 2).Value = "word_index";
        ws.Cell(1, 3).Value = "word_text";
        ws.Cell(1, 4).Value = "line_index";
        ws.Cell(1, 5).Value = "fixation_count";
        ws.Cell(1, 6).Value = "ffd_sec";
        ws.Cell(1, 7).Value = "gd_sec";
        ws.Cell(1, 8).Value = "tfd_sec";
        ws.Cell(1, 9).Value = "go_past_sec";
        ws.Cell(1, 10).Value = "second_pass_sec";
        ws.Cell(1, 11).Value = "landing_pos";
        ws.Cell(1, 12).Value = "regressions_in";
        ws.Cell(1, 13).Value = "regressions_out";
        ws.Cell(1, 14).Value = "was_skipped";
        ws.Cell(1, 15).Value = "was_refixated";

        int row = 2;
        foreach (var (stimUid, m) in data)
        {
            ws.Cell(row, 1).Value = stimUid;
            ws.Cell(row, 2).Value = m.WordIndex;
            ws.Cell(row, 3).Value = m.WordText;
            ws.Cell(row, 4).Value = m.LineIndex;
            ws.Cell(row, 5).Value = m.FixationCount;
            ws.Cell(row, 6).Value = m.FirstFixationDuration;
            ws.Cell(row, 7).Value = m.GazeDuration;
            ws.Cell(row, 8).Value = m.TotalFixationDuration;
            ws.Cell(row, 9).Value = m.GoPastDuration;
            ws.Cell(row, 10).Value = m.SecondPassDuration;
            ws.Cell(row, 11).Value = m.InitialLandingPosition;
            ws.Cell(row, 12).Value = m.NumberOfRegressionsIn;
            ws.Cell(row, 13).Value = m.NumberOfRegressionsOut;
            ws.Cell(row, 14).Value = m.WasSkipped ? 1 : 0;
            ws.Cell(row, 15).Value = m.WasRefixated ? 1 : 0;
            row++;
        }

        if (row < ExcelAutoFitThreshold)
            ws.Columns().AdjustToContents();

        wb.SaveAs(path);
    }
}
