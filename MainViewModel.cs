using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Data;
using System.Globalization;
using System.Threading;
// === ИСПРАВЛЕНИЕ КОНФЛИКТОВ ===
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Application = System.Windows.Application; // Важно!
namespace NeuroBureau.Experiment;

public sealed class MainViewModel : ObservableObject
{
    private static readonly string[] DefaultResultColors =
    {
        "#EF4444", // red
        "#3B82F6", // blue
        "#10B981", // emerald
        "#F59E0B", // amber
        "#8B5CF6", // violet
        "#14B8A6", // teal
        "#EC4899", // pink
        "#22C55E", // green
        "#E11D48", // rose
        "#0EA5E9", // sky
    };
    private string _experimentsRoot;
    public string ExperimentsRoot
    {
        get => _experimentsRoot;
        set
        {
            var v = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(v)) return;

            if (SetProperty(ref _experimentsRoot, v))
            {
                _ = ReloadExperimentsAsync();
            }
        }
    }


    public ObservableCollection<ExperimentListItem> Experiments { get; } = new();
    public ObservableCollection<StimulusTileVm> Stimuli { get; } = new();

    private ExperimentListItem? _selectedExperiment;
    public ExperimentListItem? SelectedExperiment
    {
        get => _selectedExperiment;
        set
        {
            if (!SetProperty(ref _selectedExperiment, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedDevicesText));
            OnPropertyChanged(nameof(SelectedDescription));
            OnPropertyChanged(nameof(SelectedMetaText));
            OnPropertyChanged(nameof(SelectedPathText));

            _ = ReloadStimuliAsync(); // грузим стимулы при выборе
            _ = ReloadResultsAsync();
            _ = ReloadDeviceCardsAsync();
            OnPropertyChanged(nameof(CanRun));

        }
    }

    private static bool IsMouseKeyboardDevice(DeviceFile d)
        => string.Equals((d.DevType ?? "").Trim(), "MouseKeyboard", StringComparison.OrdinalIgnoreCase);

    private static bool IsEyeTrackerDevice(DeviceFile d)
    {
        var t = (d.DevType ?? "").Trim();
        return t.Equals("PathFinder", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Gazepoint", StringComparison.OrdinalIgnoreCase)
            || t.Contains("tracker", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKgrDevice(DeviceFile d)
    {
        var t = (d.DevType ?? "").Trim();
        var n = (d.Name ?? "").Trim();
        return t.Contains("shimmer", StringComparison.OrdinalIgnoreCase)
            || n.Contains("shimmer", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    
    private async Task ReloadResultsAsync()
    {
        var item = SelectedExperiment;

        // Пустая таблица по умолчанию
        if (item == null)
        {
            ResultsView = BuildResultsTableSchema(characteristicNames: null).DefaultView;
            return;
        }

        try
        {
            // 1) Читаем exp.json чтобы понять, какие колонки “характеристик” показывать
            // Важно: используй твой реальный путь/тип ExperimentFile (он у тебя уже есть в проекте).
            var expJsonPath = System.IO.Path.Combine(item.ExpDir, "exp.json");
            ExperimentFile? exp = null;

            if (File.Exists(expJsonPath))
            {
                var json = await File.ReadAllTextAsync(expJsonPath);
                exp = JsonSerializer.Deserialize<ExperimentFile>(json, _jsonOpts);
            }

            var characteristicNames = exp?.Characteristics?
                .Select(c => c.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)           // <-- превращаем в List<string>
                .Distinct()
                .ToList();

            // 2) Строим схему таблицы
            var table = BuildResultsTableSchema(characteristicNames);

            // 3) Читаем results/*/result.json
            var resultsDir = System.IO.Path.Combine(item.ExpDir, "results");
            if (!Directory.Exists(resultsDir))
            {
                ResultsView = table.DefaultView;
                return;
            }

            var rows = new List<(long timeUnix, DataRow row)>();

            int colorIndex = 0;
            foreach (var dir in Directory.EnumerateDirectories(resultsDir))
            {
                var uid = System.IO.Path.GetFileName(dir);
                var resultJson = System.IO.Path.Combine(dir, "result.json");
                if (!File.Exists(resultJson)) continue;

                var rjson = await File.ReadAllTextAsync(resultJson);
                var result = JsonSerializer.Deserialize<ResultFile>(rjson, _jsonOpts);
                if (result == null) continue;

                var row = table.NewRow();

                row["Select"] = false;
                row["ResultUid"] = uid;
                if (!string.IsNullOrWhiteSpace(result.Rgba))
                {
                    row["Color"] = result.Rgba;
                }
                else
                {
                    row["Color"] = DefaultResultColors[colorIndex % DefaultResultColors.Length];
                    colorIndex++;
                }

                // Date (локальное время)
                var dt = DateTimeOffset.FromUnixTimeSeconds(result.TimeUnix).ToLocalTime().DateTime;
                row["Date"] = dt.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

                row["Duration"] = result.DurationSec;

                // chars-data -> колонки
                var map = (result.CharsData ?? new List<CharValue>())
                    .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                    .GroupBy(x => x.Name!)
                    .ToDictionary(g => g.Key, g => g.Last().Val ?? "", StringComparer.OrdinalIgnoreCase);

                if (characteristicNames != null)
                {
                    foreach (var name in characteristicNames)
                    {
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        row[name] = map.TryGetValue(name, out var v) ? v : "";
                    }
                }

                rows.Add((result.TimeUnix, row));
            }

            // сортировка по дате (новые сверху)
            foreach (var (_, row) in rows.OrderByDescending(r => r.timeUnix))
                table.Rows.Add(row);

            ResultsView = table.DefaultView;
        }
        catch
        {
            // если хочешь — сюда можно поставить StatusText = "...ошибка чтения результатов"
            ResultsView = BuildResultsTableSchema(characteristicNames: null).DefaultView;
        }
    }

    private DataTable BuildResultsTableSchema(List<string>? characteristicNames)
    {
        var t = new DataTable();

        t.Columns.Add("Select", typeof(bool));
        t.Columns.Add("Date", typeof(string));
        t.Columns.Add("Duration", typeof(long));

        // тех.колонка (скроем в AutoGeneratingColumn)
        t.Columns.Add("ResultUid", typeof(string));
        t.Columns.Add("Color", typeof(string)); // хранит r.Rgba (например "#RRGGBB" или "1 0 0 1")

        if (characteristicNames != null)
        {
            foreach (var name in characteristicNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!t.Columns.Contains(name))
                    t.Columns.Add(name, typeof(string));
            }
        }

        return t;
    }

    public bool HasSelection => SelectedExperiment != null;

    public string SelectedDevicesText =>
        SelectedExperiment?.DevicesText ?? "—";

    public string SelectedDescription =>
        SelectedExperiment?.Description ?? "";

    public string SelectedPathText =>
        SelectedExperiment == null ? "" :
        $"Experiments root:\n{ExperimentsRoot}\n\nExperiment folder:\n{SelectedExperiment.ExpDir}";

    public string SelectedMetaText =>
        SelectedExperiment == null ? "" :
        $"Устройства: {SelectedExperiment.DevicesText} | Результатов: {SelectedExperiment.ResultsCount} | " +
        $"mod-time: {SelectedExperiment.ModTimeText} | create-time: {SelectedExperiment.CreateTimeText}";

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string StimuliCountText => $"{Stimuli.Count} items";

    public MainViewModel()
    {
        ExperimentsRoot = ExperimentLoader.GetExperimentsRoot();

        Stimuli.CollectionChanged += (_, __) =>
            OnPropertyChanged(nameof(StimuliCountText));
        _experimentsRoot = ExperimentLoader.GetExperimentsRoot();
        
        _trackerClient = new TrackerClient(TrackerDefaults.BaseUrl);

        // Запускаем автоматическую проверку трекера при инициализации
        _ = Task.Run(async () =>
        {
            await ProbeEyeTrackerAsync();
            
            // Запускаем периодическую проверку трекера
            while (!App.Current.Dispatcher.HasShutdownStarted)
            {
                await Task.Delay(TimeSpan.FromSeconds(2)); // Проверяем каждые 2 секунды
                await ProbeEyeTrackerAsync();
            }
        });
    }
    private DataView _resultsView = new DataTable().DefaultView;
    public DataView ResultsView
    {
        get => _resultsView;
        private set => SetProperty(ref _resultsView, value);
    }

    public async Task ReloadExperimentsAsync(string? selectUid = null)
    {
        StatusText = $"Папка Experiments: {ExperimentsRoot}";

        List<ExperimentListItem> list;
        try
        {
            list = await Task.Run(() => ScanExperiments(ExperimentsRoot));
        }
        catch 
        {
            StatusText = "Ошибка загрузки экспериментов";
            // можно логировать ex
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            Experiments.Clear();
            foreach (var it in list) Experiments.Add(it);

            if (!string.IsNullOrWhiteSpace(selectUid))
            {
                SelectedExperiment = Experiments.FirstOrDefault(x =>
                    string.Equals(x.UidFolder, selectUid, StringComparison.OrdinalIgnoreCase));
            }

            if (SelectedExperiment == null && Experiments.Count > 0)
                SelectedExperiment = Experiments[0];
        });
    }

    private static List<ExperimentListItem> ScanExperiments(string root)
    {
        Directory.CreateDirectory(root);

        var list = new List<ExperimentListItem>();

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var expJson = Path.Combine(dir, "exp.json");
            if (!File.Exists(expJson)) continue;

            try
            {
                var exp = JsonSerializer.Deserialize<ExperimentFile>(File.ReadAllText(expJson));
                if (exp == null) continue;

                int resultsCount = 0;
                var resultsDir = Path.Combine(dir, "results");
                if (Directory.Exists(resultsDir))
                    resultsCount = Directory.EnumerateDirectories(resultsDir).Count();

                list.Add(new ExperimentListItem
                {
                    UidFolder = Path.GetFileName(dir),
                    ExpDir = dir,
                    ExpJsonPath = expJson,
                    Name = exp.Name,
                    Description = exp.Description,
                    CreateTime = exp.CreateTime,
                    ModTime = exp.ModTime,
                    ResultsCount = resultsCount,
                    DevicesText = BuildDevicesText(exp),
                });
            }
            catch
            {
                // битый exp.json — пропускаем
            }
        }

        // “свежие” сверху (если mod-time парсится плохо, всё равно будет стабильный порядок по числу)
        return list.OrderByDescending(x => x.ModTime).ToList();
    }

    private static string BuildDevicesText(ExperimentFile exp)
    {
        var parts = new List<string>();

        foreach (var d in exp.Devices)
        {
            var t = (d.DevType ?? "").Trim();

            if (t.Equals("MouseKeyboard", StringComparison.OrdinalIgnoreCase))
                parts.Add("Клавиатура/мышь");
            else if (t.Equals("ShimmerGSR", StringComparison.OrdinalIgnoreCase))
                parts.Add("КГР");
            else if (t.Equals("PathFinder", StringComparison.OrdinalIgnoreCase) ||
                     t.Equals("Gazepoint", StringComparison.OrdinalIgnoreCase) ||
                     t.Contains("tracker", StringComparison.OrdinalIgnoreCase))
                parts.Add("Айтрекер");
            else if (!string.IsNullOrWhiteSpace(d.Name))
                parts.Add(d.Name!);
            else if (!string.IsNullOrWhiteSpace(t))
                parts.Add(t);
            else
                parts.Add("Неизвестно");
        }

        return string.Join(" + ", parts.Distinct());
    }
    private bool _isEyeTrackerConnected;
    public bool IsEyeTrackerConnected
    {
        get => _isEyeTrackerConnected;
        set
        {
            if (SetProperty(ref _isEyeTrackerConnected, value))
                OnPropertyChanged(nameof(CanRun));
        }
    }
    public List<string> GetMissingOptionalDevices()
    {
        // optional = все кроме трекера (который обязателен)
        return DeviceCards
            .Where(c => c.Kind != DeviceKind.EyeTracker)
            .Where(c => c.Kind != DeviceKind.Kgr) // КГР решаем в DeviceCheckWindow при старте
            .Where(c => c.Status != DeviceStatus.Connected)
            .Select(c => c.Title)
            .ToList();
    }

    public bool CanRun => HasSelection && IsEyeTrackerConnected;

    // В файле MainViewModel.cs

    private async Task ReloadStimuliAsync()
    {
        // УБРАЛИ Stimuli.Clear() отсюда. 
        // Если очистить здесь, то при параллельных вызовах мы получим дубликаты,
        // либо список будет неприятно мигать.

        var item = SelectedExperiment;
        if (item == null) 
        {
            Stimuli.Clear();
            OnPropertyChanged(nameof(StimuliCountText));
            return;
        }

        try
        {
            // 1. Сначала готовим все данные в фоновом потоке
            // (чтение файла, создание плиток)
            var tiles = await Task.Run(() =>
            {
                var expJson = File.ReadAllText(item.ExpJsonPath);
                var exp = JsonSerializer.Deserialize<ExperimentFile>(expJson)
                        ?? throw new InvalidOperationException("Failed to parse exp.json");

                var list = new List<StimulusTileVm>();

                foreach (var st in exp.Stimuls)
                {
                    var kind = st.Kind ?? 0;
                    if (kind == 0) continue; 

                    string? path = ResolveStimulusPath(item.ExpDir, st.Uid, st.Filename);
                    bool hasFile = path != null && File.Exists(path);

                    bool isVideo = hasFile && IsVideoFile(path!);
                    bool isImage = hasFile && IsImageFile(path!);

                    ImageSource? thumb = null;
                    // Загрузка картинки в фоне безопасна, если мы делаем Freeze (что есть в TryLoadThumb)
                    if (isImage && path != null)
                        thumb = TryLoadThumb(path);

                    Brush bg = TryParseRgbaBrush(st.Rgba) ?? MakeFrozenBrush(Color.FromRgb(0xF9, 0xFA, 0xFB));

                    var title = !string.IsNullOrWhiteSpace(st.Filename)
                        ? Path.GetFileName(st.Filename)
                        : (!string.IsNullOrWhiteSpace(st.Uid) ? st.Uid : "stimulus");

                    list.Add(new StimulusTileVm
                    {
                        Title = title,
                        Uid = st.Uid,
                        FilePath = path,
                        HasFile = hasFile || !string.IsNullOrWhiteSpace(st.Rgba),
                        IsVideo = isVideo,
                        Thumbnail = thumb,
                        PreviewBackground = bg
                    });
                }
                return list;
            });

            // 2. И только теперь обновляем UI одним действием
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Очищаем и добавляем в одном блоке. 
                // Это гарантирует, что старые данные удалятся прямо перед вставкой новых.
                Stimuli.Clear();
                foreach (var t in tiles) Stimuli.Add(t);
            });
        }
        catch
        {
            // При ошибке можно очистить список или оставить как есть
            Application.Current.Dispatcher.Invoke(() => Stimuli.Clear());
        }
    }

    private static string? ResolveStimulusPath(string expDir, string uid, string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;

        // 1) самый ожидаемый вариант
        var p1 = Path.Combine(expDir, uid, filename);
        if (File.Exists(p1)) return p1;

        // 2) иногда кладут прямо в корень эксперимента
        var p2 = Path.Combine(expDir, filename);
        if (File.Exists(p2)) return p2;

        // 3) fallback: ищем по подпапкам
        // [Не проверено] Я не могу подтвердить твою точную структуру папок со стимулами,
        // поэтому делаю “поисковый” вариант на крайний случай.
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
        return ext is ".mp4" or ".avi" or ".mov" or ".mkv" or ".wmv" or ".webm";
    }

    private static ImageSource? TryLoadThumb(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = 420;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static Brush? TryParseRgbaBrush(string? rgba)
    {
        if (string.IsNullOrWhiteSpace(rgba)) return null;

        // поддержка "#RRGGBB" / "#AARRGGBB"
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

        // поддержка "1 0 0 1" или "255,0,0,255"
        var parts = rgba.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;

        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var r)) return null;
        if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var g)) return null;
        if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var b2)) return null;

        double a = 1.0;
        if (parts.Length >= 4 && double.TryParse(parts[3], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var aa))
            a = aa;

        bool unit01 = (r <= 1 && g <= 1 && b2 <= 1 && a <= 1);
        byte ToByte(double v)
        {
            if (unit01) v *= 255.0;
            v = Math.Max(0, Math.Min(255, v));
            return (byte)Math.Round(v);
        }

        var col = Color.FromArgb(ToByte(a), ToByte(r), ToByte(g), ToByte(b2));
        return MakeFrozenBrush(col);
    }

    private static SolidColorBrush MakeFrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public ObservableCollection<DeviceCardVm> DeviceCards { get; } = new();
    private string _trackerIp = "127.0.0.1";
    public string TrackerIp
    {
        get => _trackerIp;
        set
        {
            var v = (value ?? "").Trim();
          //  if (string.IsNullOrWhiteSpace(v)) v = AppConfigManager.DefaultTrackerIp;

            if (SetProperty(ref _trackerIp, v))
            {
                _trackerClient = new TrackerClient(TrackerDefaults.BaseUrl);
                _ = ProbeEyeTrackerAsync();
            }
        }
    }

    private TrackerClient _trackerClient = new(TrackerDefaults.BaseUrl);
    private CancellationTokenSource? _eyeProbeCts;

    private async Task ReloadDeviceCardsAsync()
    {
        DeviceCards.Clear();

        var item = SelectedExperiment;
        if (item == null)
        {
            OnPropertyChanged(nameof(CanRun));
            return;
        }

        try
        {
            if (!File.Exists(item.ExpJsonPath))
            {
                OnPropertyChanged(nameof(CanRun));
                return;
            }

            var json = await File.ReadAllTextAsync(item.ExpJsonPath);
            var exp = JsonSerializer.Deserialize<ExperimentFile>(json, _jsonOpts);
            var devices = exp?.Devices ?? new List<DeviceFile>();


            RebuildDeviceCardsFromExperiment(devices);

            // асинхронно “пингуем” трекер и обновляем CanRun
            if (DeviceCards.Any(c => c.Kind == DeviceKind.EyeTracker))
                _ = ProbeEyeTrackerAsync();

            OnPropertyChanged(nameof(CanRun));
        }
        catch
        {
            // если что-то пошло не так — пусть трекер будет “не подключен”
            SetStatus(DeviceKind.EyeTracker, DeviceStatus.Disconnected);
            OnPropertyChanged(nameof(CanRun));
        }
    }

    private void RebuildDeviceCardsFromExperiment(IEnumerable<DeviceFile> devices)
    {
        DeviceCards.Clear();

        bool hasEye = devices.Any(d => string.Equals(d.DevType, "PathFinder", StringComparison.OrdinalIgnoreCase));
        bool hasMk  = devices.Any(d => string.Equals(d.DevType, "MouseKeyboard", StringComparison.OrdinalIgnoreCase));
        bool hasKgr = devices.Any(d => string.Equals(d.DevType, "ShimmerGSR", StringComparison.OrdinalIgnoreCase));

        if (hasEye)
            DeviceCards.Add(new DeviceCardVm { Kind = DeviceKind.EyeTracker, Title = "Айтрекер", Status = DeviceStatus.Connecting });

        if (hasMk)
            // важно: это локально, “связи” нет — всегда зелёное
            DeviceCards.Add(new DeviceCardVm { Kind = DeviceKind.MouseKeyboard, Title = "Клавиатура/мышь", Status = DeviceStatus.Connected });

        if (hasKgr)
            DeviceCards.Add(new DeviceCardVm { Kind = DeviceKind.Kgr, Title = "КГР (Shimmer)", Status = DeviceStatus.Disconnected });

        OnPropertyChanged(nameof(CanRun));
    }

    public void SetStatus(DeviceKind kind, DeviceStatus st)
    {
        var card = DeviceCards.FirstOrDefault(c => c.Kind == kind);
        if (card != null) card.Status = st;

        // CanRun завязан на обязательном устройстве — айтрекере
        if (kind == DeviceKind.EyeTracker)
            IsEyeTrackerConnected = (st == DeviceStatus.Connected);

        OnPropertyChanged(nameof(CanRun));
    }


    private async Task ProbeEyeTrackerAsync()
    {
        _eyeProbeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _eyeProbeCts = cts;

        SetStatus(DeviceKind.EyeTracker, DeviceStatus.Connecting);

        try
        {
            using var timeout = new CancellationTokenSource(800);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

            // “жив ли сервер” — если ответил на GET состояния калибровки, считаем подключенным
            // Check tracker state - if we can get calibration state, tracker is connected
            var calibrationState = await _trackerClient.GetCalibrationStateAsync(linked.Token);
            
            // If we got calibration state, tracker is connected
            if (!cts.IsCancellationRequested && calibrationState.HasValue)
                SetStatus(DeviceKind.EyeTracker, DeviceStatus.Connected);
            else if (!cts.IsCancellationRequested)
                SetStatus(DeviceKind.EyeTracker, DeviceStatus.Disconnected);
        }
        catch
        {
            if (!cts.IsCancellationRequested)
                SetStatus(DeviceKind.EyeTracker, DeviceStatus.Disconnected);
        }
    }

    // Public method to manually check tracker connection
    public async Task RefreshEyeTrackerConnectionAsync()
    {
        await ProbeEyeTrackerAsync();
    }

    // -------- Device cards (Devices section UI) --------

    public enum DeviceKind { EyeTracker, MouseKeyboard, Kgr }
    public enum DeviceStatus { Disconnected, Connecting, Connected }

    public sealed class DeviceCardVm : ObservableObject
    {
        public DeviceKind Kind { get; init; }
        public string Title { get; init; } = "";

        private DeviceStatus _status = DeviceStatus.Disconnected;
        public DeviceStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                    UpdateVisuals();
                }
            }
        }

        public string StatusText => Status switch
        {
            DeviceStatus.Connected => "Подключено",
            DeviceStatus.Connecting => "Подключение…",
            _ => "Не подключено"
        };

        private Brush _bg = BrushFrom("#FEE2E2");
        public Brush Bg { get => _bg; private set => SetProperty(ref _bg, value); }

        private Brush _fg = BrushFrom("#B91C1C");
        public Brush Fg { get => _fg; private set => SetProperty(ref _fg, value); }

        private string _emoji = "❌";
        public string Emoji { get => _emoji; private set => SetProperty(ref _emoji, value); }

        public DeviceCardVm() => UpdateVisuals();

        private void UpdateVisuals()
        {
            switch (Status)
            {
                case DeviceStatus.Connected:
                    Bg = BrushFrom("#E8F7EE");
                    Fg = BrushFrom("#15803D");
                    Emoji = "✅";
                    break;

                case DeviceStatus.Connecting:
                    Bg = BrushFrom("#FFF7E6");
                    Fg = BrushFrom("#B45309");
                    Emoji = "⏳";
                    break;

                default:
                    Bg = BrushFrom("#FEE2E2");
                    Fg = BrushFrom("#B91C1C");
                    Emoji = "❌";
                    break;
            }
        }

        private static Brush BrushFrom(string hex)
            => (Brush)new BrushConverter().ConvertFromString(hex)!;
    }
}

