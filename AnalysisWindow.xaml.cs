using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using NeuroBureau.Experiment.Controls;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace NeuroBureau.Experiment;

public partial class AnalysisWindow : Window
{
    private List<BeeSeries>? _cachedBeeSeries; 
    private enum PlaybackState
    {
        Stop,
        Play,
        Pause
    }

    private readonly string _expDir;
    private readonly HashSet<string> _initialSelectedResultUids;
    private readonly Dictionary<string, string?> _initialSelectedResultRgba;
    private readonly int _initialSelectedCount;
    private readonly string? _primaryResultUid;

    private ExperimentFile? _exp;
    private bool _currentStimIsVideo;

    private readonly List<StimulusItem> _stimuli = new();
    private readonly List<ResultDisplayItem> _resultsDisplay = new();
    private int _screenW;
    private int _screenH;
    private int _screenWmm;
    private int _screenHmm;

    private AnalysisDetectionSettings _detectSettings = new();
    private AnalysisVisualizationSettings _visualSettings = new();
    private double _stimOffX, _stimOffY, _stimW2, _stimH2;
    private bool _stimIsVideo;

    private string? _trackerUid;
    private readonly Dictionary<string, List<RawGazeSample>> _rawCache = new();
    private readonly Dictionary<string, List<Fixation>> _fixCache = new();
    private readonly Dictionary<string, double> _stimDurationCache = new();
    private readonly Dictionary<string, StimulusVizSettings> _vizCache = new();
    private readonly Dictionary<string, List<GsrSample>> _gsrCache = new();
    private bool _hasKgr;
    private string? _kgrDeviceUid;

    // time-slice (для картинок/цвета)
    private string? _sliceStimUid;
    private double _sliceMin;
    private double _sliceMax;
    private double _sliceTotalSec;
    private bool _suppressSliceEvents;
    private bool _suppressSliceTimeEvents;
    private bool _isInternalSliceTimeUpdate;

    private string? _currentStimUid;
    private bool _videoTimeMode;                 // false = превью (все фиксации), true = по времени
    private const double FixTimeSliceSec = 30.0;  // “окно” отображения (сек). Подберёшь как в Vala
    private StimulusVizSettings _currentVizSettings = new();
    private bool _suppressVizUi;
    private PlaybackState _playbackState = PlaybackState.Stop;
    private double _currentTime;
    private bool _stopRequested;
    private readonly Stopwatch _stopwatch = new();
    private double _startTimeAtPlay;

    // VLC
    private bool _vlcInited;
    private LibVLCSharp.Shared.LibVLC? _vlc;
    private LibVLCSharp.Shared.MediaPlayer? _player;
    private LibVLCSharp.Shared.Media? _currentMedia;
    private static string K(string resultUid, string stimUid, EyeSelection eye) => $"{resultUid}|{stimUid}|{eye}";
    private static string KF(string resultUid, string stimUid) => $"{resultUid}|{stimUid}";
    private static string KG(string resultUid, string stimUid) => $"{resultUid}|{stimUid}";


    private DispatcherTimer? _timer;
    private DispatcherTimer? _slicePlaybackTimer;
    private bool _dragging;
    private bool _isAoiMode = false;
    private AoiType _currentAoiType = AoiType.Rectangle;
    private AoiElement? _currentDraftAoi = null; // AOI, который рисуем прямо сейчас
    private List<AoiElement> _aoiList = new();
    private AoiMetricsResult[] _cachedAoiMetrics = Array.Empty<AoiMetricsResult>();
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly record struct GsrSample(double TimeSec, double Sr, double Sc, double Hr, double Ppg);

    private IEnumerable<ResultDisplayItem> EnumerateVisibleResults()
    {
        foreach (var r in _resultsDisplay)
        {
            if (r.IsVisible)
                yield return r;
        }
    }

    private void InitKgrPanel()
    {
        var kgrDevice = _exp?.Devices
            .FirstOrDefault(d => string.Equals(d.DevType, "ShimmerGSR", StringComparison.OrdinalIgnoreCase));

        _kgrDeviceUid = kgrDevice?.Uid;
        _hasKgr = !string.IsNullOrWhiteSpace(_kgrDeviceUid);

        if (_hasKgr)
        {
            KgrChartsPanel.Visibility = Visibility.Visible;
            KgrSpacerColumn.Width = new GridLength(14);
            KgrColumn.Width = new GridLength(360);
        }
        else
        {
            KgrChartsPanel.Visibility = Visibility.Collapsed;
            KgrSpacerColumn.Width = new GridLength(0);
            KgrColumn.Width = new GridLength(0);
        }
    }

    // Добавьте этот метод в AnalysisWindow.xaml.cs (например, рядом с BuildHeatmapSamples)
    private void AoiType_Rect_Checked(object sender, RoutedEventArgs e) => _currentAoiType = AoiType.Rectangle;
    private void AoiType_Ellipse_Checked(object sender, RoutedEventArgs e) => _currentAoiType = AoiType.Ellipse;
    private void AoiType_Poly_Checked(object sender, RoutedEventArgs e) => _currentAoiType = AoiType.Polygon;

    private void AoiDeleteLast_Click(object sender, RoutedEventArgs e)
    {
        if (_aoiList.Count > 0)
        {
            _aoiList.RemoveAt(_aoiList.Count - 1);
            SaveAoisForStimulus();
            RecalculateAoiMetrics();
            AoiOverlay.SetData(_aoiList, _cachedAoiMetrics);
        }
    }
    private List<HeatmapSeries> BuildHeatmapSeriesForCurrentStim(float tMin, float tMax)
    {
        var list = new List<HeatmapSeries>();
        if (_currentStimUid == null) return list;

        foreach (var result in EnumerateVisibleResults())
        {
            var samples = BuildHeatmapSamples(result.ResultUid, _currentStimUid, tMin, tMax);
            if (samples.Count > 0)
                list.Add(new HeatmapSeries(samples, result.Color));
        }

        return list;
    }
    public AnalysisWindow(string expDir, IReadOnlyList<SelectedResult> results, ExperimentFile? exp)
    {
        InitializeComponent();
        _expDir = expDir;

        _initialSelectedResultUids = results
            .Select(r => (r.ResultUid ?? "").Trim())
            .Where(uid => !string.IsNullOrWhiteSpace(uid))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _initialSelectedResultRgba = results
            .Where(r => !string.IsNullOrWhiteSpace(r.ResultUid))
            .GroupBy(r => r.ResultUid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Rgba, StringComparer.OrdinalIgnoreCase);

        _initialSelectedCount = _initialSelectedResultUids.Count;
        _primaryResultUid = results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.ResultUid))?.ResultUid?.Trim();

        _exp = exp ?? TryLoadExperiment(expDir);
        _visualSettings = AppConfigManager.LoadAnalysisVisualizationSettings();

        Title = _initialSelectedCount == 1 && !string.IsNullOrWhiteSpace(_primaryResultUid)
            ? $"Анализ — {_primaryResultUid}"
            : $"Анализ — {_initialSelectedCount} результатов";
    }

    private static ExperimentFile? TryLoadExperiment(string expDir)
    {
        var path = Path.Combine(expDir, "exp.json");
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<ExperimentFile>(File.ReadAllText(path), _jsonOpts);
        }
        catch
        {
            return null;
        }
    }


    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyVisualizationSettings();
        LoadResultInfo();
        LoadStimuli();
        LoadResultsDisplay();
        InitKgrPanel();
        // В Window_Loaded
        UpdateAoiColorBtnPreview();
        StimuliList.ItemsSource = _stimuli;
        ResultsDataGrid.ItemsSource = _resultsDisplay;
        if (_stimuli.Count > 0)
            StimuliList.SelectedIndex = 0;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, __) => TickUi();
        _timer.Start();

        _slicePlaybackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _slicePlaybackTimer.Tick += (_, __) => TickSlicePlayback();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try { _timer?.Stop(); } catch { }
        try { _slicePlaybackTimer?.Stop(); } catch { }
        try { StopVideoUi(); } catch { }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        try { DisposeVlc(); } catch { }
    }
    private string RawCacheKey(string resultUid, string stimUid) => K(resultUid, stimUid, _detectSettings.Eye);

    private string GsrCacheKey(string resultUid, string stimUid) => KG(resultUid, stimUid);

    private List<GsrSample> GetGsrSamplesForStim(string resultUid, string stimUid)
    {
        var key = GsrCacheKey(resultUid, stimUid);
        if (_gsrCache.TryGetValue(key, out var cached)) return cached;

        var list = ReadGsrSamplesForStim(resultUid, stimUid);
        _gsrCache[key] = list;
        return list;
    }

    private List<GsrSample> ReadGsrSamplesForStim(string resultUid, string stimUid)
    {
        if (string.IsNullOrWhiteSpace(_kgrDeviceUid)) return new();

        var path = Path.Combine(_expDir, "results", resultUid, stimUid, _kgrDeviceUid);
        if (!File.Exists(path)) return new();

        var list = new List<GsrSample>(2048);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> buf = stackalloc byte[GsrData.Size];

        double t0 = double.NaN;
        double prevT = double.NegativeInfinity;

        while (true)
        {
            int n = fs.Read(buf);
            if (n == 0) break;
            if (n != GsrData.Size) break;

            double t = ReadD(buf, 0);
            if (!double.IsFinite(t)) continue;
            if (t <= prevT) continue;
            prevT = t;

            if (double.IsNaN(t0)) t0 = t;
            double timeSec = t - t0;
            if (!double.IsFinite(timeSec) || timeSec < 0) continue;

            double hr = ReadD(buf, 8);
            double sr = ReadD(buf, 16);
            double sc = ReadD(buf, 24);
            double ppg = ReadD(buf, 40);

            list.Add(new GsrSample(timeSec, sr, sc, hr, ppg));
        }

        return list;

        static double ReadD(Span<byte> b, int off) =>
            BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(b.Slice(off, 8)));
    }

    private void UpdateKgrChartsForStim(string stimUid)
    {
        if (!_hasKgr || string.IsNullOrWhiteSpace(stimUid))
            return;

        if (string.IsNullOrWhiteSpace(_primaryResultUid))
        {
            ClearKgrCharts("Нет данных КГР для стимула");
            return;
        }

        var samples = GetGsrSamplesForStim(_primaryResultUid, stimUid);
        if (samples.Count < 2)
        {
            ClearKgrCharts("Нет данных КГР для стимула");
            return;
        }

        double tMin = samples[0].TimeSec;
        double tMax = samples[^1].TimeSec;
        if (tMax <= tMin) tMax = tMin + 1;

        var srPoints = samples
            .Where(s => double.IsFinite(s.Sr))
            .Select(s => new MetricPoint(s.TimeSec, s.Sr))
            .ToList();
        var scPoints = samples
            .Where(s => double.IsFinite(s.Sc))
            .Select(s => new MetricPoint(s.TimeSec, s.Sc))
            .ToList();
        var hrPoints = samples
            .Where(s => double.IsFinite(s.Hr))
            .Select(s => new MetricPoint(s.TimeSec, s.Hr))
            .ToList();
        var ppgPoints = samples
            .Where(s => double.IsFinite(s.Ppg))
            .Select(s => new MetricPoint(s.TimeSec, s.Ppg))
            .ToList();

        if (srPoints.Count > 0)
        {
            var min = srPoints.Min(p => p.Value);
            var max = srPoints.Max(p => p.Value);
            var range = Math.Max(1.0, max - min);
            KgrSrChart.SetData(srPoints, tMin, tMax, min - range * 0.1, max + range * 0.1, double.NaN,
                "КГР: Сопротивление (SR)", null, "кОм");
        }
        else
        {
            KgrSrChart.Clear("Нет данных SR");
        }

        if (scPoints.Count > 0)
        {
            var min = scPoints.Min(p => p.Value);
            var max = scPoints.Max(p => p.Value);
            var range = Math.Max(1.0, max - min);
            KgrScChart.SetData(scPoints, tMin, tMax, min - range * 0.1, max + range * 0.1, double.NaN,
                "КГР: Проводимость (SC)", null, "мкСм");
        }
        else
        {
            KgrScChart.Clear("Нет данных SC");
        }

        if (hrPoints.Count > 0)
        {
            var min = Math.Max(0, hrPoints.Min(p => p.Value));
            var max = Math.Min(200, hrPoints.Max(p => p.Value));
            if (max <= min) max = min + 1;
            KgrHrChart.SetData(hrPoints, tMin, tMax, min - 10, max + 10, double.NaN,
                "Пульс (HR)", null, "уд/мин");
        }
        else
        {
            KgrHrChart.Clear("Нет данных HR");
        }

        if (ppgPoints.Count > 0)
        {
            var min = ppgPoints.Min(p => p.Value);
            var max = ppgPoints.Max(p => p.Value);
            var range = Math.Max(1.0, max - min);
            KgrPpgChart.SetData(ppgPoints, tMin, tMax, min - range * 0.1, max + range * 0.1, double.NaN,
                "Фотоплетизмограмма (PPG)", null, "усл.ед.");
        }
        else
        {
            KgrPpgChart.Clear("Нет данных PPG");
        }
    }

    private void ClearKgrCharts(string message)
    {
        KgrSrChart.Clear(message);
        KgrScChart.Clear(message);
        KgrHrChart.Clear(message);
        KgrPpgChart.Clear(message);
    }

    private List<RawGazeSample> GetRawSamplesForStim(string resultUid, string stimUid)
    {
        var key = RawCacheKey(resultUid, stimUid);
        if (_rawCache.TryGetValue(key, out var cached)) return cached;

        var list = ReadRawSamplesForStim(resultUid, stimUid, _detectSettings.Eye);
        _rawCache[key] = list;
        return list;
    }
    private List<BeeSeries> BuildBeeSeries(string stimUid)
    {
        var series = new List<BeeSeries>();
        
        // Проверка на корректность размеров экрана
        if (_screenW <= 0 || _screenH <= 0) return series;

        // Параметры для перевода координат (из translate_result_to_image)
        float ox = (float)_stimOffX;
        float oy = (float)_stimOffY;
        float maxX = (float)_stimW2;
        float maxY = (float)_stimH2;

        foreach (var result in EnumerateVisibleResults())
        {
            var raw = GetRawSamplesForStim(result.ResultUid, stimUid);
            if (raw.Count == 0) continue;

            var beeSamples = new List<BeeSample>(raw.Count);
            
            foreach (var r in raw)
            {
                if (!r.Valid) continue;

                // 1. Нормализованные (0..1) -> Пиксели экрана
                float x = r.Xn * _screenW;
                float y = r.Yn * _screenH;

                // 2. Пиксели экрана -> Пиксели внутри стимула (с учетом черных полос)
                x -= ox;
                y -= oy;

                // 3. Отсекаем то, что за границами стимула
                if (x >= 0 && y >= 0 && x <= maxX && y <= maxY)
                {
                    beeSamples.Add(new BeeSample(r.TimeSec, x, y));
                }
            }

            if (beeSamples.Count > 0)
            {
                series.Add(new BeeSeries(beeSamples, result.Color));
            }
        }
        return series;
    }
    private List<RawGazeSample> ReadRawSamplesForStim(string resultUid, string stimUid, EyeSelection eye)
    {
        var trackerUid = GetTrackerUid();
        if (string.IsNullOrWhiteSpace(trackerUid)) return new();

        var p = Path.Combine(_expDir, "results", resultUid, stimUid, trackerUid);

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

            // время должно быть строго монотонным (иначе ломаются dt и бинарные поиски)
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
                // reject NaN/Inf и мусорные координаты
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
            // LEYEZ / REYEZ (в метрах) — если оба глаза есть, берём среднее.
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

    private (int wPx, int hPx, int wMm, int hMm) ReadStimulusScreenInfo(string resultUid, string stimUid)
    {
        try
        {
            var path = Path.Combine(_expDir, "results", resultUid, stimUid, "result.json");
            if (!File.Exists(path))
                return (_screenW, _screenH, _screenWmm, _screenHmm);

            var json = File.ReadAllText(path);
            var result = JsonSerializer.Deserialize<ResultFile>(json, _jsonOpts);
            if (result == null)
                return (_screenW, _screenH, _screenWmm, _screenHmm);

            int wPx = result.ScreenWidthPx > 0 ? result.ScreenWidthPx : _screenW;
            int hPx = result.ScreenHeightPx > 0 ? result.ScreenHeightPx : _screenH;
            int wMm = result.ScreenWidthMm > 0 ? result.ScreenWidthMm : _screenWmm;
            int hMm = result.ScreenHeightMm > 0 ? result.ScreenHeightMm : _screenHmm;

            return (wPx, hPx, wMm, hMm);
        }
        catch
        {
            return (_screenW, _screenH, _screenWmm, _screenHmm);
        }
    }

    private IReadOnlyList<Fixation> EnsureFixationsForStim(string resultUid, string stimUid)
    {
        var key = K(resultUid, stimUid, _detectSettings.Eye);
        if (_fixCache.TryGetValue(key, out var cached))
            return cached;

        var raw = GetRawSamplesForStim(resultUid, stimUid);
        if (raw.Count > 0)
            _stimDurationCache[KF(resultUid, stimUid)] = raw[^1].TimeSec;

        var pre = AnalysisFixationPipeline.Preprocess(raw, _detectSettings);

        List<Fixation> fixScreen = _detectSettings.Algorithm == FixationAlgorithm.Ivt
            ? AnalysisFixationPipeline.DetectIvt(pre, _screenW, _screenH, _screenWmm, _screenHmm, _detectSettings)
            : AnalysisFixationPipeline.DetectIdt(pre, _screenW, _screenH, _detectSettings);

        // ВАЖНО: переводим в координаты отображаемого стимула (w2/h2 + offsets)
        var fixMapped = MapFixationsToStimRect(fixScreen, _stimOffX, _stimOffY, _stimW2, _stimH2);

        // на всякий случай — по времени
        fixMapped.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));

        _fixCache[key] = fixMapped;
        return fixMapped;
    }

    private (List<Fixation> Screen, List<Fixation> Stim) EnsureFixationsForStimPaired(string resultUid, string stimUid)
    {
        var raw = GetRawSamplesForStim(resultUid, stimUid);
        if (raw.Count > 0)
            _stimDurationCache[KF(resultUid, stimUid)] = raw[^1].TimeSec;

        var pre = AnalysisFixationPipeline.Preprocess(raw, _detectSettings);

        List<Fixation> fixScreen = _detectSettings.Algorithm == FixationAlgorithm.Ivt
            ? AnalysisFixationPipeline.DetectIvt(pre, _screenW, _screenH, _screenWmm, _screenHmm, _detectSettings)
            : AnalysisFixationPipeline.DetectIdt(pre, _screenW, _screenH, _detectSettings);

        var fixStim = MapFixationsToStimRectPaired(fixScreen, _stimOffX, _stimOffY, _stimW2, _stimH2, out var fixScreenKept);

        fixStim.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        fixScreenKept.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));

        return (fixScreenKept, fixStim);
    }

    private List<FixationSeries> BuildFixationSeriesForStim(string stimUid)
    {
        var series = new List<FixationSeries>();

        foreach (var result in EnumerateVisibleResults())
        {
            var fix = EnsureFixationsForStim(result.ResultUid, stimUid);
            if (fix.Count > 0)
                series.Add(new FixationSeries(fix, result.Color));
        }

        return series;
    }

    private List<FixationSeries> BuildFixationSeriesForWindow(string stimUid, float tMin, float tMax)
    {
        var series = new List<FixationSeries>();

        foreach (var result in EnumerateVisibleResults())
        {
            var fix = EnsureFixationsForStim(result.ResultUid, stimUid);
            if (fix.Count == 0) continue;

            var visible = SliceByStartTime(fix, tMin, tMax);
            if (visible.Count > 0)
                series.Add(new FixationSeries(visible, result.Color));
        }

        return series;
    }

    private bool TryGetRawTimeRange(string stimUid, out double tMin, out double tMax)
    {
        tMin = 0;
        tMax = 0;
        bool hasData = false;

        foreach (var result in EnumerateVisibleResults())
        {
            var raw = GetRawSamplesForStim(result.ResultUid, stimUid);
            if (raw.Count < 2) continue;

            double start = raw[0].TimeSec;
            double end = raw[^1].TimeSec;

            if (!hasData)
            {
                tMin = start;
                tMax = end;
                hasData = true;
            }
            else
            {
                tMin = Math.Min(tMin, start);
                tMax = Math.Max(tMax, end);
            }
        }

        if (!hasData) return false;
        if (tMax <= tMin) tMax = tMin + 1;
        return true;
    }

    private double GetStimTotalSecForSelection(string stimUid)
    {
        double total = 0;
        foreach (var result in EnumerateVisibleResults())
            total = Math.Max(total, GetStimTotalSec(result.ResultUid, stimUid));
        return total;
    }


    // -------- load --------

    private void LoadResultInfo()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_primaryResultUid))
            {
                ResultInfoText.Text = "Не выбран результат для анализа";
                return;
            }

            var p = Path.Combine(_expDir, "results", _primaryResultUid, "result.json");
            if (!File.Exists(p))
            {
                ResultInfoText.Text = $"result.json не найден: {p}";
                return;
            }

            var r = JsonSerializer.Deserialize<ResultFile>(File.ReadAllText(p), _jsonOpts);
            if (r == null)
            {
                ResultInfoText.Text = "result.json не распарсился";
                return;
            }

            int w = r.ScreenWidthPx;
            int h = r.ScreenHeightPx;

            int wmm = r.ScreenWidthMm;
            int hmm = r.ScreenHeightMm;

            // Если чего-то нет в result.json — берём из системы.
            var sys = GetSystemScreenInfo();

            if (w <= 0 || h <= 0)
            {
                w = sys.wPx;
                h = sys.hPx;
            }

            if (wmm <= 0 || hmm <= 0)
            {
                wmm = sys.wMm;
                hmm = sys.hMm;
            }

            _screenW = w;
            _screenH = h;
            _screenWmm = wmm;
            _screenHmm = hmm;

            // и дальше уже настраиваешь виртуальный экран/оверлей:
            ScreenCanvas.Width = _screenW;
            ScreenCanvas.Height = _screenH;

            FixOverlay.Width = _screenW;
            FixOverlay.Height = _screenH;
            Canvas.SetLeft(FixOverlay, 0);
            Canvas.SetTop(FixOverlay, 0);

            EmptyHint.Width = _screenW;
            EmptyHint.Height = _screenH;
            Canvas.SetLeft(EmptyHint, 0);
            Canvas.SetTop(EmptyHint, 0);


            var dt = DateTimeOffset.FromUnixTimeSeconds(r.TimeUnix).ToLocalTime().DateTime;
            if (_initialSelectedCount == 1)
            {
                ResultInfoText.Text = $"Результат: {dt:dd.MM.yyyy HH:mm:ss} • длительность: {r.DurationSec} c • экран: {_screenW}×{_screenH} ({_screenWmm}×{_screenHmm} мм)";
            }
            else
            {
                ResultInfoText.Text = $"Результатов: {_initialSelectedCount} • пример: {dt:dd.MM.yyyy HH:mm:ss} • экран: {_screenW}×{_screenH} ({_screenWmm}×{_screenHmm} мм)";
            }
        }
        catch (Exception ex)
        {
            ResultInfoText.Text = $"Ошибка чтения result.json: {ex.Message}";
        }
    }


    private void LoadResultsDisplay()
    {
        _resultsDisplay.Clear();

        try
        {
            var resultsDir = Path.Combine(_expDir, "results");
            if (!Directory.Exists(resultsDir))
                return;

            int fallbackColorIndex = 0;

            foreach (var folder in Directory.EnumerateDirectories(resultsDir))
            {
                var uid = Path.GetFileName(folder);
                if (string.IsNullOrWhiteSpace(uid))
                    continue;

                var resultJsonPath = Path.Combine(folder, "result.json");
                if (!File.Exists(resultJsonPath))
                    continue;

                try
                {
                    var resultFile = JsonSerializer.Deserialize<ResultFile>(File.ReadAllText(resultJsonPath), _jsonOpts);
                    if (resultFile == null)
                        continue;

                    var date = DateTimeOffset.FromUnixTimeSeconds(resultFile.TimeUnix).ToLocalTime().DateTime;
                    var duration = resultFile.DurationSec;
                    var name = GetResultName(resultFile.CharsData);

                    Color color;
                    if (!TryParseRgbaColor(resultFile.Rgba, out color))
                    {
                        if (_initialSelectedResultRgba.TryGetValue(uid, out var overrideRgba) && TryParseRgbaColor(overrideRgba, out var overrideColor))
                        {
                            color = overrideColor;
                        }
                        else
                        {
                            color = DefaultResultColors[fallbackColorIndex % DefaultResultColors.Length];
                            fallbackColorIndex++;
                        }
                    }

                    var isVisible = _initialSelectedResultUids.Contains(uid);

                    var item = new ResultDisplayItem(uid, color, date, duration, name, isVisible);
                    item.PropertyChanged += ResultDisplayItem_PropertyChanged;
                    _resultsDisplay.Add(item);
                }
                catch
                {
                    // Пропускаем результаты с ошибками парсинга
                }
            }

            // Сортируем по дате (новые сверху)
            var sorted = _resultsDisplay.OrderByDescending(r => r.Date).ToList();
            _resultsDisplay.Clear();
            foreach (var item in sorted)
                _resultsDisplay.Add(item);
        }
        catch
        {
            // Игнорируем ошибки загрузки результатов
        }
    }

    private void ResultDisplayItem_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ResultDisplayItem.IsVisible) && e.PropertyName != nameof(ResultDisplayItem.Color))
            return;

        _cachedBeeSeries = null;

        // В режиме AOI нужно сразу пересчитать метрики, иначе таблица/оверлей останутся от старого набора результатов.
        if (_currentStimUid != null && AoiOverlay.Visibility == Visibility.Visible)
        {
            RecalculateAoiMetrics();
            AoiOverlay.SetData(_aoiList, _cachedAoiMetrics);
            RefreshAoiGrid();
        }

        RefreshCurrentFixations();
    }

    private void ResultColorSwatch_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (sender is not Border border || border.DataContext is not ResultDisplayItem item)
            return;

        var cur = item.Color;

        using var dialog = new WinForms.ColorDialog
        {
            FullOpen = true,
            Color = Drawing.Color.FromArgb(cur.A, cur.R, cur.G, cur.B)
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        var c = dialog.Color;
        item.Color = Color.FromRgb(c.R, c.G, c.B);
        SaveResultColorToDisk(item.ResultUid, item.ColorHex);
    }

    private void SaveResultColorToDisk(string? resultUid, string hexColor)
    {
        if (string.IsNullOrWhiteSpace(resultUid)) return;

        var resultPath = Path.Combine(_expDir, "results", resultUid, "result.json");
        if (!File.Exists(resultPath)) return;

        try
        {
            var json = File.ReadAllText(resultPath);
            var resultObj = JsonSerializer.Deserialize<ResultFile>(json, _jsonOpts);
            if (resultObj == null) return;

            resultObj.Rgba = hexColor;

            var writeOpts = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            File.WriteAllText(resultPath, JsonSerializer.Serialize(resultObj, writeOpts));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка сохранения цвета: {ex.Message}", "Анализ", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string GetResultName(List<CharValue> charsData)
    {
        if (charsData == null || charsData.Count == 0) return "";

        // Ищем характеристику с именем
        var nameChar = charsData.FirstOrDefault(c =>
            c.Name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Equals("имя", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Equals("participant", StringComparison.OrdinalIgnoreCase));

        return nameChar?.Val ?? "";
    }

    private void LoadStimuli()
    {
        _stimuli.Clear();

        try
        {
            _exp ??= JsonSerializer.Deserialize<ExperimentFile>(
                File.ReadAllText(Path.Combine(_expDir, "exp.json")), _jsonOpts);

            if (_exp == null) return;

            foreach (var st in _exp.Stimuls)
            {
                var kind = st.Kind ?? 0;
                if (kind == 0) continue; // калибровки пока пропускаем

                var path = ResolveStimulusPath(_expDir, st.Uid, st.Filename);
                var hasFile = path != null && File.Exists(path);

                var isVideo = hasFile && IsVideoFile(path!);
                var isImage = hasFile && IsImageFile(path!);

                var title = !string.IsNullOrWhiteSpace(st.Filename)
                    ? Path.GetFileName(st.Filename)
                    : (!string.IsNullOrWhiteSpace(st.Uid) ? st.Uid : "stimulus");

                var bg = TryParseRgbaBrush(st.Rgba);

                var subtitle = isVideo ? "Видео" :
                               isImage ? "Изображение" :
                               bg != null ? "Цветовой стимул" :
                               "Файл не найден";

                var thumbnail = hasFile && isImage ? GenerateThumbnail(path!, isImage, bg) : null;
                var previewBg = bg ?? Brushes.Transparent;

                _stimuli.Add(new StimulusItem
                {
                    Title = title,
                    Subtitle = subtitle,
                    Uid = st.Uid,
                    FilePath = hasFile ? path : null,
                    IsVideo = isVideo,
                    IsImage = isImage,
                    ColorBrush = bg,
                    Scale = st.Scale,
                    Thumbnail = thumbnail,
                    PreviewBackground = previewBg
                });
            }
        }
        catch
        {
            // минимальный каркас: если что-то не так — просто пустой список
        }
    }

    // -------- UI actions --------

    private void StimuliList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StimuliList.SelectedItem is not StimulusItem st)
            return;

        ShowStimulus(st);
    }

    private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_player == null) return;

        if (_player.IsPlaying)
        {
            _player.Pause();
        }
        else
        {
            _player.Play();
            if (_currentStimIsVideo)
                _videoTimeMode = true;

            // сразу синхронизируем фиксации с текущим временем
            if (_currentStimIsVideo && _videoTimeMode)
                UpdateVideoFixations(_player.Time / 1000.0);

        }

        UpdatePlayButton();
    }

    private void AnalysisSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var win = new AnalysisSettingsWindow(_detectSettings, _visualSettings, initialTab: 0)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        if (win.ShowDialog() == true)
        {
            _detectSettings = win.Settings;
            _visualSettings = win.VisualizationSettings;

            // детекция зависит от параметров — сбрасываем кэш
            _fixCache.Clear();
            _cachedBeeSeries = null;
            _stimDurationCache.Clear();
            _rawCache.Clear();
            _vizCache.Clear(); // Clear viz cache to apply new heatmap settings

            ApplyVisualizationSettings();
            AppConfigManager.SaveAnalysisVisualizationSettings(_visualSettings);
            RefreshCurrentFixations();
        }
    }

    private void VisualizationSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var win = new AnalysisSettingsWindow(_detectSettings, _visualSettings, initialTab: 1)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        if (win.ShowDialog() == true)
        {
            _detectSettings = win.Settings;
            _visualSettings = win.VisualizationSettings;

            _fixCache.Clear();
            _cachedBeeSeries = null;
            _stimDurationCache.Clear();
            _rawCache.Clear();
            _vizCache.Clear(); // Clear viz cache to apply new heatmap settings

            ApplyVisualizationSettings();
            AppConfigManager.SaveAnalysisVisualizationSettings(_visualSettings);
            RefreshCurrentFixations();
        }
    }

    private void RefreshCurrentFixations()
    {
        if (StimuliList.SelectedItem is not StimulusItem st)
            return;

        ApplyVisualizationSettings();

        // ShowStimulus() уже выставляет геометрию.
        // Здесь нужно только пересчитать и перерисовать.
        var currentTabMode = _currentVizSettings.Mode;
        _currentStimUid = st.Uid;
        _currentVizSettings = LoadStimulusVisualizationSettings(st.Uid);
        _currentVizSettings.Mode = currentTabMode;
        SyncVisualizationUiFromSettings();
        UpdateVisualizationModeUi();
        BuildFixationSeriesForStim(st.Uid);

        if (_currentStimIsVideo)
        {
            // Видео: если уже в режиме "по времени" — обновляем по текущему тайму
            if (_player != null && _videoTimeMode)
            {
                double cur = _player.Time / 1000.0;
                UpdateVideoFixations(cur);

                // График должен соответствовать тому же окну времени, что и фиксации
                double tMax = cur;
                double tMin = Math.Max(0, cur - FixTimeSliceSec);
                var visibleSeries = BuildFixationSeriesForWindow(st.Uid, (float)tMin, (float)tMax);
                UpdateMetricChart(st.Uid, tMin, tMax, visibleSeries);
            }
            else
            {
                // Видео (превью / не time-mode): по всему времени стимула
                var total = GetStimTotalSecForSelection(st.Uid);
                if (total <= 0) total = 1;

                UpdateVideoVisualizationPreview();
                UpdateMetricChart(st.Uid, 0, total, BuildFixationSeriesForStim(st.Uid));
            }
        }
        else
        {
            // Картинка/цвет
            if (SliceTimelinePanel.Visibility == Visibility.Visible && _sliceStimUid == st.Uid)
            {
                ApplySliceFilter(); // тут перерисовываются фиксации по слайсу
            }
            else
            {
                // Слайс выключен: строим график по всему доступному времени трекера для стимула
                if (TryGetRawTimeRange(st.Uid, out var tMin, out var tMax))
                {
                    var fullSeries = BuildFixationSeriesForStim(st.Uid);
                    ApplyVisualizationForImage(fullSeries);
                    UpdateMetricChart(st.Uid, tMin, tMax, fullSeries);
                }
                else
                {
                    MetricChart.Clear("Нет данных трекера для графика");
                }
            }
        }
    }


    private void TimeSlider_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragging = true;
    }

    private void TimeSlider_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragging = false;

        if (_player == null) return;
        if (_player.Length <= 0) return;

        var sec = TimeSlider.Value;
        var ms = (long)Math.Max(0, sec * 1000.0);
        if (ms > _player.Length) ms = _player.Length;

        _player.Time = ms;
        if (_currentStimIsVideo && _videoTimeMode)
            UpdateVideoFixations(_player.Time / 1000.0);

    }

    // -------- preview logic --------

    private void ShowStimulus(StimulusItem st)
    {
        ApplyVisualizationSettings();

        // === ИСПРАВЛЕНИЕ: Запоминаем текущую вкладку перед загрузкой нового стимула ===
        var currentTabMode = _currentVizSettings.Mode; 
        // ============================================================================

        _currentStimUid = st.Uid;
        _currentVizSettings = LoadStimulusVisualizationSettings(st.Uid);

        // === ИСПРАВЛЕНИЕ: Восстанавливаем вкладку (чтобы не сбрасывалась на "Пути") ===
        _currentVizSettings.Mode = currentTabMode;
        // ============================================================================

        SyncVisualizationUiFromSettings();
        UpdateVisualizationModeUi();

        // Очищаем кэш пчел при смене стимула
        _cachedBeeSeries = null; 

        // сброс фиксаций на обоих оверлеях
        ClearVisualizationOverlays();

        EmptyHint.Visibility = Visibility.Collapsed;

        // сброс виджетов
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;

        ColorPreview.Background = Brushes.Transparent;
        ColorPreview.Visibility = Visibility.Collapsed;

        StopVideoUi();
        ResetTimelineUi();
        DisableTimeline();

        _currentStimIsVideo = false;
        SetPlaybackState(PlaybackState.Stop);
        UpdateKgrChartsForStim(st.Uid);

        if (st.FilePath != null && st.IsImage)
        {
            ShowImage(st.FilePath, st.Scale);
            UpdateVisualizationForCurrentStimulus();
            EnableSliceTimelineUi(st.Uid);

            return;
        }


        if (st.FilePath != null && st.IsVideo)
        {
            _currentStimIsVideo = true;
            _videoTimeMode = false; // ДО Play показываем все фиксации

            _ = StartVideoPreviewAsync(st.FilePath!, st.Scale, st.Uid);
            return;
        }



        if (st.FilePath == null && st.ColorBrush != null)
        {
            ColorPreview.Background = st.ColorBrush;
            ColorPreview.Width = _screenW;
            ColorPreview.Height = _screenH;
            Canvas.SetLeft(ColorPreview, 0);
            Canvas.SetTop(ColorPreview, 0);
            ColorPreview.Visibility = Visibility.Visible;

            UpdateVisualizationForCurrentStimulus();
            EnableSliceTimelineUi(st.Uid);

            TimeText.Text = "— / —";
            return;
        }

        EmptyHint.Text = "Нет файла для предпросмотра";
        EmptyHint.Visibility = Visibility.Visible;
    }

    private void ShowImage(string path, bool scaleToFit)

    {
        var full = Path.GetFullPath(path);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bmp.StreamSource = fs;
            bmp.EndInit();
        }
        bmp.Freeze();
        // offsets/w2/h2 как в translate_result_to_image
        var (offX, offY, w2, h2) = FitRect(_screenW, _screenH, bmp.PixelWidth, bmp.PixelHeight, scaleToFit);

        _stimOffX = offX; _stimOffY = offY; _stimW2 = w2; _stimH2 = h2;
        _stimIsVideo = false;

        // оверлей для картинок = FixOverlay (на Canvas)
        FixOverlay.Width = w2;
        FixOverlay.Height = h2;
        Canvas.SetLeft(FixOverlay, offX);
        Canvas.SetTop(FixOverlay, offY);
        FixOverlay.Visibility = Visibility.Collapsed;

        HeatmapOverlay.Width = w2;
        HeatmapOverlay.Height = h2;
        Canvas.SetLeft(HeatmapOverlay, offX);
        Canvas.SetTop(HeatmapOverlay, offY);

        // а видео-оверлей прячем
        VideoFixOverlay.SetFixations(null);
        VideoFixOverlay.Visibility = Visibility.Collapsed;
        VideoHeatmapOverlay.SetSamples(null);
        VideoHeatmapOverlay.Visibility = Visibility.Collapsed;

        PreviewImage.Source = bmp;
        PreviewImage.Width = w2;
        PreviewImage.Height = h2;
        Canvas.SetLeft(PreviewImage, offX);
        Canvas.SetTop(PreviewImage, offY);

        PreviewImage.Visibility = Visibility.Visible;

        TimeText.Text = "— / —";
    }

    // -------- VLC --------

    private void EnsureVlc()
    {
        if (_vlcInited) return;

        _vlc = new LibVLCSharp.Shared.LibVLC("--no-video-title-show", "--quiet");
        _player = new LibVLCSharp.Shared.MediaPlayer(_vlc) { Volume = 100 };


        PreviewVlc.MediaPlayer = _player;
        _vlcInited = true;
    }
    private async System.Threading.Tasks.Task StartVideoPreviewAsync(string path, bool scaleToFit, string stimUid)
    {
        await StartVideoAsync(path, scaleToFit, startPaused: true);

        // если пользователь успел переключить стимул — ничего не рисуем
        if (!_currentStimIsVideo || _currentStimUid != stimUid) return;

        // на превью-кадре рисуем ВСЕ фиксации
        BuildFixationSeriesForStim(stimUid);
        UpdateVideoVisualizationPreview();
        var total = GetStimTotalSecForSelection(stimUid);
        if (total <= 0) total = 1;
        UpdateMetricChart(stimUid, 0, total, BuildFixationSeriesForStim(stimUid));

        _videoTimeMode = false;
        UpdatePlayButton();
    }

    private async System.Threading.Tasks.Task StartVideoAsync(string path, bool scaleToFit, bool startPaused)
    {
        EnsureVlc();
        StopVideoUi();

        var full = Path.GetFullPath(path);
        _currentMedia = new LibVLCSharp.Shared.Media(_vlc!, new Uri(full));

        // Чтобы Tracks заполнился, медиа часто нужно распарсить/проиграть. :contentReference[oaicite:1]{index=1}
        try
        {
            await _currentMedia.Parse(LibVLCSharp.Shared.MediaParseOptions.ParseLocal);
        }
        catch
        {
            // ок, просто можем не узнать размеры
        }

        uint vw, vh;
        if (!TryGetVideoSizeFromTracks(_currentMedia, out vw, out vh))
        {
            // fallback: считаем “как экран”, offsets тогда будут 0
            vw = (uint)_screenW;
            vh = (uint)_screenH;
        }

        var (offX, offY, w2, h2) = FitRect(_screenW, _screenH, vw, vh, scaleToFit);
        _stimOffX = offX; _stimOffY = offY; _stimW2 = w2; _stimH2 = h2;
        _stimIsVideo = true;

        // для видео оверлей должен быть ВНУТРИ VideoView
        VideoFixOverlay.Width = w2;
        VideoFixOverlay.Height = h2;
        VideoFixOverlay.Visibility = Visibility.Collapsed;

        VideoHeatmapOverlay.Width = w2;
        VideoHeatmapOverlay.Height = h2;
        VideoHeatmapOverlay.Visibility = Visibility.Collapsed;

        // а canvas-оверлей прячем (он всё равно “за видео”)
        FixOverlay.SetFixations(null);
        FixOverlay.Visibility = Visibility.Collapsed;

        HeatmapOverlay.SetSamples(null);
        HeatmapOverlay.Visibility = Visibility.Collapsed;

        PreviewVlc.Width = w2;
        PreviewVlc.Height = h2;
        Canvas.SetLeft(PreviewVlc, offX);
        Canvas.SetTop(PreviewVlc, offY);
        PreviewVlc.Visibility = Visibility.Visible;

        _player!.Play(_currentMedia);

        EnableTimeline();

        if (startPaused)
        {
            await PrimeFirstFrameAndPauseAsync();
            UpdatePlayButton(); // должно стать "▶ Play"
        }
        else
        {
            UpdatePlayButton();
        }

    }



    private void StopVideoUi()
    {
        try { _player?.Stop(); } catch { }
        try
        {
            if (_player != null) _player.Media = null;
        }
        catch { }

        try { _currentMedia?.Dispose(); } catch { }
        _currentMedia = null;

        PreviewVlc.Visibility = Visibility.Collapsed;

        VideoFixOverlay.SetFixations(null);
        VideoFixOverlay.Visibility = Visibility.Collapsed;

        VideoHeatmapOverlay.SetSamples(null);
        VideoHeatmapOverlay.Visibility = Visibility.Collapsed;

    }

    private void DisposeVlc()
    {
        if (!_vlcInited) return;

        try { PreviewVlc.MediaPlayer = null; } catch { }
        try { _player?.Stop(); } catch { }
        try { _player?.Dispose(); } catch { }
        _player = null;

        try { _vlc?.Dispose(); } catch { }
        _vlc = null;

        _vlcInited = false;
    }

    // -------- timeline UI --------

    private void SliceRange_RangeChanged(object? sender, EventArgs e)
    {
        if (_suppressSliceEvents) return;
        if (_sliceStimUid == null) return;

        _sliceMin = SliceRange.StartValue;
        _sliceMax = SliceRange.EndValue;
        if (_playbackState == PlaybackState.Play)
        {
            SetPlaybackState(PlaybackState.Stop);
        }

        if (_currentTime < _sliceMin || _currentTime > _sliceMax)
        {
            _currentTime = _sliceMin;
            UpdateSliceTimeUi();
        }

        ApplySliceFilter();
    }

    private void SliceTimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliceTimeEvents) return;
        if (_isInternalSliceTimeUpdate) return;
        if (_sliceStimUid == null) return;

        if (_playbackState == PlaybackState.Play)
        {
            SetPlaybackState(PlaybackState.Pause);
        }

        _currentTime = SliceTimeSlider.Value;
        UpdateSlicePlaybackVisualization();
    }

    private void DisableTimeline()
    {
        PlayPauseBtn.IsEnabled = false;
        TimeSlider.IsEnabled = false;
        TimeSlider.Minimum = 0;
        TimeSlider.Maximum = 1;
        TimeSlider.Value = 0;
        TimeText.Text = "00:00 / 00:00";
        PlayPauseBtn.Content = "▶ Play";
    }

    private void TickUi()
    {
        if (_player == null) return;
        if (_player.Length <= 0) { UpdatePlayButton(); return; }

        var totalSec = _player.Length / 1000.0;
        var curSec = _player.Time / 1000.0;
        if (_currentStimIsVideo && _videoTimeMode)
        {
            // окно такое же, как для фиксаций
            double tMax = curSec;
            double tMin = Math.Max(0, curSec - FixTimeSliceSec);

            // фиксации
            UpdateVideoFixations(curSec);

            // график
            if (_currentStimUid != null)
            {
                var visibleSeries = BuildFixationSeriesForWindow(_currentStimUid, (float)tMin, (float)tMax);
                UpdateMetricChart(_currentStimUid, tMin, tMax, visibleSeries);
            }
        }


        if (totalSec > 0)
        {
            TimeSlider.Maximum = totalSec;
            if (!_dragging)
                TimeSlider.Value = Math.Max(0, Math.Min(totalSec, curSec));
        }

        TimeText.Text = $"{Fmt(curSec)} / {Fmt(totalSec)}";
        UpdatePlayButton();
    }

    private void UpdatePlayButton()
    {
        if (_player == null)
        {
            PlayPauseBtn.Content = "▶ Play";
            return;
        }

        PlayPauseBtn.Content = _player.IsPlaying ? "⏸ Pause" : "▶ Play";
    }

    private static string Fmt(double sec)
    {
        if (double.IsNaN(sec) || double.IsInfinity(sec) || sec < 0) sec = 0;
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }
    private static (double offX, double offY, double w2, double h2) FitRect(
    double screenW, double screenH,
    double contentW, double contentH,
    bool scaleToFit)
    {
        if (screenW <= 0 || screenH <= 0 || contentW <= 0 || contentH <= 0)
            return (0, 0, screenW, screenH);

        double scale = scaleToFit
            ? Math.Min(screenW / contentW, screenH / contentH)
            : 1.0;

        double w2 = contentW * scale;
        double h2 = contentH * scale;

        double offX = (screenW - w2) / 2.0;
        double offY = (screenH - h2) / 2.0;

        return (offX, offY, w2, h2);
    }
    private string? GetTrackerUid()
    {
        if (_trackerUid != null) return _trackerUid;

        _exp ??= JsonSerializer.Deserialize<ExperimentFile>(
            File.ReadAllText(Path.Combine(_expDir, "exp.json")), _jsonOpts);

        static bool IsEyeTrackerType(string? t)
        {
            if (string.IsNullOrWhiteSpace(t)) return false;
            return t.Equals("PathFinder", StringComparison.OrdinalIgnoreCase)
                || t.Equals("Gazepoint", StringComparison.OrdinalIgnoreCase)
                || t.Contains("tracker", StringComparison.OrdinalIgnoreCase);
        }

        _trackerUid = _exp?.Devices.FirstOrDefault(d => IsEyeTrackerType(d.DevType))?.Uid;
        return _trackerUid;
    }
    private void UpdateVideoFixations(double curSec)
    {
        if (_currentStimUid == null) return;

        // Если выбран режим "Пчелиный рой" (индекс 2)
        if ((int)_currentVizSettings.Mode == 2) 
        {
            // Скрываем старые оверлеи
            VideoFixOverlay.Visibility = Visibility.Collapsed;
            VideoHeatmapOverlay.Visibility = Visibility.Collapsed;

            // Показываем оверлей пчел
            VideoBeeOverlay.Visibility = Visibility.Visible;
            
            // Применяем настройки (радиус и т.д.)
            VideoBeeOverlay.ApplySettings(_visualSettings); 
        // ---------------------------------------------------------------------------------------------

            // === ИСПРАВЛЕНИЕ: Используем кэш вместо пересчета ===
            if (_cachedBeeSeries == null)
                _cachedBeeSeries = BuildBeeSeries(_currentStimUid);
                
            VideoBeeOverlay.SetSeries(_cachedBeeSeries);
            // ====================================================
            VideoBeeOverlay.SetTime(curSec);
            return;
        }

        // --- Старая логика для Heatmap / GazePath ---
        
        // Скрываем пчел, если мы не в этом режиме
        VideoBeeOverlay.Visibility = Visibility.Collapsed;

        float tMax = (float)curSec;
        float tMin = (float)Math.Max(0, curSec - FixTimeSliceSec);

        var visibleSeries = BuildFixationSeriesForWindow(_currentStimUid, tMin, tMax);

        if (_currentVizSettings.Mode == StimulusVisualizationMode.Heatmap)
        {
            VideoFixOverlay.SetFixationSeries(null);
            VideoFixOverlay.Visibility = Visibility.Collapsed;

            // Скрываем bee overlay при переключении на тепловую карту
            VideoBeeOverlay.SetSeries(null);
            VideoBeeOverlay.Visibility = Visibility.Collapsed;

            VideoHeatmapOverlay.ApplySettings(_currentVizSettings.Heatmap);
            var hSeries = BuildHeatmapSeriesForCurrentStim(tMin, tMax);
            VideoHeatmapOverlay.SetHeatmapSeries(hSeries);
            VideoHeatmapOverlay.Visibility = Visibility.Visible;
        }
        else if (_currentVizSettings.Mode == StimulusVisualizationMode.GazePath)
        {
            VideoHeatmapOverlay.SetHeatmapSeries(null);
            VideoHeatmapOverlay.Visibility = Visibility.Collapsed;

            VideoFixOverlay.SetFixationSeries(visibleSeries);
            VideoFixOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            VideoFixOverlay.SetFixationSeries(null);
            VideoFixOverlay.Visibility = Visibility.Collapsed;
            VideoHeatmapOverlay.SetHeatmapSeries(null);
            VideoHeatmapOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private static List<Fixation> SliceByStartTime(IReadOnlyList<Fixation> all, float tMin, float tMax)
    {
        int a = LowerBound(all, tMin);
        int b = UpperBound(all, tMax);

        int n = Math.Max(0, b - a);
        var res = new List<Fixation>(n);
        for (int i = a; i < b; i++) res.Add(all[i]);
        return res;
    }

    private static int LowerBound(IReadOnlyList<Fixation> all, float t)
    {
        int l = 0, r = all.Count;
        while (l < r)
        {
            int m = (l + r) >> 1;
            if (all[m].StartSec < t) l = m + 1;
            else r = m;
        }
        return l;
    }

    private static int UpperBound(IReadOnlyList<Fixation> all, float t)
    {
        int l = 0, r = all.Count;
        while (l < r)
        {
            int m = (l + r) >> 1;
            if (all[m].StartSec <= t) l = m + 1;
            else r = m;
        }
        return l;
    }

    private void ApplyVisualizationSettings()
    {
        ApplyVisualizationSettings(FixOverlay);
        ApplyVisualizationSettings(VideoFixOverlay);
    }

    private void ApplyVisualizationSettings(FixationOverlay overlay)
    {
        overlay.ApplySettings(_visualSettings);
    }

    private void ClearVisualizationOverlays()
    {
        FixOverlay.SetFixationSeries(null); // Было SetFixations
        FixOverlay.SetPlaybackTime(null, 0, false);
        FixOverlay.Visibility = Visibility.Collapsed;
        
        HeatmapOverlay.SetHeatmapSeries(null); // Было SetSamples
        HeatmapOverlay.Visibility = Visibility.Collapsed;

        VideoFixOverlay.SetFixationSeries(null); // Было SetFixations
        VideoFixOverlay.Visibility = Visibility.Collapsed;
        
        VideoHeatmapOverlay.SetHeatmapSeries(null); // Было SetSamples
        VideoHeatmapOverlay.Visibility = Visibility.Collapsed;

        BeeOverlay.SetSeries(null);
        BeeOverlay.Visibility = Visibility.Collapsed;
        
        VideoBeeOverlay.SetSeries(null);
        VideoBeeOverlay.Visibility = Visibility.Collapsed;
    }

    private void UpdateVisualizationModeUi()
    {
        // Heatmap settings panel removed - moved to visualization settings window
    }

    private void SyncVisualizationUiFromSettings()
    {
        _suppressVizUi = true;
        try
        {
            _currentVizSettings.Normalize();
            VisualizationModeTabs.SelectedIndex = (int)_currentVizSettings.Mode;
        }
        finally
        {
            _suppressVizUi = false;
        }
    }

    private void UpdateVisualizationForCurrentStimulus()
    {
        if (_currentStimUid == null) return;

        BuildFixationSeriesForStim(_currentStimUid);
        // === ИСПРАВЛЕНИЕ: Загружаем данные для пчел один раз в кэш ===
        if ((int)_currentVizSettings.Mode == 2 && _cachedBeeSeries == null)
        {
            _cachedBeeSeries = BuildBeeSeries(_currentStimUid);
        }
        // ============================================================

        if (_currentStimIsVideo)
        {
            if (_player != null && _videoTimeMode)
            {
                UpdateVideoFixations(_player.Time / 1000.0);
            }
            else
            {
                UpdateVideoVisualizationPreview();
            }
        }
        else
        {
            if (SliceTimelinePanel.Visibility == Visibility.Visible && _sliceStimUid == _currentStimUid)
            {
                ApplySliceFilter();
            }
            else
            {
                ApplyVisualizationForImage(BuildFixationSeriesForStim(_currentStimUid));
            }
        }
    }

    private void ApplyVisualizationForImage(IReadOnlyList<FixationSeries>? fixations)
    {
        FixOverlay.SetPlaybackTime(null, 0, false);

        // Сбрасываем BeeOverlay по умолчанию
        BeeOverlay.SetSeries(null);
        BeeOverlay.Visibility = Visibility.Collapsed;

        // Расчет времени
        float tStart = 0;
        float tEnd = 0;
        if (fixations != null && fixations.Count > 0)
        {
            bool hasRange = false;
            foreach (var ser in fixations)
            {
                if (ser.Fixations.Count == 0) continue;
                float sStart = (float)ser.Fixations[0].StartSec;
                float sEnd = (float)(ser.Fixations[^1].StartSec + ser.Fixations[^1].DurSec);
                if (!hasRange) { tStart = sStart; tEnd = sEnd; hasRange = true; }
                else { tStart = Math.Min(tStart, sStart); tEnd = Math.Max(tEnd, sEnd); }
            }
        }

        // Режим: Пчелиный рой
        if ((int)_currentVizSettings.Mode == 2)
        {
            // Скрываем другие
            FixOverlay.Visibility = Visibility.Collapsed;
            HeatmapOverlay.Visibility = Visibility.Collapsed;

           // --- ИСПРАВЛЕНИЕ: Передаем _visualSettings ---
            BeeOverlay.ApplySettings(_visualSettings);
        // ---------------------------------------------
            
            // Строим данные и показываем для текущего момента времени
            if (_currentStimUid != null)
            {
                    // === ИСПРАВЛЕНИЕ: Используем кэш ===
                if (_cachedBeeSeries == null)
                    _cachedBeeSeries = BuildBeeSeries(_currentStimUid);
                    
                BeeOverlay.SetSeries(_cachedBeeSeries);
                BeeOverlay.SetTime(_currentTime);
            }
            
            BeeOverlay.Visibility = Visibility.Visible;
        }
        // Режим: Тепловая карта
        else if (_currentVizSettings.Mode == StimulusVisualizationMode.Heatmap)
        {
            FixOverlay.SetFixationSeries(null);
            FixOverlay.Visibility = Visibility.Collapsed;

            // Скрываем bee overlay при переключении на тепловую карту
            BeeOverlay.SetSeries(null);
            BeeOverlay.Visibility = Visibility.Collapsed;

            HeatmapOverlay.ApplySettings(_currentVizSettings.Heatmap);
            var hSeries = BuildHeatmapSeriesForCurrentStim(tStart, tEnd);
            HeatmapOverlay.SetHeatmapSeries(hSeries);
            HeatmapOverlay.Visibility = hSeries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        // Режим: Путь взгляда
        else if (_currentVizSettings.Mode == StimulusVisualizationMode.GazePath)
        {
            HeatmapOverlay.SetHeatmapSeries(null);
            HeatmapOverlay.Visibility = Visibility.Collapsed;

            // Скрываем bee overlay при переключении на путь взгляда
            BeeOverlay.SetSeries(null);
            BeeOverlay.Visibility = Visibility.Collapsed;

            FixOverlay.SetFixationSeries(fixations);
            FixOverlay.Visibility = (fixations != null && fixations.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            FixOverlay.SetFixationSeries(null);
            FixOverlay.Visibility = Visibility.Collapsed;
            HeatmapOverlay.SetHeatmapSeries(null);
            HeatmapOverlay.Visibility = Visibility.Collapsed;
            // Скрываем bee overlay при переключении на другие режимы
            BeeOverlay.SetSeries(null);
            BeeOverlay.Visibility = Visibility.Collapsed;
        }
    }
    // В файле AnalysisWindow.cs

    private List<HeatmapSeries> BuildHeatmapSeries(string stimUid, float tMin, float tMax)
    {
        var series = new List<HeatmapSeries>();

        foreach (var result in EnumerateVisibleResults())
        {
            var samples = BuildHeatmapSamples(result.ResultUid, stimUid, tMin, tMax);
            if (samples.Count > 0)
                series.Add(new HeatmapSeries(samples, result.Color));
        }

        return series;
    }

     private List<HeatmapSample> BuildHeatmapSamples(string resultUid, string stimUid, float tMin, float tMax)
    {
        var samples = new List<HeatmapSample>();

        // Нужны размеры экрана и геометрия стимула
        if (_screenW <= 0 || _screenH <= 0) return samples;

        // 1) Берём СЫРЫЕ точки взгляда (у тебя они уже санитизируются в ReadRawSamplesForStim)
        var raw = GetRawSamplesForStim(resultUid, stimUid);
        if (raw.Count == 0) return samples;

        // 2) Препроцесс (шумодав/заполнение дыр/т.п.) — чтобы не тащить мусор
        var pre = AnalysisFixationPipeline.Preprocess(raw, _detectSettings);

        // Параметры перевода из координат экрана -> в координаты области стимула (как в BuildBeeSeries)
        float ox = (float)_stimOffX;
        float oy = (float)_stimOffY;
        float maxX = (float)_stimW2;
        float maxY = (float)_stimH2;

        // Даём “зерно” как на 2-м скрине, но не убиваем производительность:
        // берём примерно 50 точек/сек (можешь поставить 0.01f для 100 Гц, если надо плотнее)
        const float sampleStep = 0.02f;
        float nextT = tMin;

        for (int i = 0; i < pre.Count; i++)
        {
            var r = pre[i];
            if (!r.Valid) continue;

            float ts = r.TimeSec;
            if (ts < tMin || ts > tMax) continue;

            // даунсэмплинг по времени (чтобы не было сотен тысяч точек)
            if (ts < nextT) continue;
            nextT = ts + sampleStep;

            // 0..1 -> пиксели экрана
            float x = r.Xn * _screenW;
            float y = r.Yn * _screenH;

            // пиксели экрана -> пиксели внутри стимула
            x -= ox;
            y -= oy;

            // отсекаем чёрные поля/вне стимула
            if (x < 0 || y < 0 || x > maxX || y > maxY) continue;

            samples.Add(new HeatmapSample(x, y));
        }

        return samples;
    }


    private void UpdateVideoVisualizationPreview()
    {
        if (_currentStimUid == null) return;

        // Режим "Пчелиный рой"
        if ((int)_currentVizSettings.Mode == 2)
        {
            VideoFixOverlay.Visibility = Visibility.Collapsed;
            VideoHeatmapOverlay.Visibility = Visibility.Collapsed;

             VideoBeeOverlay.ApplySettings(_visualSettings);
            // ---------------------------------------------
            
            var beeData = BuildBeeSeries(_currentStimUid);
            VideoBeeOverlay.SetSeries(beeData);
            VideoBeeOverlay.SetTime(0); 
            VideoBeeOverlay.Visibility = Visibility.Visible;
            return;
        }

        VideoBeeOverlay.Visibility = Visibility.Collapsed;

        // --- Старая логика ---
        var allSeries = BuildFixationSeriesForStim(_currentStimUid);
        float tFull = (float)GetStimTotalSecForSelection(_currentStimUid);
        if (tFull <= 0) tFull = 10000f;

        if (_currentVizSettings.Mode == StimulusVisualizationMode.Heatmap)
        {
            VideoFixOverlay.SetFixationSeries(null);
            VideoFixOverlay.Visibility = Visibility.Collapsed;

            // Скрываем bee overlay при переключении на тепловую карту
            VideoBeeOverlay.SetSeries(null);
            VideoBeeOverlay.Visibility = Visibility.Collapsed;

            VideoHeatmapOverlay.ApplySettings(_currentVizSettings.Heatmap);
            var hSeries = BuildHeatmapSeriesForCurrentStim(0, tFull);
            VideoHeatmapOverlay.SetHeatmapSeries(hSeries);
            VideoHeatmapOverlay.Visibility = hSeries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (_currentVizSettings.Mode == StimulusVisualizationMode.GazePath)
        {
            VideoHeatmapOverlay.SetHeatmapSeries(null);
            VideoHeatmapOverlay.Visibility = Visibility.Collapsed;

            // Скрываем bee overlay при переключении на путь взгляда
            VideoBeeOverlay.SetSeries(null);
            VideoBeeOverlay.Visibility = Visibility.Collapsed;

            VideoFixOverlay.SetFixationSeries(allSeries);
            VideoFixOverlay.Visibility = allSeries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            VideoFixOverlay.SetFixationSeries(null);
            VideoFixOverlay.Visibility = Visibility.Collapsed;
            VideoHeatmapOverlay.SetHeatmapSeries(null);
            VideoHeatmapOverlay.Visibility = Visibility.Collapsed;
            // Скрываем bee overlay при переключении на другие режимы
            VideoBeeOverlay.SetSeries(null);
            VideoBeeOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void VisualizationModeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVizUi) return;
        if (_currentStimUid == null) return;

        if (VisualizationModeTabs.SelectedIndex < 0) return;
        _currentVizSettings.Mode = (StimulusVisualizationMode)VisualizationModeTabs.SelectedIndex;
        _currentVizSettings.Normalize();
        SaveCurrentVisualizationSettings();
        UpdateVisualizationModeUi();
        UpdateVisualizationForCurrentStimulus();
        UpdateAoiMode(VisualizationModeTabs.SelectedIndex);
    }

    private StimulusVizSettings LoadStimulusVisualizationSettings(string stimUid)
    {
        if (_vizCache.TryGetValue(stimUid, out var cached))
            return cached.Clone();

        var settings = new StimulusVizSettings();
        if (string.IsNullOrWhiteSpace(_primaryResultUid)) return settings;

        var path = Path.Combine(_expDir, "results", _primaryResultUid, stimUid, "viz.json");
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<StimulusVizSettings>(File.ReadAllText(path), _jsonOpts);
                if (loaded != null)
                {
                    settings = loaded;
                    // Heatmap settings are now global - override with current visual settings
                    settings.Heatmap = CreateHeatmapSettingsFromVisualizationSettings();
                }
            }
            else
            {
                // No saved file - use global heatmap settings
                settings.Heatmap = CreateHeatmapSettingsFromVisualizationSettings();
            }
        }
        catch
        {
            settings = new StimulusVizSettings();
            settings.Heatmap = CreateHeatmapSettingsFromVisualizationSettings();
        }

        settings.Normalize();
        _vizCache[stimUid] = settings.Clone();
        return settings.Clone();
    }

    private StimulusHeatmapSettings CreateHeatmapSettingsFromVisualizationSettings()
    {
        return new StimulusHeatmapSettings
        {
            Function = _visualSettings.HeatmapFunction,
            Radius = _visualSettings.HeatmapRadius,
            InitialOpacity = _visualSettings.HeatmapInitialOpacity,
            Threshold = _visualSettings.HeatmapThreshold,
            MapType = _visualSettings.HeatmapMapType
        };
    }

    private void SaveCurrentVisualizationSettings()
    {
        if (_currentStimUid == null) return;

        _currentVizSettings.Normalize();
        _vizCache[_currentStimUid] = _currentVizSettings.Clone();

        try
        {
            if (string.IsNullOrWhiteSpace(_primaryResultUid)) return;

            var dir = Path.Combine(_expDir, "results", _primaryResultUid, _currentStimUid);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "viz.json");
            var json = JsonSerializer.Serialize(_currentVizSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // ignore file errors
        }
    }



    private string _currentAoiColorHex = "#FFFF0000"; // Красный по умолчанию
    // -------- helpers (как в MainViewModel) --------
    // Вызывается при переключении вкладки "Области интереса"
    private void UpdateAoiMode(int tabIndex)
    {
        bool isAoi = (tabIndex == 3);
        _isAoiMode = isAoi;

        // 1. Скрываем/показываем Оверлей
        if (AoiOverlay != null)
        {
            AoiOverlay.Visibility = isAoi ? Visibility.Visible : Visibility.Collapsed;
            AoiOverlay.IsHitTestVisible = isAoi;
        }

        // 2. Скрываем Пчелиный рой и другие визуализации
        if (isAoi)
        {
            // В режиме AOI скрываем всё лишнее
            if(BeeOverlay != null) BeeOverlay.Visibility = Visibility.Collapsed;
            if(FixOverlay != null) FixOverlay.Visibility = Visibility.Collapsed;
            if(HeatmapOverlay != null) HeatmapOverlay.Visibility = Visibility.Collapsed;
            if(VideoBeeOverlay != null) VideoBeeOverlay.Visibility = Visibility.Collapsed;
            if(VideoFixOverlay != null) VideoFixOverlay.Visibility = Visibility.Collapsed;
            if(VideoHeatmapOverlay != null) VideoHeatmapOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            // ВЫХОД из режима AOI: восстанавливаем визуализацию текущего режима
            UpdateVisualizationForCurrentStimulus();
        }

        // 3. График только на вкладке с фиксациями (индекс 0)
        bool showMetricChart = (tabIndex == 0);
        if (MetricChart != null) MetricChart.Visibility = showMetricChart ? Visibility.Visible : Visibility.Collapsed;
        if (AoiResultsGrid != null) AoiResultsGrid.Visibility = isAoi ? Visibility.Visible : Visibility.Collapsed;

        // 4. Панель инструментов AOI
        if (AoiToolbar != null) AoiToolbar.Visibility = isAoi ? Visibility.Visible : Visibility.Collapsed;

        // 5. Кнопка экспорта (видна только в AOI)
        if (ExportAoiBtn != null) ExportAoiBtn.Visibility = isAoi ? Visibility.Visible : Visibility.Collapsed;

        // 6. Управление кнопками Плей/Пауза и Настройки (скрываем ТОЛЬКО в AOI)
        Visibility controlsVisibility = isAoi ? Visibility.Collapsed : Visibility.Visible;

        // Для кнопок Видео (Play/Pause) - они лежат внутри VideoTimelinePanel.
        // Но вы просили скрыть именно кнопки, оставив слайдер.
        // Слайдер (TimeSlider) мы не трогаем, а вот кнопку PlayPauseBtn скрываем.
        if (PlayPauseBtn != null) PlayPauseBtn.Visibility = controlsVisibility;

        // Для кнопок Slice (картинки)
        if (SlicePlayPauseBtn != null) SlicePlayPauseBtn.Visibility = controlsVisibility;
        if (SliceStopBtn != null) SliceStopBtn.Visibility = controlsVisibility;

        if (SliceTimeSlider != null) SliceTimeSlider.Visibility = controlsVisibility;

        if (SliceTimeText != null) SliceTimeText.Visibility = controlsVisibility;
        // Кнопка настроек отображения
        if (VisualizationSettingsBtn != null) VisualizationSettingsBtn.Visibility = controlsVisibility;

        // Загрузка данных
        if (isAoi && _currentStimUid != null)
        {
            LoadAoisForStimulus(_currentStimUid);
            UpdateAoiColorBtnPreview(); // Обновить цвет кнопки при входе
        }
    }
    private void AoiColorBtn_Click(object sender, RoutedEventArgs e)
    {
        using (var dialog = new WinForms.ColorDialog())
        {
            // Пытаемся распарсить текущий цвет для начального выбора
            try
            {
                var c = (Color)System.Windows.Media.ColorConverter.ConvertFromString(_currentAoiColorHex);
                dialog.Color = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
            }
            catch { }

            dialog.FullOpen = true; // Сразу показать расширенную палитру

            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                var c = dialog.Color;
                // Конвертируем в Hex строку #AARRGGBB
                _currentAoiColorHex = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
                
                UpdateAoiColorBtnPreview();
            }
        }
    }

    // Обновление фона кнопки
    private void UpdateAoiColorBtnPreview()
    {
        if (AoiColorBtn == null) return;
        try
        {
            var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(_currentAoiColorHex);
            var brush = new SolidColorBrush(color);
            AoiColorBtn.Background = brush;
        }
        catch { }
    }

    // Метод получения цвета для создания новых AOI (используется в AoiOverlay_MouseDown)
    private string GetSelectedAoiColor()
    {
        return _currentAoiColorHex;
    }
    // Чтение настроек из ComboBox

    private double GetSelectedAoiThick()
    {
        if (AoiThickCombo.SelectedItem is ComboBoxItem item && item.Tag is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            return v;
        return 2.0;
    }

    // Обработчики изменений в ComboBox
    private void AoiShapeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AoiShapeCombo.SelectedItem is ComboBoxItem item && item.Tag is string typeStr)
        {
            if (Enum.TryParse<AoiType>(typeStr, out var t))
                _currentAoiType = t;
        }
    }

    private void AoiStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Если нужно менять стиль уже выбранного AOI, можно добавить логику здесь.
        // Пока просто сохраняет для НОВЫХ фигур.
    }

    // Обновление таблицы
    private void RefreshAoiGrid()
    {
        if (AoiResultsGrid == null) return;

        var displayList = new List<AoiDisplayItem>();
        
        // Сопоставляем AOI и метрики
        for (int i = 0; i < _aoiList.Count; i++)
        {
            var aoi = _aoiList[i];
            // Ищем метрики по имени (или по индексу, если порядок совпадает)
            var metrics = (_cachedAoiMetrics != null && i < _cachedAoiMetrics.Length) 
                ? _cachedAoiMetrics[i] 
                : null;

            displayList.Add(new AoiDisplayItem(aoi, metrics));
        }

        AoiResultsGrid.ItemsSource = displayList;
    }

    // Удаление строки из таблицы
    private void DeleteAoiRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is AoiDisplayItem item)
        {
            _aoiList.Remove(item.SourceAoi);
            FinishAoiDrawing(); // Пересчитать и обновить
        }
    }
    // Загрузка/Инициализация списка AOI
    private void LoadAoisForStimulus(string stimUid)
    {
        _aoiList.Clear();
        try
        {
            if (!string.IsNullOrWhiteSpace(_primaryResultUid))
            {
                var path = Path.Combine(_expDir, "results", _primaryResultUid, stimUid, "aoi.json");
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    _aoiList = JsonSerializer.Deserialize<List<AoiElement>>(json, _jsonOpts) ?? new();
                }
            }
        }
        catch { }

        RecalculateAoiMetrics();
        AoiOverlay.SetData(_aoiList, _cachedAoiMetrics);
        
        // ВАЖНО: Обновляем таблицу, чтобы она не была пустой!
        RefreshAoiGrid(); 
    }

    private void SaveAoisForStimulus()
    {
        if (_currentStimUid == null || string.IsNullOrWhiteSpace(_primaryResultUid)) return;
        
        try
        {
            var dir = Path.Combine(_expDir, "results", _primaryResultUid, _currentStimUid);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "aoi.json");
            var json = JsonSerializer.Serialize(_aoiList, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { MessageBox.Show("Ошибка сохранения AOI"); }
    }

    // --- ОБРАБОТКА МЫШИ (РИСОВАНИЕ) ---

    private void AoiOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isAoiMode) return;
        
        // Координаты относительно оверлея
        var pos = e.GetPosition(AoiOverlay);
        double w = AoiOverlay.ActualWidth;
        double h = AoiOverlay.ActualHeight;
        
        // Нормализация 0..1
        Point np = new Point(pos.X / w, pos.Y / h);
         // Обработка ДВОЙНОГО КЛИКА для завершения полигона
        if (_currentDraftAoi != null && e.ClickCount == 2)
        {
            if (_currentAoiType == AoiType.Polygon)
            {
                FinishAoiDrawing();
                e.Handled = true;
                return;
            }
        }

        if (_currentDraftAoi == null)
        {
            // НАЧАЛО РИСОВАНИЯ
            _currentDraftAoi = new AoiElement 
            { 
                Type = _currentAoiType,
                Name = $"AOI {_aoiList.Count + 1}",
                ColorHex = GetSelectedAoiColor(), // <-- Читаем из ComboBox
                LineWidth = GetSelectedAoiThick() // <-- Читаем из ComboBox
            };
            
            _currentDraftAoi.NormalizedPoints.Add(np);
            
            // Для прямоугольника/эллипса сразу добавляем вторую точку (конец), которую будем двигать
            if (_currentAoiType == AoiType.Rectangle || _currentAoiType == AoiType.Ellipse)
            {
                _currentDraftAoi.NormalizedPoints.Add(np);
            }
            
            AoiOverlay.GhostAoi = _currentDraftAoi;
        }
        else
        {
            // Добавляем точку в полигон
            if (_currentAoiType == AoiType.Polygon)
            {
                _currentDraftAoi.NormalizedPoints.Add(np);
            }
        }
        AoiOverlay.InvalidateVisual();
    }

    private void AoiOverlay_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isAoiMode || _currentDraftAoi == null) return;

        var pos = e.GetPosition(AoiOverlay);
        double w = AoiOverlay.ActualWidth;
        double h = AoiOverlay.ActualHeight;
        Point np = new Point(Math.Clamp(pos.X / w, 0, 1), Math.Clamp(pos.Y / h, 0, 1));

        if (_currentAoiType == AoiType.Rectangle || _currentAoiType == AoiType.Ellipse)
        {
            // Обновляем вторую точку (BottomRight)
            if (_currentDraftAoi.NormalizedPoints.Count > 1)
                _currentDraftAoi.NormalizedPoints[1] = np;
        }
        // Для полигона можно рисовать "резиновую нить" до курсора, но пока оставим как есть
        
        AoiOverlay.InvalidateVisual();
    }

    private void AoiOverlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isAoiMode || _currentDraftAoi == null) return;

        // Для Rectangle/Ellipse отпускание мыши завершает фигуру
        if (_currentAoiType == AoiType.Rectangle || _currentAoiType == AoiType.Ellipse)
        {
            var p1 = _currentDraftAoi.NormalizedPoints[0];
            var p2 = _currentDraftAoi.NormalizedPoints[1];
            
            // Защита от микро-кликов (площадь 0)
            if (Math.Abs(p1.X - p2.X) > 0.001 && Math.Abs(p1.Y - p2.Y) > 0.001)
            {
                FinishAoiDrawing();
            }
            else
            {
                // Отмена
                _currentDraftAoi = null;
                AoiOverlay.GhostAoi = null;
                AoiOverlay.InvalidateVisual();
            }
        }
    }

    private void FinishAoiDrawing()
    {
        // (Если был драфт, добавляем в список)
        if (_currentDraftAoi != null)
        {
            _aoiList.Add(_currentDraftAoi);
            _currentDraftAoi = null;
            AoiOverlay.GhostAoi = null;
        }
        
        SaveAoisForStimulus();
        RecalculateAoiMetrics();
        AoiOverlay.SetData(_aoiList, _cachedAoiMetrics);
        
        // ВАЖНО: Обновляем таблицу
        RefreshAoiGrid(); 
    }

    private string GetNextAoiColor()
    {
        string[] colors = { "#FFFF0000", "#FF00FF00", "#FF0000FF", "#FFFFFF00", "#FF00FFFF", "#FFFFA500" };
        return colors[_aoiList.Count % colors.Length];
    }

    // --- РАСЧЕТ АНАЛИТИКИ (ВИЗИТЫ) ---

    private void RecalculateAoiMetrics()
    {
        if (_currentStimUid == null) return;
        
        var fixSeriesList = BuildFixationSeriesForStim(_currentStimUid);
        var metricsList = new List<AoiMetricsResult>();

        // Для градусной амплитуды нужны экранные координаты (px) для тех же фиксаций,
        // которые попали в координаты стимула (после отсечения чёрных полей).
        var visibleResults = EnumerateVisibleResults().ToList();
        var pairedFix = new List<(string ResultUid, List<Fixation> Screen, List<Fixation> Stim)>(visibleResults.Count);
        foreach (var result in visibleResults)
        {
            if (string.IsNullOrWhiteSpace(result.ResultUid)) continue;
            var (screen, stim) = EnsureFixationsForStimPaired(result.ResultUid, _currentStimUid);
            pairedFix.Add((result.ResultUid, screen, stim));
        }

        double w = _stimW2; 
        double h = _stimH2;
        double totalStimulusArea = w * h;

        foreach (var aoi in _aoiList)
        {
            var m = new AoiMetricsResult { AoiName = aoi.Name };

            // Площадь
            double aoiArea = AoiGeometry.CalculateAreaPx(aoi, w, h);
            if (totalStimulusArea > 0)
                m.AreaRatio = aoiArea / totalStimulusArea;

            // Переменные для сбора статистики
            int totalFix = 0;
            double totalDur = 0;
            
            double firstFixStartTime = -1;
            double firstFixDuration = 0;
            int fixationsBefore = 0;
            
            int visits = 0;
            bool wasIn = false;
            
            int saccadeCount = 0;
            double totalSaccadeAmp = 0;
            double totalSaccadeAmpDeg = 0;
            double scanpath = 0;

            // Проходим по всем сериям (испытуемым)
            foreach (var series in fixSeriesList)
            {
                wasIn = false;
                Fixation? prevFix = null;
                bool foundFirstInUser = false;
                int userFixCounter = 0;

                foreach (var fix in series.Fixations)
                {
                    Point pt = new Point(fix.Xpx, fix.Ypx);
                    bool inAoi = AoiGeometry.IsPointInAoi(pt, aoi, w, h);

                    // Подсчет "до первой фиксации"
                    if (!foundFirstInUser)
                    {
                        if (inAoi)
                        {
                            foundFirstInUser = true;
                            // Если это первая глобальная или мы усредняем? 
                            // Для одного испытуемого:
                            if (firstFixStartTime == -1 || fix.StartSec < firstFixStartTime)
                            {
                                firstFixStartTime = fix.StartSec;
                                firstFixDuration = fix.DurSec;
                                fixationsBefore = userFixCounter; // Сохраняем сколько было ДО
                            }
                        }
                        else
                        {
                            userFixCounter++;
                        }
                    }

                    // Основные метрики
                    if (inAoi)
                    {
                        totalFix++;
                        totalDur += fix.DurSec;
                        
                        if (!wasIn)
                        {
                            visits++;
                            wasIn = true;
                        }

                        // Саккады и путь (считаем входящие и внутренние)
                        if (prevFix != null)
                        {
                            double dx = fix.Xpx - prevFix.Value.Xpx;
                            double dy = fix.Ypx - prevFix.Value.Ypx;
                            double dist = Math.Sqrt(dx*dx + dy*dy);
                            
                            scanpath += dist;
                            totalSaccadeAmp += dist;
                            saccadeCount++;
                        }
                    }
                    else
                    {
                        wasIn = false;
                    }
                    
                    prevFix = fix;
                }
            }
            
            m.FixationCount = totalFix;
            m.TotalDwellTime = totalDur;
            m.RevisitCount = Math.Max(0, visits - 1);
            
            // Время до первой фиксации
            m.TimeToFirstFixation = firstFixStartTime;
            m.TimeBeforeFirstFixation = firstFixStartTime; // Дублирует TTFF по смыслу, но для таблицы
            
            m.FixationsBeforeFirst = (firstFixStartTime != -1) ? fixationsBefore : 0;
            m.FirstFixationDuration = (firstFixStartTime != -1) ? firstFixDuration : 0;

            if (totalFix > 0) 
                m.AverageFixationDuration = totalDur / totalFix;
                
            m.SaccadeCount = saccadeCount;
            if (saccadeCount > 0)
                m.AverageSaccadeAmplitude = totalSaccadeAmp / saccadeCount;

            // Вычисляем амплитуду саккад в градусах
            if (saccadeCount > 0)
            {
                totalSaccadeAmpDeg = CalculateSaccadeAmplitudeInDegreesPaired(pairedFix, aoi, w, h);
                m.AverageSaccadeAmplitudeDeg = totalSaccadeAmpDeg / saccadeCount;
            }
                
            m.ScanpathLength = scanpath;
            
            metricsList.Add(m);
        }
        
        _cachedAoiMetrics = metricsList.ToArray();
    }

    private double CalculateSaccadeAmplitudeInDegreesPaired(
        List<(string ResultUid, List<Fixation> Screen, List<Fixation> Stim)> pairedFix,
        AoiElement aoi, double w, double h)
    {
        if (_currentStimUid == null) return 0;

        double totalAmpDeg = 0;

        foreach (var (resultUid, fixScreen, fixStim) in pairedFix)
        {
            if (string.IsNullOrWhiteSpace(resultUid))
                continue;

            if (fixScreen.Count == 0 || fixStim.Count == 0) continue;
            if (fixScreen.Count != fixStim.Count) continue;

            var (wPx, hPx, wMm, hMm) = ReadStimulusScreenInfo(resultUid, _currentStimUid);
            if (wPx <= 0 || hPx <= 0 || wMm <= 0 || hMm <= 0) continue;

            var raw = GetRawSamplesForStim(resultUid, _currentStimUid);
            if (raw.Count == 0) continue;

            float mmPerPxX = wMm / (float)wPx;
            float mmPerPxY = hMm / (float)hPx;

            Fixation? prevScreen = null;
            Fixation? prevStim = null;

            for (int k = 0; k < fixStim.Count; k++)
            {
                var stimFix = fixStim[k];
                var screenFix = fixScreen[k];

                Point pt = new Point(stimFix.Xpx, stimFix.Ypx);
                bool inAoi = AoiGeometry.IsPointInAoi(pt, aoi, w, h);

                if (inAoi && prevStim != null && prevScreen != null)
                {
                    // dx/dy берём в экранных пикселях, чтобы mmPerPx соответствовал размеру экрана.
                    double dx = screenFix.Xpx - prevScreen.Value.Xpx;
                    double dy = screenFix.Ypx - prevScreen.Value.Ypx;

                    double dxMm = dx * mmPerPxX;
                    double dyMm = dy * mmPerPxY;
                    double dMm = Math.Sqrt(dxMm * dxMm + dyMm * dyMm);

                    float distM = GetDistanceForFixation(raw, stimFix);
                    if (distM > 0 && float.IsFinite(distM))
                    {
                        float distMm = distM * 1000f;
                        double angRad = Math.Atan2(dMm, distMm);
                        double angDeg = angRad * (180.0 / Math.PI);
                        if (!double.IsFinite(angDeg)) angDeg = 0;
                        totalAmpDeg += angDeg;
                    }
                }

                prevStim = stimFix;
                prevScreen = screenFix;
            }
        }

        return totalAmpDeg;
    }

    private float GetDistanceForFixation(List<RawGazeSample> raw, Fixation fix)
    {
        float t0 = fix.StartSec;
        float t1 = fix.StartSec + fix.DurSec;

        double sum = 0;
        int cnt = 0;

        foreach (var s in raw)
        {
            if (!s.Valid) continue;
            if (!float.IsFinite(s.TimeSec) || !float.IsFinite(s.DistanceM)) continue;
            if (s.TimeSec < t0 || s.TimeSec > t1) continue;
            if (s.DistanceM <= 0) continue;
            sum += s.DistanceM;
            cnt++;
        }

        if (cnt == 0) return 0;
        return (float)(sum / cnt);
    }

    // --- ЭКСПОРТ CSV ---

    private void ExportAoiCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_cachedAoiMetrics.Length == 0) return;

        var sfd = new Microsoft.Win32.SaveFileDialog { 
            Filter = "CSV|*.csv", 
            FileName = $"{_currentStimUid}_aoi_stats.csv" 
        };
        
        if (sfd.ShowDialog() == true)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("AOI Name;Fixation Count;Dwell Time (s);Time to First Fixation (s);Avg Fix Duration (s);Revisits;Fixations Before First;First Fixation Duration (s);Saccade Count;Avg Saccade Amplitude (px);Avg Saccade Amplitude (deg);Scanpath Length (px);Area Ratio (%)");
            
            foreach (var m in _cachedAoiMetrics)
            {
                sb.AppendLine($"{m.AoiName};{m.FixationCount};{m.TotalDwellTime:F3};{m.TimeToFirstFixation:F3};{m.AverageFixationDuration:F3};{m.RevisitCount};{m.FixationsBeforeFirst};{m.FirstFixationDuration:F3};{m.SaccadeCount};{m.AverageSaccadeAmplitude:F2};{m.AverageSaccadeAmplitudeDeg:F2};{m.ScanpathLength:F2};{(m.AreaRatio * 100):F2}");
            }
            
            File.WriteAllText(sfd.FileName, sb.ToString(), System.Text.Encoding.UTF8);
            MessageBox.Show("Экспорт завершен");
        }
    }
    private void UpdateMetricChart(string stimUid, double tMin, double tMax, IReadOnlyList<FixationSeries>? visibleFixSeries = null)
    {
        if (tMax <= tMin) tMax = tMin + 1e-6;

        // bands = фиксации как интервалы (start..end)
        List<(double StartSec, double EndSec)>? bands = null;
        if (visibleFixSeries != null && visibleFixSeries.Count == 1)
        {
            var fixations = visibleFixSeries[0].Fixations;
            if (fixations.Count > 0)
            {
                bands = new List<(double, double)>(fixations.Count);
                foreach (var f in fixations)
                    bands.Add((f.StartSec, f.StartSec + f.DurSec));
            }
        }

        if (_detectSettings.Algorithm == FixationAlgorithm.Ivt)
        {
            var series = BuildMetricSeries(stimUid, tMin, tMax, useSpeed: true);
            if (series.Count == 0)
            {
                MetricChart.Clear("Нет валидных точек для скорости");
                return;
            }

            double thr = _detectSettings.IvtSpeedFixDegPerSec;
            double maxObserved = series.Max(s => s.Points.Max(p => p.Value));
            double yMax = Math.Max(thr * 2.0, maxObserved * 1.1);

            if (_detectSettings.MaxSpeedDegPerSec > 0)
                yMax = Math.Min(yMax, _detectSettings.MaxSpeedDegPerSec);

            MetricChart.SetData(series, tMin, tMax, 0, yMax, thr, "IVT: скорость", bands, "°/с");
        }
        else
        {
            var series = BuildMetricSeries(stimUid, tMin, tMax, useSpeed: false);
            if (series.Count == 0)
            {
                MetricChart.Clear("Нет валидных точек для дисперсии");
                return;
            }

            double thr = _detectSettings.IdtDispersionThresholdPx;
            double maxObserved = series.Max(s => s.Points.Max(p => p.Value));
            double yMax = Math.Max(thr * 2.0, maxObserved * 1.1);

            MetricChart.SetData(series, tMin, tMax, 0, yMax, thr, "IDT: дисперсия", bands, "px");
        }
    }

    private List<MetricSeries> BuildMetricSeries(string stimUid, double tMin, double tMax, bool useSpeed)
    {
        var series = new List<MetricSeries>();

        foreach (var result in EnumerateVisibleResults())
        {
            var raw = GetRawSamplesForStim(result.ResultUid, stimUid);
            if (raw.Count < 2) continue;

            var points = useSpeed
                ? BuildSpeedSeries(raw, tMin, tMax)
                : BuildDispersionSeries(raw, tMin, tMax);

            if (points.Count < 2) continue;

            series.Add(new MetricSeries(points, result.Color));
        }

        return series;
    }

    private List<MetricPoint> BuildSpeedSeries(IReadOnlyList<RawGazeSample> raw, double tMin, double tMax)
    {
        var res = new List<MetricPoint>(512);

        const double MaxSpeedDegPerSec = 800.0;
        const double MaxDtSec = 0.25;

        // для угловой скорости нужны физ. размеры экрана
        if (_screenWmm <= 0 || _screenHmm <= 0)
            return res;

        int a = LowerBoundRaw(raw, tMin);
        if (a > 0) a--; // возьмём точку до начала, чтобы посчитать первую скорость
        int b = UpperBoundRaw(raw, tMax);

        bool havePrev = false;
        RawGazeSample prev = default;

        for (int i = a; i < b; i++)
        {
            var s = raw[i];
            if (!s.Valid) { havePrev = false; continue; }
            if (!double.IsFinite(s.TimeSec) || !double.IsFinite(s.Xn) || !double.IsFinite(s.Yn) || !double.IsFinite(s.DistanceM)) { havePrev = false; continue; }

            if (!havePrev)
            {
                prev = s;
                havePrev = true;
                continue;
            }

            double dt = s.TimeSec - prev.TimeSec;
            if (!double.IsFinite(dt) || dt <= 1e-6 || dt > MaxDtSec) { prev = s; havePrev = true; continue; }

            // если distance=0 — пропускаем (иначе угловая скорость будет мусором)
            double distM = s.DistanceM > 0 ? s.DistanceM : prev.DistanceM;
            if (!double.IsFinite(distM) || distM <= 0) { prev = s; continue; }

            double distMm = distM * 1000.0;

            double x1 = prev.Xn * _screenWmm;
            double y1 = prev.Yn * _screenHmm;
            double x2 = s.Xn * _screenWmm;
            double y2 = s.Yn * _screenHmm;

            double dx = x2 - x1;
            double dy = y2 - y1;
            double dMm = Math.Sqrt(dx * dx + dy * dy);

            double angRad = Math.Atan2(dMm, distMm);
            double speedDegPerSec = (angRad * (180.0 / Math.PI)) / dt;

            if (!double.IsFinite(speedDegPerSec) || speedDegPerSec > MaxSpeedDegPerSec)
            {
                // выброс — разрываем трек, чтобы не "тащить" его дальше
                havePrev = false;
                continue;
            }

            // точку кладём на время текущего сэмпла
            if (s.TimeSec >= tMin && s.TimeSec <= tMax)
                res.Add(new MetricPoint(s.TimeSec, speedDegPerSec));

            prev = s;
        }

        return Decimate(res, 1500);
    }

    private List<MetricPoint> BuildDispersionSeries(IReadOnlyList<RawGazeSample> raw, double tMin, double tMax)
    {
        var res = new List<MetricPoint>(512);

        if (_screenW <= 0 || _screenH <= 0)
            return res;

        double win = Math.Max(0.001, _detectSettings.IdtWindowMs / 1000.0);

        int a = LowerBoundRaw(raw, tMin);
        int b = UpperBoundRaw(raw, tMax);

        for (int i = a; i < b; i++)
        {
            var s0 = raw[i];
            if (!s0.Valid) continue;
            if (!double.IsFinite(s0.TimeSec) || !double.IsFinite(s0.Xn) || !double.IsFinite(s0.Yn)) continue;

            double t0 = s0.TimeSec;
            double t1 = Math.Min(tMax, t0 + win);

            int j = UpperBoundRaw(raw, t1);
            if (j <= i + 1) continue;

            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
            int cnt = 0;

            for (int k = i; k < j; k++)
            {
                var s = raw[k];
                if (!s.Valid) continue;
                if (!double.IsFinite(s.Xn) || !double.IsFinite(s.Yn)) continue;

                double x = s.Xn * _screenW;
                double y = s.Yn * _screenH;

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                cnt++;
            }

            if (cnt < 2) continue;

            double disp = (maxX - minX) + (maxY - minY);
            res.Add(new MetricPoint(t0, disp));
        }

        return Decimate(res, 1500);
    }

    private static List<MetricPoint> Decimate(List<MetricPoint> pts, int maxPoints)
    {
        if (pts.Count <= maxPoints) return pts;
        var res = new List<MetricPoint>(maxPoints);

        double step = (double)(pts.Count - 1) / (maxPoints - 1);
        for (int i = 0; i < maxPoints; i++)
        {
            int idx = (int)Math.Round(i * step);
            if (idx < 0) idx = 0;
            if (idx >= pts.Count) idx = pts.Count - 1;
            res.Add(pts[idx]);
        }
        return res;
    }

    private static int LowerBoundRaw(IReadOnlyList<RawGazeSample> all, double t)
    {
        int l = 0, r = all.Count;
        while (l < r)
        {
            int m = (l + r) >> 1;
            if (all[m].TimeSec < t) l = m + 1;
            else r = m;
        }
        return l;
    }

    private static int UpperBoundRaw(IReadOnlyList<RawGazeSample> all, double t)
    {
        int l = 0, r = all.Count;
        while (l < r)
        {
            int m = (l + r) >> 1;
            if (all[m].TimeSec <= t) l = m + 1;
            else r = m;
        }
        return l;
    }

    private void EnableTimeline()
    {
        VideoTimelinePanel.Visibility = Visibility.Visible;
        SliceTimelinePanel.Visibility = Visibility.Collapsed;

        PlayPauseBtn.IsEnabled = true;
        TimeSlider.IsEnabled = true;
    }

    private void ResetTimelineUi()
    {
        // видео-линия
        PlayPauseBtn.IsEnabled = false;
        TimeSlider.IsEnabled = false;
        TimeSlider.Minimum = 0;
        TimeSlider.Maximum = 1;
        TimeSlider.Value = 0;
        TimeText.Text = "00:00 / 00:00";
        PlayPauseBtn.Content = "▶ Play";

        // time-slice
        _sliceStimUid = null;
        _sliceMin = 0;
        _sliceMax = 0;
        _sliceTotalSec = 0;
        _currentTime = 0;
        _playbackState = PlaybackState.Stop;
        _stopRequested = false;
        _slicePlaybackTimer?.Stop();
        _stopwatch.Reset();

        _suppressSliceEvents = true;
        try
        {
            SliceRange.Minimum = 0;
            SliceRange.Maximum = 1;
            SliceRange.StartValue = 0;
            SliceRange.EndValue = 1;

            SliceText.Text = "—";
            SliceTimeSlider.Minimum = 0;
            SliceTimeSlider.Maximum = 1;
            SliceTimeSlider.Value = 0;
            SliceTimeSlider.IsEnabled = false;
            SlicePlayPauseBtn.IsEnabled = false;
            SliceStopBtn.IsEnabled = false;
            SlicePlayPauseIcon.Text = "▶";
            SliceTimeText.Text = "—";
        }
        finally
        {
            _suppressSliceEvents = false;
        }

        VideoTimelinePanel.Visibility = Visibility.Collapsed;
        SliceTimelinePanel.Visibility = Visibility.Collapsed;
    }

    private void EnableSliceTimelineUi(string stimUid)
    {
        if (_currentStimIsVideo) return;

        double total = GetStimTotalSecForSelection(stimUid);

        // fallback, если длительность не извлеклась из трекера
        if (total <= 0)
        {
            foreach (var series in BuildFixationSeriesForStim(stimUid))
            {
                if (series.Fixations.Count == 0) continue;
                total = Math.Max(total, series.Fixations[^1].StartSec + series.Fixations[^1].DurSec);
            }
        }

        if (total <= 0)
        {
            SliceTimelinePanel.Visibility = Visibility.Collapsed;
            return;
        }

        VideoTimelinePanel.Visibility = Visibility.Collapsed;
        SliceTimelinePanel.Visibility = Visibility.Visible;

        _sliceStimUid = stimUid;
        _sliceTotalSec = total;
        _sliceMin = 0;
        _sliceMax = total;
        _currentTime = 0;
        _playbackState = PlaybackState.Stop;
        _slicePlaybackTimer?.Stop();
        _stopwatch.Reset();

        _suppressSliceEvents = true;
        try
        {
            SliceRange.Minimum = 0;
            SliceRange.Maximum = total;
            SliceRange.StartValue = 0;
            SliceRange.EndValue = total;
        }
        finally { _suppressSliceEvents = false; }

        _sliceMin = 0;
        _sliceMax = total;
        _suppressSliceTimeEvents = true;
        try
        {
            SliceTimeSlider.Minimum = 0;
            SliceTimeSlider.Maximum = total;
            SliceTimeSlider.Value = 0;
            SliceTimeSlider.IsEnabled = true;
        }
        finally { _suppressSliceTimeEvents = false; }

        SlicePlayPauseBtn.IsEnabled = true;
        SliceStopBtn.IsEnabled = true;
        SlicePlayPauseIcon.Text = "▶";
        UpdateSliceText();
        UpdateSliceTimeUi();
        ApplySliceFilter();

    }

    private void ApplySliceFilter()
    {
        if (_sliceStimUid == null) return;
        if (_currentStimIsVideo) return;

        float tMin = (float)Math.Max(0, _sliceMin);
        float tMax = (float)Math.Max(tMin, _sliceMax);

        if (_currentTime < _sliceMin || _currentTime > _sliceMax)
        {
            _currentTime = _sliceMin;
            UpdateSliceTimeUi();
        }

        _suppressSliceTimeEvents = true;
        try
        {
            SliceTimeSlider.Minimum = _sliceMin;
            SliceTimeSlider.Maximum = _sliceMax;
        }
        finally
        {
            _suppressSliceTimeEvents = false;
        }

        UpdateSliceText();
        UpdateSlicePlaybackVisualization();

    }

    private void UpdateSliceText()
    {
        if (_sliceTotalSec <= 0) { SliceText.Text = "—"; return; }
        SliceText.Text = $"{Fmt(_sliceMin)} — {Fmt(_sliceMax)} / {Fmt(_sliceTotalSec)}";
    }

    private void UpdateSliceTimeUi()
    {
        if (_sliceTotalSec <= 0)
        {
            SliceTimeText.Text = "—";
            return;
        }

        _isInternalSliceTimeUpdate = true;
        try
        {
            SliceTimeSlider.Value = Math.Max(SliceTimeSlider.Minimum, Math.Min(SliceTimeSlider.Maximum, _currentTime));
        }
        finally
        {
            _isInternalSliceTimeUpdate = false;
        }

        SliceTimeText.Text = $"{Fmt(_currentTime)} / {Fmt(_sliceTotalSec)}";
    }

    private void UpdateSlicePlaybackVisualization()
    {
        if (_sliceStimUid == null) return;
        if (_currentStimIsVideo) return;

        // Режим "Пчелиный рой"
        if ((int)_currentVizSettings.Mode == 2)
        {
            FixOverlay.Visibility = Visibility.Collapsed;
            HeatmapOverlay.Visibility = Visibility.Collapsed;

            BeeOverlay.ApplySettings(_visualSettings);
            // Данные уже должны быть загружены в ApplyVisualizationForImage,
            // но здесь мы просто обновляем время анимации
            BeeOverlay.Visibility = Visibility.Visible;
            BeeOverlay.SetTime(_currentTime);
            
            // График метрик обновляем всё равно
            UpdateMetricChart(_sliceStimUid, _currentTime - FixTimeSliceSec, _currentTime, null);
            UpdateSliceTimeUi();
            return;
        }

        // --- Старая логика ---
        bool usePlaybackWindow = _playbackState != PlaybackState.Stop;
        double tMin, tMax;

        if (usePlaybackWindow)
        {
            tMax = Math.Min(_sliceMax, _currentTime);
            tMin = Math.Max(_sliceMin, _currentTime - FixTimeSliceSec);
            if (tMax < _sliceMin) tMax = _sliceMin;
            if (tMin > tMax) tMin = tMax;
        }
        else
        {
            tMin = _sliceMin;
            tMax = _sliceMax;
        }

        var visibleSeries = BuildFixationSeriesForWindow(_sliceStimUid, (float)tMin, (float)tMax);

        if (_currentVizSettings.Mode == StimulusVisualizationMode.Heatmap)
        {
            FixOverlay.SetFixationSeries(null);
            FixOverlay.SetPlaybackTime(null, 0, false);
            FixOverlay.Visibility = Visibility.Collapsed;

            // Скрываем bee overlay при переключении на тепловую карту
            BeeOverlay.SetSeries(null);
            BeeOverlay.Visibility = Visibility.Collapsed;

            HeatmapOverlay.ApplySettings(_currentVizSettings.Heatmap);
            var hSeries = BuildHeatmapSeriesForCurrentStim((float)tMin, (float)tMax);
            HeatmapOverlay.SetHeatmapSeries(hSeries);
            HeatmapOverlay.Visibility = hSeries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (_currentVizSettings.Mode == StimulusVisualizationMode.GazePath)
        {
            HeatmapOverlay.SetHeatmapSeries(null);
            HeatmapOverlay.Visibility = Visibility.Collapsed;

            // Скрываем bee overlay при переключении на путь взгляда
            BeeOverlay.SetSeries(null);
            BeeOverlay.Visibility = Visibility.Collapsed;

            FixOverlay.SetFixationSeries(visibleSeries);
            if (usePlaybackWindow)
                FixOverlay.SetPlaybackTime(_currentTime, FixTimeSliceSec, true);
            else
                FixOverlay.SetPlaybackTime(null, 0, false);
            FixOverlay.Visibility = visibleSeries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            FixOverlay.SetFixationSeries(null);
            FixOverlay.Visibility = Visibility.Collapsed;
            HeatmapOverlay.SetHeatmapSeries(null);
            HeatmapOverlay.Visibility = Visibility.Collapsed;
            // Скрываем bee overlay при переключении на другие режимы
            BeeOverlay.SetSeries(null);
            BeeOverlay.Visibility = Visibility.Collapsed;
        }

        UpdateMetricChart(_sliceStimUid, tMin, tMax, visibleSeries);
        UpdateSliceTimeUi();
    }

    private void TickSlicePlayback()
    {
        if (_playbackState != PlaybackState.Play) return;
        if (_sliceStimUid == null) return;
        if (_currentStimIsVideo) return;

        _currentTime = _startTimeAtPlay + _stopwatch.Elapsed.TotalSeconds;

        if (_currentTime > _sliceMax)
        {
            _stopRequested = true;
            SetPlaybackState(PlaybackState.Stop);
            _stopRequested = false;
            return;
        }

        UpdateSlicePlaybackVisualization();
    }

    private void SetPlaybackState(PlaybackState state)
    {
        if (_currentStimIsVideo) return;
        if (_sliceStimUid == null || _sliceTotalSec <= 0) return;

        if (state == PlaybackState.Play)
        {
            if (_playbackState == PlaybackState.Play) return;

            if (_currentTime < _sliceMin || _currentTime > _sliceMax)
                _currentTime = _sliceMin;

            _startTimeAtPlay = _currentTime;
            _stopwatch.Restart();
            _slicePlaybackTimer?.Start();
            _playbackState = PlaybackState.Play;
            SlicePlayPauseIcon.Text = "⏸";
            UpdateSlicePlaybackVisualization();
            return;
        }

        if (state == PlaybackState.Pause)
        {
            if (_playbackState != PlaybackState.Play) return;

            _currentTime = _startTimeAtPlay + _stopwatch.Elapsed.TotalSeconds;
            _slicePlaybackTimer?.Stop();
            _stopwatch.Stop();
            _playbackState = PlaybackState.Pause;
            SlicePlayPauseIcon.Text = "▶";
            UpdateSlicePlaybackVisualization();
            return;
        }

        if (state == PlaybackState.Stop)
        {
            if (_playbackState == PlaybackState.Stop && !_stopRequested) return;

            _slicePlaybackTimer?.Stop();
            _stopwatch.Stop();
            _currentTime = _sliceMin;
            _playbackState = PlaybackState.Stop;
            SlicePlayPauseIcon.Text = "▶";
            UpdateSlicePlaybackVisualization();
        }
    }
    private void ResultsDataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;

        // если клик по CheckBox или по Border-цвету — не даём DataGrid "забрать" первый клик под фокус/выделение
        var dep = e.OriginalSource as DependencyObject;
        if (dep == null) return;

        // Проверяем, что клик был внутри нужных контролов
        if (FindAncestor<System.Windows.Controls.CheckBox>(dep) is System.Windows.Controls.CheckBox cb)
        {
            cb.IsChecked = !(cb.IsChecked ?? false);
            e.Handled = true;
            cb.GetBindingExpression(System.Windows.Controls.CheckBox.IsCheckedProperty)?.UpdateSource();
            return;
        }


        if (FindAncestor<Border>(dep) is Border b && b.DataContext is ResultDisplayItem)
        {
            // Цвет у тебя меняется в ResultColorSwatch_MouseLeftButtonDown,
            // но DataGrid иногда съедает первый клик — поэтому перехватываем тут.
            // Дальше событие дойдёт до Border.MouseLeftButtonDown.
            return;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void SlicePlayPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_playbackState == PlaybackState.Play)
            SetPlaybackState(PlaybackState.Pause);
        else
            SetPlaybackState(PlaybackState.Play);
    }

    private void SliceStopBtn_Click(object sender, RoutedEventArgs e)
    {
        SetPlaybackState(PlaybackState.Stop);
    }


    private async System.Threading.Tasks.Task PrimeFirstFrameAndPauseAsync()
    {
        if (_player == null) return;

        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();

        void OnTimeChanged(object? s, MediaPlayerTimeChangedEventArgs e)
        {
            // как только пошло время — значит кадр уже “проснулся”
            tcs.TrySetResult(true);
        }

        _player.TimeChanged += OnTimeChanged;
        try
        {
            await System.Threading.Tasks.Task.WhenAny(tcs.Task, System.Threading.Tasks.Task.Delay(300));
        }
        finally
        {
            _player.TimeChanged -= OnTimeChanged;
        }

        try { _player.Pause(); } catch { }
        try { _player.Time = 0; } catch { } // чтобы превью было на самом старте
    }

    private static List<Fixation> MapFixationsToStimRect(
    IReadOnlyList<Fixation> fixScreen,
    double offX, double offY, double w2, double h2)
    {
        return MapFixationsToStimRectPaired(fixScreen, offX, offY, w2, h2, out _);
    }

    private static List<Fixation> MapFixationsToStimRectPaired(
        IReadOnlyList<Fixation> fixScreen,
        double offX, double offY, double w2, double h2,
        out List<Fixation> keptScreen)
    {
        var res = new List<Fixation>(fixScreen.Count);
        keptScreen = new List<Fixation>(fixScreen.Count);
        float ox = (float)offX, oy = (float)offY;
        float maxX = (float)w2, maxY = (float)h2;

        foreach (var f in fixScreen)
        {
            float x = f.Xpx - ox;
            float y = f.Ypx - oy;

            // отсекаем то, что попало на чёрные поля
            if (x < 0 || y < 0 || x > maxX || y > maxY) continue;

            keptScreen.Add(f);
            res.Add(new Fixation(f.StartSec, f.DurSec, x, y));
        }
        return res;
    }

    private static bool TryGetVideoSizeFromTracks(LibVLCSharp.Shared.Media media, out uint w, out uint h)
    {
        w = 0; h = 0;
        var tracks = media.Tracks;
        if (tracks == null) return false;

        foreach (var t in tracks)
        {
            if (t.TrackType != LibVLCSharp.Shared.TrackType.Video) continue;

            // Пытаемся вытащить Width/Height из разных вариантов API через reflection
            if (TryReadVideoWH(t, out w, out h))
                return w > 0 && h > 0;
        }
        return false;
    }

    private static bool TryReadVideoWH(object track, out uint w, out uint h)
    {
        w = 0; h = 0;

        // Вариант 1 (в некоторых версиях): track.Data.Video.Width/Height
        var dataProp = track.GetType().GetProperty("Data");
        if (dataProp != null)
        {
            var data = dataProp.GetValue(track);
            if (data != null && TryReadNestedWH(data, "Video", out w, out h)) return true;
        }

        // Вариант 2: track.Video.Width/Height
        if (TryReadNestedWH(track, "Video", out w, out h)) return true;

        // Варианты “на всякий”
        if (TryReadNestedWH(track, "VideoTrack", out w, out h)) return true;
        if (TryReadNestedWH(track, "VideoInfo", out w, out h)) return true;

        return false;
    }

    private static bool TryReadNestedWH(object obj, string propName, out uint w, out uint h)
    {
        w = 0; h = 0;

        var p = obj.GetType().GetProperty(propName);
        if (p == null) return false;

        var nested = p.GetValue(obj);
        if (nested == null) return false;

        var wp = nested.GetType().GetProperty("Width");
        var hp = nested.GetType().GetProperty("Height");
        if (wp == null || hp == null) return false;

        var wv = wp.GetValue(nested);
        var hv = hp.GetValue(nested);

        // приводим аккуратно
        if (wv is uint wu && hv is uint hu) { w = wu; h = hu; return true; }
        if (wv is int wi && hv is int hi) { if (wi > 0 && hi > 0) { w = (uint)wi; h = (uint)hi; return true; } }
        if (wv is long wl && hv is long hl) { if (wl > 0 && hl > 0) { w = (uint)wl; h = (uint)hl; return true; } }

        return false;
    }

    private (int w, int h) GetSystemScreenPixels()
    {
        const int HORZRES = 8;
        const int VERTRES = 10;
        const int DESKTOPVERTRES = 117;
        const int DESKTOPHORZRES = 118;

        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            int w = GetDeviceCaps(hdc, DESKTOPHORZRES);
            int h = GetDeviceCaps(hdc, DESKTOPVERTRES);

            if (w <= 0 || h <= 0)
            {
                w = GetDeviceCaps(hdc, HORZRES);
                h = GetDeviceCaps(hdc, VERTRES);
            }

            return (w, h);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }


    // Пиксели + физические размеры (мм) — из системных API.
    // Используем для I-VT, т.к. нужна конвертация px->mm.
    private (int wPx, int hPx, int wMm, int hMm) GetSystemScreenInfo()
    {
        const int HORZSIZE = 4; // mm
        const int VERTSIZE = 6; // mm
        const int HORZRES = 8;  // px
        const int VERTRES = 10; // px

        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            int wPx = GetDeviceCaps(hdc, HORZRES);
            int hPx = GetDeviceCaps(hdc, VERTRES);
            int wMm = GetDeviceCaps(hdc, HORZSIZE);
            int hMm = GetDeviceCaps(hdc, VERTSIZE);

            // fallback для px (если что-то странное вернулось)
            if (wPx <= 0 || hPx <= 0)
            {
                var dip = GetSystemScreenPixels();
                wPx = dip.w;
                hPx = dip.h;
            }

            return (wPx, hPx, wMm, hMm);
        }
        finally
        {
            if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);


    private static string? ResolveStimulusPath(string expDir, string uid, string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;

        var p1 = Path.Combine(expDir, uid, filename);
        if (File.Exists(p1)) return p1;

        var p2 = Path.Combine(expDir, filename);
        if (File.Exists(p2)) return p2;

        try
        {
            return Directory.EnumerateFiles(expDir, filename, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
    }

    private static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm";
    }

    private static Brush? TryParseRgbaBrush(string? rgba)
    {
        if (string.IsNullOrWhiteSpace(rgba)) return null;

        // "#RRGGBB" / "#AARRGGBB"
        if (rgba.TrimStart().StartsWith("#"))
        {
            try
            {
                var obj = new BrushConverter().ConvertFromString(rgba);
                if (obj is SolidColorBrush b)
                {
                    if (!b.IsFrozen) b.Freeze();
                    return b;
                }
            }
            catch { }
        }

        // "1 0 0 1" или "255,0,0,255"
        var parts = rgba.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;

        if (!double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var r)) return null;
        if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var g)) return null;
        if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var b2)) return null;

        double a = 1.0;
        if (parts.Length >= 4 && double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var aa))
            a = aa;

        bool unit01 = (r <= 1 && g <= 1 && b2 <= 1 && a <= 1);

        byte ToByte(double v)
        {
            if (unit01) v *= 255.0;
            v = Math.Max(0, Math.Min(255, v));
            return (byte)Math.Round(v);
        }

        var col = Color.FromArgb(ToByte(a), ToByte(r), ToByte(g), ToByte(b2));
        var br = new SolidColorBrush(col);
        br.Freeze();
        return br;
    }

    // -------- local model --------

    private static BitmapImage? GenerateThumbnail(string filePath, bool isImage, Brush? colorBrush)
    {
        try
        {
            if (isImage && File.Exists(filePath))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 280;
                bmp.UriSource = new Uri(filePath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
        }
        catch { }

        return null;
    }

    private sealed class StimulusItem
    {
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string Uid { get; set; } = "";
        public string? FilePath { get; set; }
        public bool IsVideo { get; set; }
        public bool IsImage { get; set; }
        public Brush? ColorBrush { get; set; }
        public bool Scale { get; set; } = true;
        public BitmapImage? Thumbnail { get; set; }
        public Brush PreviewBackground { get; set; } = Brushes.Transparent;
        public bool HasFile => FilePath != null;
    }

    private sealed class ResultDisplayItem : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isVisible;
        private Color _color;
        private Brush _colorBrush;

        public string ResultUid { get; }
        public DateTime Date { get; }
        public string DateString => Date.ToString("dd.MM.yyyy HH:mm");
        public long DurationSec { get; }
        public string Name { get; }

        public Color Color
        {
            get => _color;
            set
            {
                if (_color == value) return;
                _color = value;
                _colorBrush = CreateFrozenBrush(_color);
                OnPropertyChanged();
                OnPropertyChanged(nameof(ColorBrush));
                OnPropertyChanged(nameof(ColorHex));
            }
        }

        public string ColorHex => $"#{_color.R:X2}{_color.G:X2}{_color.B:X2}";

        public Brush ColorBrush => _colorBrush;

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible == value) return;
                _isVisible = value;
                OnPropertyChanged();
            }
        }

        public ResultDisplayItem(string resultUid, Color color, DateTime date, long durationSec, string name, bool isVisible)
        {
            ResultUid = resultUid;
            _color = color;
            _colorBrush = CreateFrozenBrush(color);
            Date = date;
            DurationSec = durationSec;
            Name = name;
            _isVisible = isVisible;
        }

        private static Brush CreateFrozenBrush(Color color)
        {
            var b = new SolidColorBrush(color);
            if (b.CanFreeze) b.Freeze();
            return b;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
    }

    private static readonly Color[] DefaultResultColors =
    {
        Color.FromRgb(0xEF, 0x44, 0x44),
        Color.FromRgb(0x3B, 0x82, 0xF6),
        Color.FromRgb(0x10, 0xB9, 0x81),
        Color.FromRgb(0xF5, 0x9E, 0x0B),
        Color.FromRgb(0x8B, 0x5C, 0xF6),
        Color.FromRgb(0x14, 0xB8, 0xA6),
        Color.FromRgb(0xEC, 0x48, 0x99),
        Color.FromRgb(0x22, 0xC5, 0x5E),
        Color.FromRgb(0xE1, 0x1D, 0x48),
        Color.FromRgb(0x0E, 0xA5, 0xE9),
    };

    private static Color ResolveResultColor(string? rgba, int index)
    {
        if (TryParseRgbaColor(rgba, out var color))
            return color;

        if (index < 0) index = 0;
        return DefaultResultColors[index % DefaultResultColors.Length];
    }

    private static bool TryParseRgbaColor(string? rgba, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(rgba)) return false;

        if (rgba.TrimStart().StartsWith("#"))
        {
            try
            {
                var obj = new BrushConverter().ConvertFromString(rgba);
                if (obj is SolidColorBrush b)
                {
                    color = b.Color;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        var parts = rgba.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;

        if (!double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var r)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var g)) return false;
        if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var b2)) return false;

        double a = 1.0;
        if (parts.Length >= 4 && double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var aa))
            a = aa;

        bool unit01 = (r <= 1 && g <= 1 && b2 <= 1 && a <= 1);

        byte ToByte(double v)
        {
            if (unit01) v *= 255.0;
            v = Math.Clamp(v, 0, 255);
            return (byte)Math.Round(v);
        }

        color = Color.FromArgb(ToByte(a), ToByte(r), ToByte(g), ToByte(b2));
        return true;
    }

    private void ExportBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "Выберите папку для экспорта данных",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() != WinForms.DialogResult.OK)
                return;

            var outputDir = dialog.SelectedPath;
            if (string.IsNullOrWhiteSpace(outputDir))
                return;

            ExportExperimentData(outputDir);
            MessageBox.Show($"Экспорт завершён!\n\nФайлы сохранены в:\n{outputDir}", "Экспорт", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при экспорте: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportExperimentData(string outputDir)
    {
        _exp ??= JsonSerializer.Deserialize<ExperimentFile>(
            File.ReadAllText(Path.Combine(_expDir, "exp.json")), _jsonOpts);

        if (_exp == null)
        {
            MessageBox.Show("Не удалось загрузить exp.json", "Экспорт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_primaryResultUid))
        {
            MessageBox.Show("Не выбран результат для экспорта", "Экспорт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var trackerUid = GetTrackerUid();
        if (string.IsNullOrWhiteSpace(trackerUid))
        {
            MessageBox.Show("Не найден UID трекера в эксперименте", "Экспорт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var resultUid = _primaryResultUid;

        ExportRawGazeData(resultUid, trackerUid, outputDir);
        ExportStatistics(resultUid, outputDir);
    }

    private void ExportRawGazeData(string resultUid, string trackerUid, string outputDir)
    {
        var csvPath = Path.Combine(outputDir, "gaze_raw.csv");
        using var writer = new StreamWriter(csvPath, false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        writer.WriteLine("stimulus,time,valid,x,y,z,lp,rp,lopen,ropen");

        if (_exp == null || _exp.Stimuls == null)
            return;

        foreach (var stim in _exp.Stimuls)
        {
            if (stim.Kind == 0) continue;

            var binPath = Path.Combine(_expDir, "results", resultUid, stim.Uid, trackerUid);
            if (!File.Exists(binPath)) continue;

            using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] buf = ArrayPool<byte>.Shared.Rent(TrackerData.Size);
            try
            {
                Span<byte> span = buf.AsSpan(0, TrackerData.Size);

                float t0 = float.NaN;

                while (true)
                {
                    int n = fs.Read(span);
                    if (n == 0) break;
                    if (n != TrackerData.Size) break;

                    int valid = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(0, 4));
                    float time = ReadF(span, 4);
                    if (float.IsNaN(t0)) t0 = time;
                    float timeSec = time - t0;

                    const string Nan = "";

                    bool leftCoordValid = (valid & (int)TrackerDataValidity.LEFT_PUPIL_COORD_VALID) != 0;
                    bool rightCoordValid = (valid & (int)TrackerDataValidity.RIGHT_PUPIL_COORD_VALID) != 0;

                    float lx = ReadF(span, 60);
                    float ly = ReadF(span, 64);
                    float rx = ReadF(span, 52);
                    float ry = ReadF(span, 56);

                    bool lGood = leftCoordValid && float.IsFinite(lx) && float.IsFinite(ly) && lx >= 0 && lx <= 1 && ly >= 0 && ly <= 1;
                    bool rGood = rightCoordValid && float.IsFinite(rx) && float.IsFinite(ry) && rx >= 0 && rx <= 1 && ry >= 0 && ry <= 1;

                    string xStr;
                    string yStr;

                    if (lGood && rGood)
                    {
                        xStr = ((lx + rx) * 0.5f).ToString("F6", CultureInfo.InvariantCulture);
                        yStr = ((ly + ry) * 0.5f).ToString("F6", CultureInfo.InvariantCulture);
                    }
                    else if (lGood)
                    {
                        xStr = lx.ToString("F6", CultureInfo.InvariantCulture);
                        yStr = ly.ToString("F6", CultureInfo.InvariantCulture);
                    }
                    else if (rGood)
                    {
                        xStr = rx.ToString("F6", CultureInfo.InvariantCulture);
                        yStr = ry.ToString("F6", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        xStr = Nan;
                        yStr = Nan;
                    }

                    bool leftZValid = (valid & (int)TrackerDataValidity.LEFT_PUPIL_3D_COORD_VALID) != 0;
                    bool rightZValid = (valid & (int)TrackerDataValidity.RIGHT_PUPIL_3D_COORD_VALID) != 0;

                    float lz = ReadF(span, 36);
                    float rz = ReadF(span, 48);

                    bool lZGood = leftZValid && float.IsFinite(lz) && lz > 0;
                    bool rZGood = rightZValid && float.IsFinite(rz) && rz > 0;

                    string zStr;
                    if (lZGood && rZGood) zStr = ((lz + rz) * 0.5f).ToString("F6", CultureInfo.InvariantCulture);
                    else if (lZGood) zStr = lz.ToString("F6", CultureInfo.InvariantCulture);
                    else if (rZGood) zStr = rz.ToString("F6", CultureInfo.InvariantCulture);
                    else zStr = Nan;

                    float lp = ReadF(span, 20);
                    float rp = ReadF(span, 24);

                    bool leftPupilValid = (valid & (int)TrackerDataValidity.LEFT_PUPIL_SIZE_VALID) != 0;
                    bool rightPupilValid = (valid & (int)TrackerDataValidity.RIGHT_PUPIL_SIZE_VALID) != 0;

                    string lpStr = leftPupilValid && float.IsFinite(lp) ? lp.ToString("F6", CultureInfo.InvariantCulture) : Nan;
                    string rpStr = rightPupilValid && float.IsFinite(rp) ? rp.ToString("F6", CultureInfo.InvariantCulture) : Nan;

                    float lopen = ReadF(span, 68);
                    float ropen = ReadF(span, 72);

                    bool leftOpenValid = (valid & (int)TrackerDataValidity.LEFT_OPEN_VALID) != 0;
                    bool rightOpenValid = (valid & (int)TrackerDataValidity.RIGHT_OPEN_VALID) != 0;

                    string lopenStr = leftOpenValid && float.IsFinite(lopen) ? lopen.ToString("F6", CultureInfo.InvariantCulture) : Nan;
                    string ropenStr = rightOpenValid && float.IsFinite(ropen) ? ropen.ToString("F6", CultureInfo.InvariantCulture) : Nan;

                    int validInt = (lGood || rGood) ? 1 : 0;

                    string timeStr = float.IsFinite(timeSec) ? timeSec.ToString("F6", CultureInfo.InvariantCulture) : Nan;
                    writer.WriteLine($"{stim.Uid},{timeStr},{validInt},{xStr},{yStr},{zStr},{lpStr},{rpStr},{lopenStr},{ropenStr}");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        static float ReadF(Span<byte> b, int off) =>
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(b.Slice(off, 4)));
    }

    private void ExportStatistics(string resultUid, string outputDir)
    {
        var csvPath = Path.Combine(outputDir, "stats.csv");
        using var writer = new StreamWriter(csvPath, false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        writer.WriteLine("stimulus,duration_sec,time_to_first_fixation_sec,avg_fixation_duration_sec,fixation_count,avg_saccade_amplitude_px,avg_saccade_amplitude_deg,total_saccade_count");

        if (_exp == null || _exp.Stimuls == null)
            return;

        foreach (var stim in _exp.Stimuls)
        {
            if (stim.Kind == 0) continue;

            var (wPx, hPx, wMm, hMm) = ReadStimulusScreenInfo(resultUid, stim.Uid);
            
            var duration = GetStimTotalSec(resultUid, stim.Uid);
            var (fixScreen, fixStim) = EnsureFixationsForStimPaired(resultUid, stim.Uid);

            const string Nan = "nan";

            if (fixStim.Count == 0 || fixScreen.Count == 0 || fixStim.Count != fixScreen.Count)
            {
                writer.WriteLine($"{stim.Uid},{duration.ToString("F3", CultureInfo.InvariantCulture)},{Nan},{Nan},0,{Nan},{Nan},0");
                continue;
            }

            var timeToFirstFix = fixStim[0].StartSec;
            var avgFixDuration = fixStim.Average(f => f.DurSec);
            var fixCount = fixStim.Count;

            var saccades = new List<(float distance, float angleDeg)>();
            for (int i = 1; i < fixStim.Count; i++)
            {
                var f1Stim = fixStim[i - 1];
                var f2Stim = fixStim[i];
                var f1Screen = fixScreen[i - 1];
                var f2Screen = fixScreen[i];

                float dxStim = f2Stim.Xpx - f1Stim.Xpx;
                float dyStim = f2Stim.Ypx - f1Stim.Ypx;
                float distPx = (float)Math.Sqrt(dxStim * dxStim + dyStim * dyStim);

                float angleDeg = float.NaN;
                if (wPx > 0 && hPx > 0 && wMm > 0 && hMm > 0)
                {
                    var raw = GetRawSamplesForStim(resultUid, stim.Uid);
                    var samplesInRange = raw.Where(s => s.Valid && s.TimeSec >= f1Stim.StartSec && s.TimeSec <= (f2Stim.StartSec + f2Stim.DurSec)).ToList();

                    if (samplesInRange.Count > 0)
                    {
                        float avgDist = samplesInRange.Where(s => float.IsFinite(s.DistanceM) && s.DistanceM > 0)
                            .Select(s => s.DistanceM).DefaultIfEmpty(0).Average();
                        if (avgDist > 0)
                        {
                            // В градусах считаем по экранным пикселям, чтобы mmPerPx соответствовал экрану.
                            float dx = f2Screen.Xpx - f1Screen.Xpx;
                            float dy = f2Screen.Ypx - f1Screen.Ypx;

                            float mmPerPxX = wMm / (float)wPx;
                            float mmPerPxY = hMm / (float)hPx;
                            float dxMm = dx * mmPerPxX;
                            float dyMm = dy * mmPerPxY;
                            float dMm = (float)Math.Sqrt(dxMm * dxMm + dyMm * dyMm);
                            float angRad = (float)Math.Atan2(dMm, avgDist * 1000f);
                            angleDeg = angRad * 57.2957795f;
                        }
                    }
                }

                saccades.Add((distPx, angleDeg));
            }

            var avgSaccadeDistPx = saccades.Count > 0 ? saccades.Average(s => s.distance) : float.NaN;
            var avgSaccadeAngleDeg = saccades.Count > 0 ? saccades.Where(s => !float.IsNaN(s.angleDeg)).Select(s => s.angleDeg).DefaultIfEmpty(float.NaN).Average() : float.NaN;
            var saccadeCount = saccades.Count;

            string avgSaccadePxStr = float.IsNaN(avgSaccadeDistPx) ? Nan : avgSaccadeDistPx.ToString("F2", CultureInfo.InvariantCulture);
            string avgSaccadeDegStr = float.IsNaN(avgSaccadeAngleDeg) ? Nan : avgSaccadeAngleDeg.ToString("F2", CultureInfo.InvariantCulture);

            writer.WriteLine($"{stim.Uid},{duration.ToString("F3", CultureInfo.InvariantCulture)},{timeToFirstFix.ToString("F3", CultureInfo.InvariantCulture)},{avgFixDuration.ToString("F3", CultureInfo.InvariantCulture)},{fixCount},{avgSaccadePxStr},{avgSaccadeDegStr},{saccadeCount}");
        }
    }

    private double GetStimTotalSec(string resultUid, string stimUid)
    {
        var key = KF(resultUid, stimUid);
        if (_stimDurationCache.TryGetValue(key, out var cached))
            return cached;

        var raw = GetRawSamplesForStim(resultUid, stimUid);
        if (raw.Count == 0)
            return 0;

        var duration = raw[^1].TimeSec;
        _stimDurationCache[key] = duration;
        return duration;
    }
}
