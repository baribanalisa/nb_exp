using System;
using System.IO; // <--- ВАЖНО: нужно для Directory.Exists
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
namespace NeuroBureau.Experiment;

public partial class SettingsWindow : Window
{
    private enum CaptureTarget { None, Next, Exit }
    private CaptureTarget _capture = CaptureTarget.None;

    private Hotkey _nextHotkey;
    private Hotkey _exitHotkey;
    private int _writeDesktop;
    private bool _recordDesktop;
    private bool _recordCamera;
    private string? _cameraDeviceName;
    public bool RecordDesktop => _recordDesktop;
    public bool RecordCamera => _recordCamera;
    public string? CameraDeviceName => _cameraDeviceName;
    public string ExperimentsRoot => (ExperimentsPathBox.Text ?? "").Trim();
    public Hotkey NextHotkey => _nextHotkey;
    public Hotkey ExitHotkey => _exitHotkey;
    public int WriteDesktop => _writeDesktop;
    private bool _recordAudio;
    private string? _audioDeviceName;
    private readonly bool _allowFfmpegFromPath;

    public bool RecordAudio => _recordAudio;
    public string? AudioDeviceName => _audioDeviceName;

    private sealed class MonitorItem
    {
        public int Index { get; init; }
        public string Text { get; init; } = "";
        public override string ToString() => Text;
    }

    public SettingsWindow(
    string experimentsRoot,
    Hotkey nextHotkey,
    Hotkey exitHotkey,
    int writeDesktop,
    bool recordDesktop,
    bool recordCamera,
    string? cameraDeviceName,
    bool recordAudio,
    string? audioDeviceName,
    bool allowFfmpegFromPath)
    {
        InitializeComponent();

        ExperimentsPathBox.Text = experimentsRoot ?? "";

        _nextHotkey = nextHotkey;
        _exitHotkey = exitHotkey;
        _writeDesktop = writeDesktop;
        _recordDesktop = recordDesktop;
        _recordCamera = recordCamera;
        _cameraDeviceName = cameraDeviceName;

        _recordAudio = recordAudio;
        _audioDeviceName = audioDeviceName;
        _allowFfmpegFromPath = allowFfmpegFromPath;

        RecordDesktopCheck.IsChecked = _recordDesktop;
        RecordCameraCheck.IsChecked = _recordCamera;

        RecordAudioCheck.IsChecked = _recordAudio;
        AudioCombo.IsEnabled = _recordAudio;

        Loaded += SettingsWindow_Loaded;

        RefreshHotkeyTexts();
        LoadMonitors();
    }
    // === НОВЫЙ МЕТОД: Обработка кнопки "Обзор..." ===
    private void BrowseExperiments_Click(object sender, RoutedEventArgs e)
    {
        // Используем диалог из Windows Forms (он уже подключен в проект)
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        
        dialog.Description = "Выберите папку с экспериментами";
        dialog.UseDescriptionForTitle = true;
        dialog.ShowNewFolderButton = true;

        // Если в поле уже есть путь, пытаемся открыть диалог сразу на нем
        var currentPath = ExperimentsPathBox.Text.Trim();
        if (!string.IsNullOrEmpty(currentPath) && Directory.Exists(currentPath))
        {
            dialog.SelectedPath = currentPath;
        }

        // Открываем диалог (возвращает DialogResult.OK из WinForms)
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            ExperimentsPathBox.Text = dialog.SelectedPath;
        }
    }
    // ===============================================
    private void RecordAudioCheck_Changed(object sender, RoutedEventArgs e)
    {
        _recordAudio = RecordAudioCheck.IsChecked == true;
        AudioCombo.IsEnabled = _recordAudio;
    }

    private void RecordCameraCheck_Changed(object sender, RoutedEventArgs e)
    {
        _recordCamera = RecordCameraCheck.IsChecked == true;
        CameraCombo.IsEnabled = _recordCamera;
    }
    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        CameraCombo.IsEnabled = _recordCamera;

        var ffmpeg = CameraDeviceProvider.FindFfmpegExe(_allowFfmpegFromPath);
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            CameraHintText.Text = "ffmpeg не найден. Нужен локальный ffmpeg.exe рядом с приложением (ffmpeg.exe или ffmpeg\\ffmpeg.exe).";
            CameraHintText.Visibility = Visibility.Visible;

            _recordCamera = false;
            RecordCameraCheck.IsChecked = false;
            RecordCameraCheck.IsEnabled = false;
            CameraCombo.IsEnabled = false;
            return;
        }

        var devices = await CameraDeviceProvider.GetVideoDevicesAsync(ffmpeg);
        if (devices.Count == 0)
        {
            CameraHintText.Text = "Камеры не найдены (dshow список пуст).";
            CameraHintText.Visibility = Visibility.Visible;

            _recordCamera = false;
            RecordCameraCheck.IsChecked = false;
            CameraCombo.IsEnabled = false;
            return;
        }

        CameraCombo.ItemsSource = devices;

        if (!string.IsNullOrWhiteSpace(_cameraDeviceName))
        {
            var selected = devices.FirstOrDefault(d =>
                string.Equals(d.FriendlyName, _cameraDeviceName, StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(d =>
                    string.Equals(d.AlternativeName, _cameraDeviceName, StringComparison.OrdinalIgnoreCase));

            if (selected != null)
                CameraCombo.SelectedItem = selected;
            else
                CameraCombo.SelectedIndex = 0;
        }
        else
        {
            CameraCombo.SelectedIndex = 0;
        }

        CameraCombo.SelectionChanged += (_, _) =>
        {
            _cameraDeviceName = (CameraCombo.SelectedItem as CameraDeviceInfo)?.FriendlyName;
        };
        var aud = await CameraDeviceProvider.GetAudioDevicesAsync(ffmpeg);
        if (aud.Count == 0)
        {
            AudioHintText.Text = "Микрофоны не найдены (dshow audio список пуст).";
            AudioHintText.Visibility = Visibility.Visible;

            _recordAudio = false;
            RecordAudioCheck.IsChecked = false;
            RecordAudioCheck.IsEnabled = false;
            AudioCombo.IsEnabled = false;
            _audioDeviceName = null;
        }
        else
        {
            AudioCombo.ItemsSource = aud;

            if (!string.IsNullOrWhiteSpace(_audioDeviceName) && aud.Contains(_audioDeviceName))
                AudioCombo.SelectedItem = _audioDeviceName;
            else
            {
                // попробуем выбрать что-то похожее на Microphone по умолчанию
                var firstMic = aud.FirstOrDefault(x => x.Contains("Microphone", StringComparison.OrdinalIgnoreCase))
                            ?? aud.FirstOrDefault();
                AudioCombo.SelectedItem = firstMic;
            }

            AudioCombo.SelectionChanged += (_, _) =>
            {
                _audioDeviceName = AudioCombo.SelectedItem as string;
            };
        }

    }
    private void LoadMonitors()
    {
        var mons = MonitorService.GetMonitors();

        var items = mons.Select(m => new MonitorItem
        {
            Index = m.Index,
            Text = m.ToUiString()
        }).ToList();

        MonitorCombo.ItemsSource = items;

        // выбираем сохранённый индекс; если -1/невалидный — primary
        var selected = items.FirstOrDefault(x => x.Index == _writeDesktop);
        if (selected == null)
        {
            var prim = mons.FirstOrDefault(m => m.IsPrimary) ?? mons.FirstOrDefault();
            if (prim != null) selected = items.FirstOrDefault(x => x.Index == prim.Index);
        }

        MonitorCombo.SelectedItem = selected;

        MonitorCombo.SelectionChanged += (_, _) =>
        {
            if (MonitorCombo.SelectedItem is MonitorItem mi)
                _writeDesktop = mi.Index;
        };
    }

    private void RefreshHotkeyTexts()
    {
        NextHotkeyBtn.Content = HotkeyUtil.Format(_nextHotkey);
        ExitHotkeyBtn.Content = HotkeyUtil.Format(_exitHotkey);
        CaptureHint.Text = _capture == CaptureTarget.None ? "" : "Нажми сочетание клавиш…";
    }

    private void NextHotkey_Click(object sender, RoutedEventArgs e)
    {
        _capture = CaptureTarget.Next;
        CaptureHint.Text = "Нажми сочетание клавиш для «Следующий стимул»…";
        Focus();
    }

    private void ExitHotkey_Click(object sender, RoutedEventArgs e)
    {
        _capture = CaptureTarget.Exit;
        CaptureHint.Text = "Нажми сочетание клавиш для «Выход из эксперимента»…";
        Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capture == CaptureTarget.None) return;

        var key = (e.Key == Key.System) ? e.SystemKey : e.Key;

        if (HotkeyUtil.IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        var hk = new Hotkey(Keyboard.Modifiers, key);

        if (_capture == CaptureTarget.Next) _nextHotkey = hk;
        else if (_capture == CaptureTarget.Exit) _exitHotkey = hk;

        _capture = CaptureTarget.None;
        e.Handled = true;
        RefreshHotkeyTexts();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _recordDesktop = RecordDesktopCheck.IsChecked == true;
        _recordCamera = RecordCameraCheck.IsChecked == true;
        _recordAudio = RecordAudioCheck.IsChecked == true;

        _cameraDeviceName = CameraCombo.SelectedItem as string;
        _audioDeviceName = _recordAudio ? (AudioCombo.SelectedItem as string) : null;

        AppConfigManager.Save(
            ExperimentsRoot, _nextHotkey, _exitHotkey, _writeDesktop,
            _recordDesktop, _recordCamera, _cameraDeviceName,
            _recordAudio, _audioDeviceName
        );


        DialogResult = true;
        Close();
    }
}
