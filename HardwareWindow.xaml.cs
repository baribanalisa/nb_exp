using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using MessageBox = System.Windows.MessageBox;

namespace NeuroBureau.Experiment;

public partial class HardwareWindow : Window
{
    private readonly string _path;
    private readonly ObservableCollection<SensorItem> _items = new();

    public HardwareWindow()
    {
        InitializeComponent();

        _path = TrackerPaths.FindExistingOrDefault("sensors.json");
        MessageBox.Show(_path, "Global sensors.json path");
        TrackerPaths.EnsureParentDir(_path);

        if (File.Exists(_path))
        {
            try
            {
                var file = JsonSerializer.Deserialize<SensorsListFile>(File.ReadAllText(_path));
                if (file?.List != null)
                    foreach (var s in file.List) _items.Add(s);
            }
            catch { /* ignore */ }
        }

        SensorsGrid.ItemsSource = _items;
    }

    private SensorItem? Selected => SensorsGrid.SelectedItem as SensorItem;

    private void GenUid_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        Selected.Uid = Guid.NewGuid().ToString();
        SensorsGrid.Items.Refresh();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        _items.Remove(Selected);
    }
    private static readonly JsonSerializerOptions InnerJsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private void ApplyShimmer_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;

        Selected.DevType = "ShimmerGSR";

        var bt = (ShimmerBtNameBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(bt))
        {
            MessageBox.Show("BT name пустой.", "Shimmer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse((ShimmerPortBox.Text ?? "").Trim(), out var port) || port <= 0 || port > 65535)
        {
            MessageBox.Show("Port должен быть числом 1..65535.", "Shimmer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Selected.Params = JsonSerializer.Serialize(new { name = bt, port = port }, InnerJsonOpts);
        if (string.IsNullOrWhiteSpace(Selected.Name))
            Selected.Name = bt;

        if (string.IsNullOrWhiteSpace(Selected.Uid))
            Selected.Uid = Guid.NewGuid().ToString();

        SensorsGrid.Items.Refresh();
    }
    private static readonly JsonSerializerOptions SensorsJsonOpts = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // helper (можно прямо внутри класса HardwareWindow)
    private static string NormalizeInnerJson(string s)
    {
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.GetRawText();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // минимальная валидация
        foreach (var s in _items)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) s.Name = s.DevType;
            if (string.IsNullOrWhiteSpace(s.Uid)) s.Uid = Guid.NewGuid().ToString();

            if (string.Equals(s.DevType, "ShimmerGSR", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(s.Params))
                {
                    MessageBox.Show("У ShimmerGSR обязателен params с {name,port}.", "Оборудование",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
        }

        var file = new SensorsListFile { List = _items.ToList() };

        foreach (var s in file.List)
        {
            if (!string.IsNullOrWhiteSpace(s.Params))
                s.Params = NormalizeInnerJson(s.Params);
        }

        var json = JsonSerializer.Serialize(file, SensorsJsonOpts);
        File.WriteAllText(_path, json, new UTF8Encoding(false));


        DialogResult = true;
        Close();
    }
}
