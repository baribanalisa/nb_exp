using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace NeuroBureau.Experiment;

public partial class BindSensorsWindow : Window
{
    private readonly string _expDir;
    private readonly string _bindPath;
    private readonly SensorsListFile _global;
    private readonly ObservableCollection<ExpDevVm> _expDevs = new();
    private readonly ObservableCollection<GlobalVm> _globalsFiltered = new();

    public BindSensorsWindow(string expDir, ExperimentFile exp)
    {
        InitializeComponent();
        _expDir = expDir;
        _bindPath = Path.Combine(expDir, "sensors.json");

        // global sensors
        var sensorsPath = TrackerPaths.FindExistingOrDefault("sensors.json");
        try
        {
            _global = JsonSerializer.Deserialize<SensorsListFile>(File.ReadAllText(sensorsPath)) ?? new SensorsListFile();
        }
        catch { _global = new SensorsListFile(); }

        // existing bind
        var bind = new ExpBindConfigFile();
        if (File.Exists(_bindPath))
        {
            try { bind = JsonSerializer.Deserialize<ExpBindConfigFile>(File.ReadAllText(_bindPath)) ?? new ExpBindConfigFile(); }
            catch { bind = new ExpBindConfigFile(); }
        }
        var map = bind.List.ToDictionary(x => x.Device, x => x.Name, StringComparer.Ordinal);

        foreach (var d in exp.Devices)
        {
            var devName = d.Name ?? "";
            if (string.IsNullOrWhiteSpace(devName)) devName = d.Uid;

            map.TryGetValue(devName, out var bound);

            _expDevs.Add(new ExpDevVm
            {
                Device = devName,
                DevType = d.DevType ?? "",
                Bound = bound
            });
        }

        ExpDevicesList.ItemsSource = _expDevs;
        GlobalSensorsList.ItemsSource = _globalsFiltered;
    }

    private ExpDevVm? SelectedExp => ExpDevicesList.SelectedItem as ExpDevVm;
    private GlobalVm? SelectedGlobal => GlobalSensorsList.SelectedItem as GlobalVm;

    private void ExpDevicesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _globalsFiltered.Clear();
        if (SelectedExp == null) return;

        var dt = SelectedExp.DevType ?? "";
        var list = _global.List
            .Where(s => string.Equals(s.DevType, dt, StringComparison.OrdinalIgnoreCase))
            .Select(s => new GlobalVm(s))
            .ToList();

        foreach (var g in list) _globalsFiltered.Add(g);
    }

    private void Bind_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedExp == null || SelectedGlobal == null) return;
        SelectedExp.Bound = SelectedGlobal.Name;
        ExpDevicesList.Items.Refresh();
    }

    private void Unbind_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedExp == null) return;
        SelectedExp.Bound = null;
        ExpDevicesList.Items.Refresh();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var file = new ExpBindConfigFile
        {
            List = _expDevs
                .Where(x => !string.IsNullOrWhiteSpace(x.Bound))
                .Select(x => new ExpBindItem { Device = x.Device, Name = x.Bound! })
                .ToList()
        };

        File.WriteAllText(_bindPath, JsonSerializer.Serialize(file));
        DialogResult = true;
        Close();
    }

    private sealed class ExpDevVm
    {
        public string Device { get; set; } = "";
        public string DevType { get; set; } = "";
        public string? Bound { get; set; }

        public string Display => $"{Device}  [{DevType}]   →   {(string.IsNullOrWhiteSpace(Bound) ? "—" : Bound)}";
    }

    private sealed class GlobalVm
    {
        public GlobalVm(SensorItem s) { Name = s.Name; DevType = s.DevType; }
        public string Name { get; }
        public string DevType { get; }
        public string Display => $"{Name}  ({DevType})";
    }
}
