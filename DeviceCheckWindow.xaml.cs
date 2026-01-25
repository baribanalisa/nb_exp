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
    // было: MaxChartPoints=2000 и ChartWindowSeconds=60

    private int _chartHeadIndex;

    private const int MaxChartPoints = 5000;      // запас на высокую частоту
    private const double ChartWindowSeconds = 5.0; // окно по времени для графиков

    // Shimmer: батарея (0..100), -1 = неизвестно
    private int _lastBatteryPercent = -1;
    private int _lastBatteryDisplayed = -1;
    private DateTime _lastBatteryUiUpdate = DateTime.MinValue;

    // Поля для отслеживания взгляда

    private TrackerClient? _trackerClient;
    private double _lastGazeX = 0.5, _lastGazeY = 0.5;
    private DateTime _lastGazeUpdate = DateTime.MinValue;
    private CancellationTokenSource? _gazeMonitorCancellationTokenSource;

    // Закрытие окна: не блокируем UI-поток (иначе возможен дедлок при DisposeAsync)
    private int _closeCleanupStarted;
    private bool _allowCloseAfterCleanup;

    public DeviceCheckWindow(string expDir, ExperimentFile exp, CancellationToken externalCt)
{
    InitializeComponent();

    // Косметика для WPF на нестандартном DPI (125%/150%):
    // - меньше "мыла" на шрифтах
    // - ровнее границы/линии
    UseLayoutRounding = true;
    SnapsToDevicePixels = true;
    TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
    TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);
    RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);

    DataContext = Vm;

    _expDir = expDir;
    _exp = exp;
    _externalCt = externalCt;

    Loaded += async (_, __) => await RunChecksAsync();
    Closing += DeviceCheckWindow_Closing;
}


    private void DeviceCheckWindow_Closing(object? sender, CancelEventArgs e)
    {
        // если это повторное закрытие после cleanup — пропускаем без препятствий
        if (_allowCloseAfterCleanup)
            return;

        // всегда останавливаем мониторинг взгляда при закрытии окна
        try { _gazeMonitorCancellationTokenSource?.Cancel(); } catch { }
        try { _gazeMonitorCancellationTokenSource?.Dispose(); } catch { }
        _gazeMonitorCancellationTokenSource = null;

        // если окно закрыли через "Запустить" — клиентов оставляем наверх, но графики/подписки прибиваем
        if (_keepClients)
        {
            try { _cts?.Cancel(); } catch { }
            StopShimmerCharts();
            return;
        }

        // иначе: не даём WPF закрыть окно прямо сейчас, делаем cleanup и закрываем повторно
        e.Cancel = true;

        if (Interlocked.Exchange(ref _closeCleanupStarted, 1) != 0)
            return;

        IsEnabled = false;
        _ = CleanupAndCloseAsync();
    }

    private async Task CleanupAndCloseAsync()
    {
        try
        {
            try { _cts?.Cancel(); } catch { }

            // UI-часть (отписки/скрытие панелей) — на UI потоке
            StopShimmerCharts();

            // Shimmer: теперь DisposeAsync/StopAsync быстрый и гарантированно освобождает порт/BT,
            // поэтому просто дожидаемся корректного завершения (без гонок и “висящих” процессов).
            if (ShimmerClient != null)
            {
                var client = ShimmerClient;
                ShimmerClient = null;
                ShimmerDevice = null;

                try
                {
                    await client.DisposeAsync();
                }
                catch
                {
                    // ignore
                }
            }

        }
        finally
        {
            try
            {
                _allowCloseAfterCleanup = true;
                IsEnabled = true;
                Close(); // вызовет Closing повторно, но уже пропустим
            }
            catch { /* ignore */ }
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

    private async void Start_Click(object sender, RoutedEventArgs e)
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

                // важно: если пропускаем КГР — не блокируем закрытие окна
                _keepClients = true;

                if (ShimmerClient != null)
                {
                    var client = ShimmerClient;
                    ShimmerClient = null;
                    ShimmerDevice = null;
                    StopShimmerCharts();

                    try
                    {
                        await client.DisposeAsync();
                    }
                    catch
                    {
                        // ignore
                    }
                }
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
            _chartHeadIndex = 0;
            _lastBatteryPercent = -1;
        }

        _lastBatteryDisplayed = -1;
        _lastBatteryUiUpdate = DateTime.MinValue;

        ShimmerClient.DataReceived += OnShimmerDataReceived;

        Dispatcher.Invoke(() =>
        {
            ShimmerChartsPanel.Visibility = Visibility.Visible;
            ShimmerBatteryText.Text = "Батарея Shimmer: —";
            SrChart.Clear("Ожидание данных...");
            ScChart.Clear("Ожидание данных...");
            HrChart.Clear("Ожидание данных...");
            PpgChart.Clear("Ожидание данных...");
        });
    }



    private void StopShimmerCharts()
    {
        if (ShimmerClient != null)
        {
            ShimmerClient.DataReceived -= OnShimmerDataReceived;
        }

        lock (_chartDataLock)
        {
            _lastBatteryPercent = -1;
        }

        _lastBatteryDisplayed = -1;
        _lastBatteryUiUpdate = DateTime.MinValue;

        Dispatcher.Invoke(() =>
        {
            ShimmerBatteryText.Text = "Батарея Shimmer: —";
            ShimmerChartsPanel.Visibility = Visibility.Collapsed;
        });
    }


    private void OnShimmerDataReceived(ShimmerDataPoint data)
    {
        lock (_chartDataLock)
        {
            if (_chartStartTime == 0)
                _chartStartTime = data.Time;

            var t = data.Time - _chartStartTime; // секунды от старта стрима

            _srData.Add(new MetricPoint(t, data.SkinResistance));
            _scData.Add(new MetricPoint(t, data.SkinConductance));
            _hrData.Add(new MetricPoint(t, data.HeartRate));
            _ppgData.Add(new MetricPoint(t, data.Ppg));

            if (data.BatteryPercent >= 0 && data.BatteryPercent <= 100)
                _lastBatteryPercent = data.BatteryPercent;

            // 1) держим только последние ChartWindowSeconds по времени

            var cutoff = t - ChartWindowSeconds;
            while (_chartHeadIndex < _srData.Count && _srData[_chartHeadIndex].TimeSec < cutoff)
                _chartHeadIndex++;

            // 2) страховка по количеству (чтобы не разрасталось бесконечно)
            var liveCount = _srData.Count - _chartHeadIndex;
            if (liveCount > MaxChartPoints)
                _chartHeadIndex = _srData.Count - MaxChartPoints;

            // 3) иногда уплотняем списки (иначе они будут расти, даже если headIndex двигается)
            if (_chartHeadIndex > 5000 && _chartHeadIndex > _srData.Count / 2)
            {
                _srData.RemoveRange(0, _chartHeadIndex);
                _scData.RemoveRange(0, _chartHeadIndex);
                _hrData.RemoveRange(0, _chartHeadIndex);
                _ppgData.RemoveRange(0, _chartHeadIndex);
                _chartHeadIndex = 0;
            }
        }

        Dispatcher.InvokeAsync(UpdateCharts, System.Windows.Threading.DispatcherPriority.Background);
    }


        private void UpdateCharts()
    {
        List<MetricPoint> srCopy, scCopy, hrCopy, ppgCopy;
        double latestTime;
        int batteryPercent;

        lock (_chartDataLock)
        {
            var n = _srData.Count - _chartHeadIndex;
            if (n <= 0) return;

            srCopy = _srData.GetRange(_chartHeadIndex, n);
            scCopy = _scData.GetRange(_chartHeadIndex, n);
            hrCopy = _hrData.GetRange(_chartHeadIndex, n);
            ppgCopy = _ppgData.GetRange(_chartHeadIndex, n);

            latestTime = srCopy[srCopy.Count - 1].TimeSec;
            batteryPercent = _lastBatteryPercent;
        }

        var window = Math.Min(ChartWindowSeconds, latestTime);
        var tMinAbs = latestTime - window;

        // Важно: приводим X к диапазону 0..window (скользящее окно)
        static List<MetricPoint> ToWindow(List<MetricPoint> src, double tMinAbs)
        {
            var res = new List<MetricPoint>(src.Count);
            for (int i = 0; i < src.Count; i++)
            {
                var p = src[i];
                if (p.TimeSec < tMinAbs) continue;
                res.Add(new MetricPoint(p.TimeSec - tMinAbs, p.Value)); // X=0..window
            }
            return res;
        }

        var sr = ToWindow(srCopy, tMinAbs);
        var sc = ToWindow(scCopy, tMinAbs);
        var hr = ToWindow(hrCopy, tMinAbs);
        var ppg = ToWindow(ppgCopy, tMinAbs);

        double xMin = 0;
        double xMax = window;

        if (sr.Count > 0)
        {
            var min = sr.Min(p => p.Value);
            var max = sr.Max(p => p.Value);
            var range = Math.Max(1.0, max - min);
            SrChart.SetData(sr, xMin, xMax, min - range * 0.1, max + range * 0.1, double.NaN, "КГР: Сопротивление (SR)", null, "кОм");
        }

        if (sc.Count > 0)
        {
            var min = sc.Min(p => p.Value);
            var max = sc.Max(p => p.Value);
            var range = Math.Max(1.0, max - min);
            ScChart.SetData(sc, xMin, xMax, min - range * 0.1, max + range * 0.1, double.NaN, "КГР: Проводимость (SC)", null, "мкСм");
        }

        if (hr.Count > 0)
        {
            var min = Math.Max(0, hr.Min(p => p.Value));
            var max = Math.Min(200, hr.Max(p => p.Value));
            HrChart.SetData(hr, xMin, xMax, min - 10, max + 10, double.NaN, "Пульс (HR)", null, "уд/мин");
        }

        if (ppg.Count > 0)
        {
            var min = ppg.Min(p => p.Value);
            var max = ppg.Max(p => p.Value);
            var range = Math.Max(1.0, max - min);
            PpgChart.SetData(ppg, xMin, xMax, min - range * 0.1, max + range * 0.1, double.NaN, "Фотоплетизмограмма (PPG)", null, "усл.ед.");
        }

        // Батарея: обновляем не чаще, чем раз в 0.5с, чтобы не дергать WPF на каждом пакете
        var now = DateTime.UtcNow;
        int normalized = (batteryPercent >= 0 && batteryPercent <= 100) ? batteryPercent : -1;

        if (normalized != _lastBatteryDisplayed || (now - _lastBatteryUiUpdate).TotalMilliseconds >= 500)
        {
            ShimmerBatteryText.Text = normalized >= 0
                ? $"Батарея Shimmer: {normalized}%"
                : "Батарея Shimmer: —";

            _lastBatteryDisplayed = normalized;
            _lastBatteryUiUpdate = now;
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
