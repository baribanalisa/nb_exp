using System;
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
        if (_keepClients) return;

        _cts?.Cancel();

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
