using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using NeuroBureau.Experiment.Controls;
using MessageBox = System.Windows.MessageBox;
namespace NeuroBureau.Experiment;

public partial class DeviceCheckWindow : Window
{
    private readonly string _expDir;
    private readonly ExperimentFile _exp;
    private readonly CancellationToken _externalCt;
    private CancellationTokenSource? _cts;
    private bool _keepClients;

    public DeviceCheckVm Vm { get; } = new();

    // если Shimmer в эксперименте есть и коннект успешен — сюда положим готовый клиент
    public ShimmerGsrClient? ShimmerClient { get; private set; }
    public DeviceFile? ShimmerDevice { get; private set; }
    public bool SkipShimmer { get; private set; }
    
    private readonly object _chartDataLock = new();
    private readonly List<MetricPoint> _srData = new();
    private readonly List<MetricPoint> _scData = new();
    private readonly List<MetricPoint> _hrData = new();
    private readonly List<MetricPoint> _ppgData = new();
    private double _chartStartTime;
    private const int MaxChartPoints = 2000; // Увеличено для поддержки большего диапазона
    private const double ChartWindowSeconds = 60.0; // Увеличено до 60 секунд

    // Поля для отслеживания взгляда
    private TrackerClient? _trackerClient;
    private double _lastGazeX = 0.5, _lastGazeY = 0.5;
    private DateTime _lastGazeUpdate = DateTime.MinValue;
    private CancellationTokenSource? _gazeMonitorCancellationTokenSource;

    public DeviceCheckWindow(string expDir, ExperimentFile exp, CancellationToken externalCt)
    {
        InitializeComponent();
        DataContext = Vm;

        _expDir = expDir;
        _exp = exp;
        _externalCt = externalCt;

        Loaded += async (_, __) => await RunChecksAsync();
        Closing += DeviceCheckWindow_Closing;
    }

    private void DeviceCheckWindow_Closing(object? sender, CancelEventArgs e)
    {
        // если окно закрыли НЕ через "Запустить" — прибиваем то, что успели поднять
        if (_keepClients)
        {
            StopShimmerCharts();
            return;
        }

        _cts?.Cancel();
        StopShimmerCharts();
        
        // Останавливаем мониторинг взгляда
        _gazeMonitorCancellationTokenSource?.Cancel();
        _gazeMonitorCancellationTokenSource?.Dispose();

        if (ShimmerClient != null)
        {
            try { ShimmerClient.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* ignore */ }
            ShimmerClient = null;
            ShimmerDevice = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
        Close();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopShimmerCharts();
        await RunChecksAsync();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        // если в эксперименте есть ShimmerGSR и он не "ОК" — спрашиваем, запускать ли без него
        var shimmerDev = _exp.Devices.FirstOrDefault(d =>
            string.Equals(d.DevType, "ShimmerGSR", StringComparison.Ordinal));

        if (shimmerDev != null)
        {
            var row = Vm.FindRow(shimmerDev);

            if (!string.Equals(row.Status, "ОК", StringComparison.Ordinal))
            {
                var res = MessageBox.Show(
                    "КГР (Shimmer) не подключён.\nЗапустить эксперимент без КГР?",
                    "КГР не подключён",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);

                if (res != MessageBoxResult.OK)
                    return;

                SkipShimmer = true;

                // важно: если пропускаем КГР — НЕ сохраняем клиента и даём окну корректно всё прибить при закрытии
                _keepClients = false;
            }
            else
            {
                // КГР есть и ОК — сохраняем клиента наверх
                _keepClients = true;
            }
        }
        else
        {
            // КГР в эксперименте нет
            _keepClients = true;
        }

        DialogResult = true;
        Close();
    }


    private static bool IsEyeTrackerType(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return false;
        return t.Equals("PathFinder", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Gazepoint", StringComparison.OrdinalIgnoreCase)
            || t.Contains("tracker", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunChecksAsync()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _externalCt);
        var ct = linked.Token;

        StartBtn.IsEnabled = false;
        Vm.Devices.Clear();
        
        ShimmerChartsPanel.Visibility = Visibility.Collapsed;

        // 1) заполним список устройств как в exp.json
        foreach (var d in _exp.Devices)
        {
            var name = !string.IsNullOrWhiteSpace(d.Name) ? d.Name! : d.Uid;
            Vm.Devices.Add(new DeviceRow
            {
                Name = name,
                DevType = d.DevType ?? "",
                Status = "Ожидание",
                Details = ""
            });
        }

        // 2) проверка трекера (минимально: доступность HTTP 127.0.0.1:4242)
        var eye = _exp.Devices.FirstOrDefault(d => IsEyeTrackerType(d.DevType));
        if (eye != null)
        {
            var row = Vm.FindRow(eye);
            row.Status = "Проверка…";
            row.Details = "Проверяю доступность сервера трекера (HTTP)…";

            try
            {
                var tracker = new TrackerClient("http://127.0.0.1:4242");
                _ = await tracker.GetRecordsAsync(nRecords: 1, ackNumber: 0, ct: ct);
                row.Status = "ОК";
                row.Details = "Сервер трекера отвечает.";
                
                // Запускаем мониторинг взгляда если трекер доступен
                _trackerClient = tracker;
                _ = StartGazeMonitoring(ct); // Игнорируем предупреждение, так как это фоновая задача
            }
            catch (Exception ex)
            {
                row.Status = "Ошибка";
                row.Details = ShortErr(ex);
            }
            // ✅ "Старт" разрешаем сразу после проверки трекера.
// КГР не блокирует запуск.
            StartBtn.IsEnabled = (eye is null) || Vm.FindRow(eye).Status == "ОК";

        }

        // 3) проверка Shimmer (если он в списке устройств)
        var shimmerDev = _exp.Devices.FirstOrDefault(d => string.Equals(d.DevType, "ShimmerGSR", StringComparison.Ordinal));
        if (shimmerDev != null)
        {
            var row = Vm.FindRow(shimmerDev);
            row.Status = "Проверка…";
            row.Details = "Поднимаю Shimmer.exe и подключаюсь по BT…";

            // если при повторной проверке уже что-то было — прибьём
            if (ShimmerClient != null)
            {
                try { await ShimmerClient.DisposeAsync(); } catch { }
                ShimmerClient = null;
                ShimmerDevice = null;
            }

            try
            {
                var resolved = ResolveShimmer(shimmerDev); // btName/port/measParams
                var shimmerDir = Path.Combine(AppContext.BaseDirectory, "Shimmer");

                var client = new ShimmerGsrClient(resolved.btName, resolved.port, resolved.measParamsJson, shimmerDir);
                await client.ReadyAsync(ct);

                ShimmerClient = client;
                ShimmerDevice = shimmerDev;

                row.Status = "ОК";
                row.Details = $"Подключено: BT \"{resolved.btName}\", port {resolved.port}.";
                
                StartShimmerCharts();
            }
            catch (Exception ex)
            {
                row.Status = "Ошибка";
                row.Details = ShortErr(ex);

                // важное: если частично успели поднять — прибьём
                if (ShimmerClient != null)
                {
                    try { await ShimmerClient.DisposeAsync(); } catch { }
                    ShimmerClient = null;
                    ShimmerDevice = null;
                }
            }
        }

        // 4) всё остальное: “не проверяем автоматически”
        foreach (var d in _exp.Devices)
        {
            var t = d.DevType ?? "";
            if (IsEyeTrackerType(t) || string.Equals(t, "ShimmerGSR", StringComparison.Ordinal))
                continue;

            var row = Vm.FindRow(d);

            if (string.Equals(t, "MouseKeyboard", StringComparison.OrdinalIgnoreCase))
            {
                row.Status = "ОК";
                row.Details = "Локальное устройство (без подключения).";
            }
            else
            {
                row.Status = "—";
                row.Details = "Автопроверка не реализована для этого DevType.";
            }
        }

        // 5) разрешаем “Запустить”, если нет ошибок у трекера/шиммера (если они есть в эксперименте)
        bool eyeOk = (eye is null) || Vm.FindRow(eye).Status == "ОК";
        StartBtn.IsEnabled = eyeOk;
    }

    private (string btName, int port, string measParamsJson) ResolveShimmer(DeviceFile shimmerDev)
    {
        // глобальный sensors.json читается так же, как в HardwareWindow/BindSensorsWindow 
        var sensorsPath = TrackerPaths.FindExistingOrDefault("sensors.json");
        SensorsListFile globalSensors;
        try { globalSensors = JsonSerializer.Deserialize<SensorsListFile>(File.ReadAllText(sensorsPath)) ?? new SensorsListFile(); }
        catch { globalSensors = new SensorsListFile(); }

        // <exp>/sensors.json (биндинг) :contentReference[oaicite:3]{index=3}
        var bindPath = Path.Combine(_expDir, "sensors.json");
        ExpBindConfigFile bind = new();
        if (File.Exists(bindPath))
        {
            try { bind = JsonSerializer.Deserialize<ExpBindConfigFile>(File.ReadAllText(bindPath)) ?? new ExpBindConfigFile(); }
            catch { bind = new ExpBindConfigFile(); }
        }

        // В BindSensorsWindow ключ устройства = Name, иначе Uid :contentReference[oaicite:4]{index=4}
        var expDevKey = !string.IsNullOrWhiteSpace(shimmerDev.Name) ? shimmerDev.Name! : shimmerDev.Uid;

        var boundName = bind.List.FirstOrDefault(x =>
            string.Equals(x.Device, expDevKey, StringComparison.OrdinalIgnoreCase))?.Name;

        SensorItem? globalShimmer =
            (!string.IsNullOrWhiteSpace(boundName)
                ? globalSensors.List.FirstOrDefault(s =>
                    string.Equals(s.DevType, "ShimmerGSR", StringComparison.Ordinal) &&
                    string.Equals(s.Name, boundName, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? (!string.IsNullOrWhiteSpace(shimmerDev.Name)
                ? globalSensors.List.FirstOrDefault(s =>
                    string.Equals(s.DevType, "ShimmerGSR", StringComparison.Ordinal) &&
                    string.Equals(s.Name, shimmerDev.Name, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? globalSensors.List.FirstOrDefault(s => string.Equals(s.DevType, "ShimmerGSR", StringComparison.Ordinal));

        if (globalShimmer == null)
            throw new InvalidOperationException("ShimmerGSR есть в exp.json, но не найден в глобальном sensors.json.");

        if (string.IsNullOrWhiteSpace(globalShimmer.Params))
            throw new InvalidOperationException("У глобального ShimmerGSR нет params (нужны {name,port}).");

        string btName;
        int port;
        using (var doc = JsonDocument.Parse(globalShimmer.Params))
        {
            btName = doc.RootElement.GetProperty("name").GetString() ?? throw new InvalidOperationException("Shimmer params.name пустой");
            port = doc.RootElement.GetProperty("port").GetInt32();
        }

        if (port <= 0 || port > 65535)
            throw new InvalidOperationException($"Некорректный порт Shimmer: {port}");

        // meas params берём из exp.json device.params (как у тебя сейчас) :contentReference[oaicite:5]{index=5}
        var measParamsJson = shimmerDev.Params ?? "{}";

        return (btName, port, measParamsJson);
    }

    private static string ShortErr(Exception ex)
    {
        // чтобы не заливать окно простынёй
        var msg = ex.Message;
        if (string.IsNullOrWhiteSpace(msg)) msg = ex.GetType().Name;
        return msg;
    }

    private void StartShimmerCharts()
    {
        if (ShimmerClient == null) return;

        lock (_chartDataLock)
        {
            _srData.Clear();
            _scData.Clear();
            _hrData.Clear();
            _ppgData.Clear();
            _chartStartTime = 0;
        }

        ShimmerClient.DataReceived += OnShimmerDataReceived;

        Dispatcher.Invoke(() =>
        {
            ShimmerChartsPanel.Visibility = Visibility.Visible;
            SrChart.Clear("Ожидание данных...");
            ScChart.Clear("Ожидание данных...");
            HrChart.Clear("Ожидание данных...");
            PpgChart.Clear("Ожидание данных...");
            
            // Инициализируем превью взгляда - УДАЛЕНО
            //GazeStatus.Text = "Ожидание данных трекера...";
            //GazeCoords.Text = "X: -, Y: -";
            //GazePoint.Visibility = Visibility.Collapsed;
        });
    }

    private void StopShimmerCharts()
    {
        if (ShimmerClient != null)
        {
            ShimmerClient.DataReceived -= OnShimmerDataReceived;
        }
        
        Dispatcher.Invoke(() =>
        {
            ShimmerChartsPanel.Visibility = Visibility.Collapsed;
        });
    }

    private void OnShimmerDataReceived(ShimmerDataPoint data)
    {
        lock (_chartDataLock)
        {
            if (_chartStartTime == 0)
            {
                _chartStartTime = data.Time;
            }

            var relativeTime = data.Time - _chartStartTime;

            _srData.Add(new MetricPoint(relativeTime, data.SkinResistance));
            _scData.Add(new MetricPoint(relativeTime, data.SkinConductance));
            _hrData.Add(new MetricPoint(relativeTime, data.HeartRate));
            _ppgData.Add(new MetricPoint(relativeTime, data.Ppg));

            if (_srData.Count > MaxChartPoints)
            {
                _srData.RemoveAt(0);
                _scData.RemoveAt(0);
                _hrData.RemoveAt(0);
                _ppgData.RemoveAt(0);
            }
        }

        Dispatcher.InvokeAsync(() => UpdateCharts(), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UpdateCharts()
    {
        List<MetricPoint> srCopy, scCopy, hrCopy, ppgCopy;
        double tMin, tMax;

        lock (_chartDataLock)
        {
            if (_srData.Count == 0) return;

            srCopy = new List<MetricPoint>(_srData);
            scCopy = new List<MetricPoint>(_scData);
            hrCopy = new List<MetricPoint>(_hrData);
            ppgCopy = new List<MetricPoint>(_ppgData);

            var latestTime = _srData[_srData.Count - 1].TimeSec;
            tMin = Math.Max(0, latestTime - ChartWindowSeconds);
            tMax = latestTime;
        }

        var srFiltered = srCopy.Where(p => p.TimeSec >= tMin).ToList();
        var scFiltered = scCopy.Where(p => p.TimeSec >= tMin).ToList();
        var hrFiltered = hrCopy.Where(p => p.TimeSec >= tMin).ToList();
        var ppgFiltered = ppgCopy.Where(p => p.TimeSec >= tMin).ToList();

        if (srFiltered.Count > 0)
        {
            var srMin = srFiltered.Min(p => p.Value);
            var srMax = srFiltered.Max(p => p.Value);
            var srRange = Math.Max(1.0, srMax - srMin);
            SrChart.SetData(srFiltered, tMin, tMax, srMin - srRange * 0.1, srMax + srRange * 0.1, double.NaN, "КГР: Сопротивление (SR)", null, "кОм");
        }

        if (scFiltered.Count > 0)
        {
            var scMin = scFiltered.Min(p => p.Value);
            var scMax = scFiltered.Max(p => p.Value);
            var scRange = Math.Max(1.0, scMax - scMin);
            ScChart.SetData(scFiltered, tMin, tMax, scMin - scRange * 0.1, scMax + scRange * 0.1, double.NaN, "КГР: Проводимость (SC)", null, "мкСм");
        }

        if (hrFiltered.Count > 0)
        {
            var hrMin = Math.Max(0, hrFiltered.Min(p => p.Value));
            var hrMax = Math.Min(200, hrFiltered.Max(p => p.Value));
            HrChart.SetData(hrFiltered, tMin, tMax, hrMin - 10, hrMax + 10, double.NaN, "Пульс (HR)", null, "уд/мин");
        }

        if (ppgFiltered.Count > 0)
        {
            var ppgMin = ppgFiltered.Min(p => p.Value);
            var ppgMax = ppgFiltered.Max(p => p.Value);
            var ppgRange = Math.Max(1.0, ppgMax - ppgMin);
            PpgChart.SetData(ppgFiltered, tMin, tMax, ppgMin - ppgRange * 0.1, ppgMax + ppgRange * 0.1, double.NaN, "Фотоплетизмограмма (PPG)", null, "усл.ед.");
        }
    }
    
    private async Task StartGazeMonitoring(CancellationToken ct)
    {
        _gazeMonitorCancellationTokenSource?.Cancel();
        _gazeMonitorCancellationTokenSource?.Dispose();
        _gazeMonitorCancellationTokenSource = new CancellationTokenSource();
        
        var linkedCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _gazeMonitorCancellationTokenSource.Token).Token;
        
        // Обновляем UI статус - УДАЛЕНО
        //Dispatcher.BeginInvoke(() =>
        //{
        //    GazeStatus.Text = "Подключение к трекеру...";
        //});
        
        try
        {
            while (!linkedCt.IsCancellationRequested)
            {
                var records = await _trackerClient!.GetRecordsAsync(nRecords: 5, ackNumber: 0, ct: linkedCt);
                
                foreach (var record in records)
                {
                    if (record.Contains("<REC", StringComparison.OrdinalIgnoreCase))
                    {
                        ProcessGazeRecord(record);
                    }
                }
                
                await Task.Delay(100, linkedCt); // Обновляем 10 раз в секунду
            }
        }
        catch (OperationCanceledException)
        {
            // Ожидаемое поведение при отмене
        }
        catch (Exception ex)
        {
            // Обновляем UI при ошибке - УДАЛЕНО
            //Dispatcher.BeginInvoke(() =>
            //{
            //    GazeStatus.Text = $"Ошибка: {ShortErr(ex)}";
            //});
            System.Diagnostics.Debug.WriteLine($"Ошибка мониторинга взгляда: {ex.Message}");
        }
    }
    
    private void ProcessGazeRecord(string record)
    {
        try
        {
            // Парсим запись типа: <REC PO XN="0.123" YN="0.456" TIME="123456789" CNT="1"/>
            var timeStr = TrackerClient.TryGetAttr(record, "TIME");
            var xnStr = TrackerClient.TryGetAttr(record, "XN");
            var ynStr = TrackerClient.TryGetAttr(record, "YN");
            
            if (timeStr != null && xnStr != null && ynStr != null)
            {
                if (double.TryParse(xnStr, out var xn) && double.TryParse(ynStr, out var yn))
                {
                    // Обновляем последнюю позицию взгляда
                    _lastGazeX = Math.Max(0, Math.Min(1, xn)); // Нормализуем в диапазон 0-1
                    _lastGazeY = Math.Max(0, Math.Min(1, yn));
                    _lastGazeUpdate = DateTime.Now;
                    
                    // Обновляем UI - УДАЛЕНО
                    //Dispatcher.BeginInvoke(() =>
                    //{
                    //    // Позиционируем красную точку в превью
                    //    var pointX = _lastGazeX * 160 - 4; // Центрируем точку 8px
                    //    var pointY = _lastGazeY * 120 - 4;
                    //    
                    //    System.Windows.Controls.Canvas.SetLeft(GazePoint, pointX);
                    //    System.Windows.Controls.Canvas.SetTop(GazePoint, pointY);
                    //    GazePoint.Visibility = Visibility.Visible;
                    //    
                    //    // Обновляем информацию
                    //    GazeStatus.Text = "Трекер активен";
                    //    GazeCoords.Text = $"X: {_lastGazeX:F3}, Y: {_lastGazeY:F3}";
                    //});
                    
                    System.Diagnostics.Debug.WriteLine($"Взгляд: X={_lastGazeX:F3}, Y={_lastGazeY:F3}");
                }
            }
        }
        catch (Exception ex)
        {
            // Логируем ошибку, но не прерываем обработку
            System.Diagnostics.Debug.WriteLine($"Ошибка обработки записи взгляда: {ex.Message}");
        }
    }
}

public sealed class DeviceCheckVm
{
    public ObservableCollection<DeviceRow> Devices { get; } = new();

    public DeviceRow FindRow(DeviceFile d)
    {
        var key = !string.IsNullOrWhiteSpace(d.Name) ? d.Name! : d.Uid;
        return Devices.First(x => string.Equals(x.Name, key, StringComparison.Ordinal));
    }
}

public sealed class DeviceRow : INotifyPropertyChanged
{
    private string _status = "";
    private string _details = "";

    public string Name { get; init; } = "";
    public string DevType { get; init; } = "";

    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }
    public string Details { get => _details; set { _details = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}