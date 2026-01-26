using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Buffers.Binary;
using LibVLCSharp.Shared;
using System.Windows.Controls;
using System.Windows.Data;
using System.Data;
using System.ComponentModel;
using System.Windows.Interop;
// === ИСПРАВЛЕНИЕ КОНФЛИКТОВ (WPF vs WinForms) ===
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox; // Важно!
using Cursors = System.Windows.Input.Cursors; // Важно!
using Binding = System.Windows.Data.Binding;  // Важно!
using TextBox = System.Windows.Controls.TextBox; // Важно!
using Orientation = System.Windows.Controls.Orientation; // Важно!
using OpenFileDialog = Microsoft.Win32.OpenFileDialog; // Важно!
using ColorConverter = System.Windows.Media.ColorConverter; // Важно!

// Псевдонимы для WinForms (для палитры)
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
// ===============================================


namespace NeuroBureau.Experiment;

public partial class MainWindow : Window
{
    private TaskCompletionSource<bool>? _nextTcs;
    private CancellationTokenSource? _runCts;
    private bool _abortedByUser;
    // =======================
    // TEMP results (чтобы при "не сохранять" в папку эксперимента не писалось НИЧЕГО)
    // =======================
    private string? _pendingTempResultDir;
    private string? _pendingResultUid;
    private string? _pendingExpDir;
    private string? _pendingExpJsonPath;
    private ExperimentFile? _pendingExp;
    private ResultFile? _pendingResult;
    private LibVLC? _vlc;
    private LibVLCSharp.Shared.MediaPlayer? _vlcPlayer;

    private bool _vlcInited;
    private MainViewModel vm => (MainViewModel)DataContext;
    private Hotkey _hkNext = new(ModifierKeys.None, Key.Space);
    private Hotkey _hkExit = new(ModifierKeys.None, Key.Escape);
    private int _writeDesktop = -1; // primary
    private bool _recordDesktop;
    private bool _recordCamera;
    private string? _cameraDeviceName;
    private bool _recordAudio;
    private string? _audioDeviceName;

    private Task? _activeRunTask; // чтобы при закрытии дождаться завершения и закрытия файлов
    private HwndSource? _hwndSource;
    private const int HotkeyNextId = 0xA001;
    private const int HotkeyExitId = 0xA002;
    private const int WmHotkey = 0x0312;

    private void ForceNextStimulus() => _nextTcs?.TrySetResult(true);
    private bool _closeFlowInProgress;
    private bool _closeAllowed;
    private void AbortExperimentFromUi()
    {
        _abortedByUser = true;
        try { _runCts?.Cancel(); } catch { }
        try { _nextTcs?.TrySetCanceled(); } catch { }
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IDisposable? BeginGlobalHotkeys()
    {
        if (_hwndSource == null)
            return null;

        var hwnd = _hwndSource.Handle;
        if (hwnd == IntPtr.Zero)
            return null;

        UnregisterGlobalHotkeys();

        var nextVk = KeyInterop.VirtualKeyFromKey(_hkNext.Key);
        var exitVk = KeyInterop.VirtualKeyFromKey(_hkExit.Key);
        var nextMods = ToHotkeyModifiers(_hkNext.Modifiers);
        var exitMods = ToHotkeyModifiers(_hkExit.Modifiers);

        bool nextOk = nextVk != 0 && RegisterHotKey(hwnd, HotkeyNextId, nextMods, nextVk);
        bool exitOk = exitVk != 0 && RegisterHotKey(hwnd, HotkeyExitId, exitMods, exitVk);

        if (!nextOk && !exitOk)
            return null;

        return new HotkeyScope(this);
    }

    private void UnregisterGlobalHotkeys()
    {
        if (_hwndSource == null)
            return;

        var hwnd = _hwndSource.Handle;
        if (hwnd == IntPtr.Zero)
            return;

        try { UnregisterHotKey(hwnd, HotkeyNextId); } catch { }
        try { UnregisterHotKey(hwnd, HotkeyExitId); } catch { }
    }

    private static uint ToHotkeyModifiers(ModifierKeys mods)
    {
        uint res = 0;
        if (mods.HasFlag(ModifierKeys.Alt)) res |= 0x0001;
        if (mods.HasFlag(ModifierKeys.Control)) res |= 0x0002;
        if (mods.HasFlag(ModifierKeys.Shift)) res |= 0x0004;
        if (mods.HasFlag(ModifierKeys.Windows)) res |= 0x0008;
        return res;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            var id = wParam.ToInt32();
            if (id == HotkeyNextId)
            {
                ForceNextStimulus();
                handled = true;
            }
            else if (id == HotkeyExitId)
            {
                AbortExperimentFromUi();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private sealed class HotkeyScope : IDisposable
    {
        private MainWindow? _owner;

        public HotkeyScope(MainWindow owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_owner == null) return;
            _owner.UnregisterGlobalHotkeys();
            _owner = null;
        }
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var vm = (MainViewModel)DataContext;

        var w = new SettingsWindow(
        vm.ExperimentsRoot,
        _hkNext,
        _hkExit,
        _writeDesktop,
        _recordDesktop,
        _recordCamera,
        _cameraDeviceName,
        _recordAudio,          // ✅ ДОБАВИЛИ
        _audioDeviceName       // ✅ ДОБАВИЛИ
    )
        { Owner = this };

        if (w.ShowDialog() == true)
        {
            _hkNext = w.NextHotkey;
            _hkExit = w.ExitHotkey;
            _writeDesktop = w.WriteDesktop;

            _recordDesktop = w.RecordDesktop;
            _recordCamera = w.RecordCamera;
            _cameraDeviceName = w.CameraDeviceName;

            _recordAudio = w.RecordAudio;          // ✅ ДОБАВИЛИ
            _audioDeviceName = w.AudioDeviceName;  // ✅ ДОБАВИЛИ
        }

    }



   private void Results_Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (vm.SelectedExperiment == null) return;

        // чтобы последнее нажатие чекбокса не “зависло” в редакторе
        ResultsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ResultsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var selected = new List<SelectedResult>();

        foreach (DataRowView rv in vm.ResultsView)
        {
            if (rv["Select"] is bool b && b)
            {
                var uid = rv["ResultUid"]?.ToString();
                if (string.IsNullOrWhiteSpace(uid)) continue;

                // если колонки Color пока нет — rgba будет null (не упадём)
                string? rgba = null;
                if (rv.Row.Table.Columns.Contains("Color"))
                    rgba = rv["Color"]?.ToString();

                selected.Add(new SelectedResult(uid, rgba));
            }
        }

        if (selected.Count == 0)
        {
            MessageBox.Show("Выбери хотя бы один результат (поставь галочку в первом столбце).", "Анализ",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var expDir = vm.SelectedExperiment.ExpDir;

        ExperimentFile? exp = null;
        try
        {
            exp = JsonSerializer.Deserialize<ExperimentFile>(File.ReadAllText(vm.SelectedExperiment.ExpJsonPath));
        }
        catch { /* пока игнор */ }

        // ВАЖНО: чтобы это собралось, ниже нужно добавить перегрузку конструктора AnalysisWindow
        var w = new AnalysisWindow(expDir, selected, exp) { Owner = this };
        w.ShowDialog();
    }


    private void Results_SelectAll_Click(object sender, RoutedEventArgs e)
    {
        // чтобы последнее нажатие чекбокса не “зависло” в редакторе
        ResultsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ResultsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        foreach (DataRowView rv in vm.ResultsView)
            rv["Select"] = true;
    }

    private async void Results_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (vm.SelectedExperiment == null) return;

        ResultsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ResultsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var expUid = vm.SelectedExperiment.UidFolder;
        var expDir = vm.SelectedExperiment.ExpDir;

        var toDelete = new List<string>();

        foreach (DataRowView rv in vm.ResultsView)
        {
            if (rv["Select"] is bool b && b)
            {
                var uid = rv["ResultUid"]?.ToString();
                if (!string.IsNullOrWhiteSpace(uid))
                    toDelete.Add(uid);
            }
        }

        if (toDelete.Count == 0)
        {
            MessageBox.Show("Ничего не выбрано. Отметь чекбоксы в первом столбце (Select).",
                "Удаление результатов", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ok = MessageBox.Show(
            $"Удалить выбранные результаты: {toDelete.Count}?\n\nЭто удалит папки:\n{expUid}\\results\\<uid>",
            "Удаление результатов",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (ok != MessageBoxResult.Yes) return;

        int deleted = 0, failed = 0;

        foreach (var uid in toDelete)
        {
            try
            {
                var dir = Path.Combine(expDir, "results", uid);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                    deleted++;
                }
            }
            catch
            {
                failed++;
            }
        }

        vm.StatusText = failed == 0
            ? $"Удалено результатов: {deleted}"
            : $"Удалено: {deleted}, ошибок: {failed}";

        await vm.ReloadExperimentsAsync(selectUid: expUid);
    }

    private void ResultsGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        // 1) Скрываем тех.колонку
        if (string.Equals(e.PropertyName, "ResultUid", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            return;
        }

        // 2) По умолчанию — только чтение
        e.Column.IsReadOnly = true;

        // 3) Настройка колонок
        // В файле MainWindow.xaml.cs

        if (e.PropertyName == "Select")
        {
            var templateColumn = new DataGridTemplateColumn();
            templateColumn.Header = "";
            templateColumn.Width = 46;

            var factory = new FrameworkElementFactory(typeof(System.Windows.Controls.CheckBox));
            
            // === ИСПРАВЛЕНИЕ: Указываем полный путь System.Windows... ===
            // Чтобы компилятор не путал Enum со свойством окна
            factory.SetValue(System.Windows.Controls.CheckBox.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            factory.SetValue(System.Windows.Controls.CheckBox.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
            // ============================================================

            var binding = new Binding("Select");
            binding.UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged;
            factory.SetBinding(System.Windows.Controls.CheckBox.IsCheckedProperty, binding);

            templateColumn.CellTemplate = new DataTemplate { VisualTree = factory };
            e.Column = templateColumn;
            
            return;
        }

        if (e.PropertyName == "Color")
        {
            var column = new DataGridTemplateColumn
            {
                Header = "Цвет",
                Width = 140,
                IsReadOnly = false,
                CellTemplate = BuildResultColorTemplate(isEditing: false),
                CellEditingTemplate = BuildResultColorTemplate(isEditing: true),
                DisplayIndex = 1
            };

            e.Column = column;
            return;
        }

        if (e.PropertyName == "Date")
        {
            e.Column.Header = "Дата";
            e.Column.Width = 180;

            if (e.Column is DataGridTextColumn tc)
                tc.Binding.StringFormat = "dd.MM.yyyy HH:mm:ss";
            return;
        }

        if (e.PropertyName == "Duration")
        {
            e.Column.Header = "Длительность, с";
            e.Column.Width = 140;
            return;
        }
    }

    // Внутри MainWindow.xaml.cs

    // В файле MainWindow.xaml.cs

    private DataTemplate BuildResultColorTemplate(bool isEditing)
    {
        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        root.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);

        // Цветной квадратик
        var swatch = new FrameworkElementFactory(typeof(Border));
        swatch.SetValue(Border.WidthProperty, 16.0);
        swatch.SetValue(Border.HeightProperty, 16.0);
        swatch.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        swatch.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        swatch.SetValue(Border.BorderBrushProperty, Brushes.LightGray);
        swatch.SetValue(Border.MarginProperty, new Thickness(0, 0, 6, 0));

        // Биндинг фона
        swatch.SetBinding(Border.BackgroundProperty, new Binding("Color")
        {
            Converter = new RgbaToBrushConverter(),
            FallbackValue = Brushes.Transparent
        });

        // === ИСПРАВЛЕНИЕ: Делаем квадратик кликабельным ВСЕГДА ===
        // (Раньше это было внутри if (isEditing), теперь снаружи)
        
        swatch.SetValue(Border.CursorProperty, Cursors.Hand);
        
        // Добавляем обработчик. 
        // Важно: MouseLeftButtonDownEvent работает лучше, чем Click для Border
        swatch.AddHandler(Border.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnColorSwatchClick));
        
        // ========================================================

        root.AppendChild(swatch);

        FrameworkElementFactory textFactory;
        
        // Текстовое поле создаем только если это режим редактирования,
        // чтобы можно было вручную вбить HEX, если очень хочется.
        if (isEditing)
        {
            textFactory = new FrameworkElementFactory(typeof(TextBox));
            textFactory.SetValue(TextBox.MinWidthProperty, 80.0);
            textFactory.SetBinding(TextBox.TextProperty, new Binding("Color")
            {
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        }
        else
        {
            textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new Binding("Color"));
        }

        root.AppendChild(textFactory);

        var template = new DataTemplate { VisualTree = root };
        return template;
    }
    // Внутри MainWindow.xaml.cs

    // В файле MainWindow.xaml.cs

    private void OnColorSwatchClick(object sender, MouseButtonEventArgs e)
    {
        // 1. Останавливаем дальнейшую обработку клика таблицей (убираем конфликт с выделением)
        e.Handled = true;

        if (sender is not Border border || border.DataContext is not DataRowView row) 
            return;

        // Пытаемся получить текущий цвет
        string currentStr = row["Color"]?.ToString() ?? "";
        var winColor = Drawing.Color.Black; 

        try 
        {
            Color wpfColor = Colors.Black;
            if (currentStr.StartsWith("#"))
            {
                var c = (Color)ColorConverter.ConvertFromString(currentStr);
                wpfColor = c;
            }
            else
            {
                // fallback для формата "r g b a"
                var parts = currentStr.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    byte r = byte.Parse(parts[0]);
                    byte g = byte.Parse(parts[1]);
                    byte b = byte.Parse(parts[2]);
                    wpfColor = Color.FromRgb(r, g, b);
                }
            }
            winColor = Drawing.Color.FromArgb(wpfColor.A, wpfColor.R, wpfColor.G, wpfColor.B);
        }
        catch { }

        // Открываем диалог
        using var dialog = new WinForms.ColorDialog();
        dialog.Color = winColor;
        dialog.FullOpen = true;

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            var c = dialog.Color;
            string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            
            // Пишем в данные
            row["Color"] = hex;
            
            // Коммитим изменения, чтобы таблица сразу обновила картинку
            try { row.EndEdit(); } catch { }
            SaveResultColorToDisk(row["ResultUid"]?.ToString(), hex);
        }
    }
    private static string BuildDevicesText(ExperimentFile exp)
    {
        var parts = new List<string>();

        foreach (var d in exp.Devices)
        {
            var t = (d.DevType ?? "").Trim();

            if (t.Equals("MouseKeyboard", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add("Клавиатура/мышь");
            }
            else if (t.Equals("PathFinder", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("Gazepoint", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("tracker", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add("Айтрекер");
            }
            else if (!string.IsNullOrWhiteSpace(d.Name))
            {
                parts.Add(d.Name!);
            }
            else if (!string.IsNullOrWhiteSpace(t))
            {
                parts.Add(t);
            }
            else
            {
                parts.Add("Неизвестно");
            }
        }

        // убираем повторы, сохраняя порядок
        var uniq = parts.Distinct().ToList();
        return string.Join(" + ", uniq);
    }

    private void SetPendingTempResult(string tempResultDir, string resultUid, string expDir, string expJsonPath, ExperimentFile exp, ResultFile result)
    {
        _pendingTempResultDir = tempResultDir;
        _pendingResultUid = resultUid;
        _pendingExpDir = expDir;
        _pendingExpJsonPath = expJsonPath;
        _pendingExp = exp;
        _pendingResult = result;
    }


    private void ClearPendingTempResult()
    {
        _pendingTempResultDir = null;
        _pendingResultUid = null;
        _pendingExpDir = null;
        _pendingExpJsonPath = null;
        _pendingExp = null;
        _pendingResult = null;
    }

    private void DeletePendingTempResult()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_pendingTempResultDir) && Directory.Exists(_pendingTempResultDir))
                Directory.Delete(_pendingTempResultDir, recursive: true);
        }
        catch { /* можно игнорировать */ }

        ClearPendingTempResult();
    }

    private bool HasPendingTempResult()
    {
        return !string.IsNullOrWhiteSpace(_pendingTempResultDir)
            && Directory.Exists(_pendingTempResultDir!)
            && !string.IsNullOrWhiteSpace(_pendingResultUid)
            && !string.IsNullOrWhiteSpace(_pendingExpDir)
            && !string.IsNullOrWhiteSpace(_pendingExpJsonPath)
            && _pendingExp != null
            && _pendingResult != null;
    }

    /// <summary>
    /// Спрашивает: сохранить ли неполный результат. Если "Да" — коммитит temp->results.
    /// Если "Нет" — удаляет temp.
    /// Возвращает true, если сохранили.
    /// </summary>
    private bool PromptSavePendingResult(string reason)
    {
        if (!HasPendingTempResult())
            return false;

        var msg =
            "Эксперимент завершился досрочно.\n" +
            (string.IsNullOrWhiteSpace(reason) ? "" : $"Причина: {reason}\n") +
            "\nСохранить неполный результат?";

        var res = MessageBox.Show(
            this,
            msg,
            "Сохранить результат",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Yes)
        {
            try
            {
                CommitPendingTempResult();   // ниже добавим
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Save error", MessageBoxButton.OK, MessageBoxImage.Error);
                // если коммит не вышел — не удаляем автоматически, чтобы не потерять данные
                return false;
            }
        }

        DeletePendingTempResult();
        return false;
    }

    // Чтобы App.xaml.cs тоже мог спросить на краше
    internal void TryOfferSavePendingResult(string reason)
    {
        try { PromptSavePendingResult(reason); } catch { }
    }


    private static void MoveDirectoryRobust(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destDir)!);

        try
        {
            if (Directory.Exists(destDir))
                Directory.Delete(destDir, recursive: true);

            Directory.Move(sourceDir, destDir);
        }
        catch
        {
            CopyDirectory(sourceDir, destDir);
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    private void CommitPendingTempResult()
    {
        if (string.IsNullOrWhiteSpace(_pendingTempResultDir) ||
            string.IsNullOrWhiteSpace(_pendingResultUid) ||
            string.IsNullOrWhiteSpace(_pendingExpDir) ||
            string.IsNullOrWhiteSpace(_pendingExpJsonPath) ||
            _pendingExp == null ||
            _pendingResult == null)
            return;

        var resultsRoot = Path.Combine(_pendingExpDir, "results");
        Directory.CreateDirectory(resultsRoot);

        var finalResultDir = Path.Combine(resultsRoot, _pendingResultUid);

        // переносим ВСЮ папку результата (tracker.dat + per-stim файлы + mouse/keyboard)
        MoveDirectoryRobust(_pendingTempResultDir, finalResultDir);

        // пишем result.json уже в финальную папку
        var resultJsonPath = Path.Combine(finalResultDir, "result.json");
        var json = JsonSerializer.Serialize(_pendingResult, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(resultJsonPath, json, new UTF8Encoding(false));

        // обновляем mod-time эксперимента только при реальном сохранении
        _pendingExp.ModTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expJson = JsonSerializer.Serialize(_pendingExp, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(_pendingExpJsonPath, expJson, new UTF8Encoding(false));

        ClearPendingTempResult();
    }

    private Task CommitPendingTempResultAsync()
    {
        CommitPendingTempResult();
        return Task.CompletedTask;
    }

    private bool _isRunning;

    private string _experimentsRoot = "";
    private List<ExperimentListItem> _items = new();

    // для восстановления окна после run-mode
    private WindowStyle _prevStyle;
    private WindowState _prevState;
    private bool _prevTopmost;
    private Brush? _prevBg;
    // =======================
    // Mouse/Keyboard device logging (как в Vala MouseKbdData)
    // Запись 48 байт:
    // double time_sec; uint32 mouse_button; uint32 keyboard_code; double x; double y; int32 reserved[4]
    // =======================
    private readonly object _mkLock = new();
    private FileStream? _mkPerStimStream;
    private Stopwatch? _mkStopwatch;

    private double MkNowSec => _mkStopwatch?.Elapsed.TotalSeconds ?? 0.0;

    private void MkSetStream(FileStream? fs)
    {
        lock (_mkLock) _mkPerStimStream = fs;
    }

    private void MkWrite(double timeSec, uint mouseButton, uint keyboardCode, double x, double y)
    {
        // 48 bytes record
        Span<byte> buf = stackalloc byte[48];
        int o = 0;

        BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(o, 8), BitConverter.DoubleToInt64Bits(timeSec)); o += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(o, 4), mouseButton); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(o, 4), keyboardCode); o += 4;
        BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(o, 8), BitConverter.DoubleToInt64Bits(x)); o += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(o, 8), BitConverter.DoubleToInt64Bits(y)); o += 8;

        buf.Slice(o, 16).Clear(); // reserved[4] = 0

        lock (_mkLock)
        {
            // пишем только если сейчас есть активный per-stimulus stream
            _mkPerStimStream?.Write(buf);
        }
    }

    // маркер "сменился стимул" — пустая запись (как в Vala stimul_changed)
    private void MkMarker() => MkWrite(MkNowSec, 0, 0, 0, 0);

    private static uint MkMouseBtn(MouseButton b) => b switch
    {
        MouseButton.Left => 1u,
        MouseButton.Middle => 2u,
        MouseButton.Right => 3u,
        _ => 0u
    };

    private (double x, double y) MkNormPos(Point p)
    {
        double w = RunLayer.ActualWidth;
        double h = RunLayer.ActualHeight;

        double x = 0, y = 0;
        if (w > 0 && p.X >= 0 && p.X <= w) x = p.X / w;
        if (h > 0 && p.Y >= 0 && p.Y <= h) y = p.Y / h;

        return (x, y);
    }

    private void MkLogKeyDown(KeyEventArgs e)
    {
        if (_mkStopwatch == null) return;

        var k = (e.Key == Key.System) ? e.SystemKey : e.Key;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(k); // совпадает с 37/39 для стрелок

        MkWrite(MkNowSec, 0, vk, 0, 0);
    }

    private void MkLogKeyUp(KeyEventArgs e)
    {
        if (_mkStopwatch == null) return;

        // как в Vala: release пишется с keyboard_code = 0
        MkWrite(MkNowSec, 0, 0, 0, 0);
    }

    private void MkLogMouseDown(MouseButtonEventArgs e)
    {
        if (_mkStopwatch == null) return;

        var (x, y) = MkNormPos(e.GetPosition(RunLayer));
        MkWrite(MkNowSec, MkMouseBtn(e.ChangedButton), 0, x, y);
    }

    private void MkLogMouseUp(MouseButtonEventArgs e)
    {
        if (_mkStopwatch == null) return;

        // как в Vala: release пишется с mouse_button = 0, координаты сохраняем
        var (x, y) = MkNormPos(e.GetPosition(RunLayer));
        MkWrite(MkNowSec, 0, 0, x, y);
    }
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        Core.Initialize(); // можно один раз
                           // EnsureVlc();
        var cfg = AppConfigManager.Load();
        _recordDesktop = cfg.RecordDesktop;
        _recordCamera = cfg.RecordCamera;
        _cameraDeviceName = cfg.CameraDeviceName;
        _recordAudio = cfg.RecordAudio;
        _audioDeviceName = cfg.AudioDeviceName;

        _writeDesktop = cfg.WriteDesktop;

        _hkNext = cfg.NextHotkey;
        _hkExit = cfg.ExitHotkey;

        PreviewKeyUp += Window_KeyUp;
        RunLayer.PreviewMouseDown += RunLayer_PreviewMouseDown;
        RunLayer.PreviewMouseUp += RunLayer_PreviewMouseUp;

        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;

        // чтобы при старте не показался “старый кадр”
        StimImage.Source = null;
        StimImage.Visibility = Visibility.Collapsed;

    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WndProc);
    }
    private bool _isShuttingDown;

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        // если уже прошли диалог и разрешили закрытие — не мешаем
        if (_closeAllowed)
        {
            _isShuttingDown = true;
            try { StopVideoUi(); } catch { }
            return;
        }

        // если эксперимент не идёт — закрываем как обычно
        if (!_isRunning)
        {
            _isShuttingDown = true;
            try { StopVideoUi(); } catch { }
            return;
        }

        // эксперимент идёт: останавливаем закрытие и запускаем flow
        e.Cancel = true;

        if (_closeFlowInProgress) return;
        _closeFlowInProgress = true;

        _ = CloseWithSavePromptAsync();
    }

    private async Task CloseWithSavePromptAsync()
    {
        try { _runCts?.Cancel(); } catch { }
        try { _nextTcs?.TrySetCanceled(); } catch { }
        try { StopVideoUi(); } catch { }

        var t = _activeRunTask;
        if (t != null)
        {
            try
            {
                // ВАЖНО: не ждём бесконечно, иначе окно никогда не закроется,
                // а процесс будет держать exe и ломать последующую сборку.
                await Task.WhenAny(t, Task.Delay(2000));
            }
            catch { }
        }

        ExitRunMode();
        PromptSavePendingResult("Выход из приложения");

        _closeAllowed = true;

        // Повторная попытка закрыть окно (теперь MainWindow_Closing пропустит)
        try { Close(); } catch { }
    }



    // Dispose лучше делать в Closed: меньше шанс гонок с VLC-ивентами
    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        try { DisposeVlc(); } catch { }
    }

    private void DisposeVlc()
    {
        if (!_vlcInited) return;

        // отвязать VideoView от плеера (важно)
        try { StimVlc.MediaPlayer = null; } catch { }

        try { _vlcPlayer?.Stop(); } catch { }
        try { _vlcPlayer?.Dispose(); } catch { }
        _vlcPlayer = null;

        try { _vlc?.Dispose(); } catch { }
        _vlc = null;

        _vlcInited = false;
    }

    private void EnsureVlc()
    {
        if (_vlcInited) return;

        _vlc = new LibVLC("--no-video-title-show", "--quiet");
        _vlcPlayer = new LibVLCSharp.Shared.MediaPlayer(_vlc)
        {
            Volume = 100
        };


        StimVlc.MediaPlayer = _vlcPlayer;
        _vlcInited = true;
    }

    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        if (!_isRunning) return;
        MkLogKeyUp(e);
    }

    private void RunLayer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isRunning) return;
        MkLogMouseDown(e);
    }

    private void RunLayer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isRunning) return;
        MkLogMouseUp(e);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = (MainViewModel)DataContext;
            _experimentsRoot = vm.ExperimentsRoot;  // чтобы твой импорт/запуск не ломать
           // await vm.ReloadExperimentsAsync(selectUid: null);

        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Init error", MessageBoxButton.OK, MessageBoxImage.Error);
            ((MainViewModel)DataContext).StatusText = "Ошибка инициализации";
        }
    }

        private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (ExpList.SelectedItem is not ExperimentListItem item) return;

        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт эксперимента (.tar.gz)",
            Filter = "Experiment archive (*.tar.gz)|*.tar.gz|All|*.*",
            FileName = $"{item.UidFolder}.tar.gz",
            AddExtension = true,
            DefaultExt = ".tar.gz",
            OverwritePrompt = true
        };

        if (sfd.ShowDialog(this) != true) return;

        var vm = (MainViewModel)DataContext;

        string tempRoot = Path.Combine(Path.GetTempPath(), "nb_export_" + Guid.NewGuid().ToString("N"));
        try
        {
            vm.StatusText = "Экспорт…";

            // Чтобы внутри архива был корневой каталог эксперимента (uid/exp.json, ...),
            // упаковываем не напрямую item.ExpDir, а временную структуру tempRoot/uid/*
            var tempExpDir = Path.Combine(tempRoot, item.UidFolder);
            CopyDirectory(item.ExpDir, tempExpDir);

            ExperimentLoader.RepackTarGz(tempRoot, sfd.FileName, makeBackup: false);

            vm.StatusText = $"Экспорт завершён: {Path.GetFileName(sfd.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Export error", MessageBoxButton.OK, MessageBoxImage.Error);
            vm.StatusText = "Экспорт: ошибка";
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
            await Task.CompletedTask;
        }
    }

    private void MultiExport_Click(object sender, RoutedEventArgs e)
    {
        if (ExpList.SelectedItem is not ExperimentListItem item) return;

        var vm = (MainViewModel)DataContext;

        var selected = new List<string>();
        try
        {
            foreach (DataRowView rv in vm.ResultsView)
            {
                if (rv["Select"] is bool b && b)
                {
                    var uid = rv["ResultUid"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(uid))
                        selected.Add(uid);
                }
            }
        }
        catch
        {
            // если по каким-то причинам таблица результатов ещё не готова — просто откроем окно без предвыбора
            selected.Clear();
        }

        var w = new MultiExportWindow(item.ExpDir, selected)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        w.ShowDialog();
    }

    private static void CopyDirectory(string sourceDir, string targetDir)

    {
        Directory.CreateDirectory(targetDir);

        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(targetDir, rel));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dst = Path.Combine(targetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }



    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRunning)
            return; // <-- НЕ закрываем приложение по Esc на экране выбора

        MkLogKeyDown(e);

        if (e.Key == Key.Escape)
        {
            _abortedByUser = true;
            _runCts?.Cancel();
            _nextTcs?.TrySetCanceled();
            return;
        }

        if (e.Key == Key.Space || e.Key == Key.Enter)
            _nextTcs?.TrySetResult(true);
    }


    private void Hardware_Click(object sender, RoutedEventArgs e)
    {
        var w = new HardwareWindow { Owner = this };
        w.ShowDialog();
    }

    private void BindSensors_Click(object sender, RoutedEventArgs e)
    {
        if (ExpList.SelectedItem is not ExperimentListItem item) return;

        var exp = JsonSerializer.Deserialize<ExperimentFile>(File.ReadAllText(item.ExpJsonPath))
                ?? throw new InvalidOperationException("Failed to parse exp.json");

        var w = new BindSensorsWindow(item.ExpDir, exp) { Owner = this };
        w.ShowDialog();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        await vm.ReloadExperimentsAsync(selectUid: (ExpList.SelectedItem as ExperimentListItem)?.UidFolder);
        
        // Принудительно проверяем подключение трекера при обновлении
        await vm.RefreshEyeTrackerConnectionAsync();
    }


    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Импорт эксперимента (.tar.gz)",
            Filter = "Experiment archive (*.tar.gz)|*.tar.gz|All|*.*",
            CheckFileExists = true
        };
        if (ofd.ShowDialog(this) != true) return;

        var vm = (MainViewModel)DataContext; // ✅ ДО try

        try
        {
            vm.StatusText = "Импорт…";
            var destDir = ExperimentLoader.ImportTarGzIntoExperiments(ofd.FileName, _experimentsRoot);
            var uid = Path.GetFileName(destDir);
            await vm.ReloadExperimentsAsync(selectUid: uid);
            vm.StatusText = $"Импорт завершён: {uid}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Import error", MessageBoxButton.OK, MessageBoxImage.Error);
            vm.StatusText = "Импорт: ошибка";
        }
    }


    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (ExpList.SelectedItem is not ExperimentListItem item) return;
        if (DataContext is not MainViewModel vm) return;

        // 1) трекер обязателен
        if (!vm.IsEyeTrackerConnected)
        {
            MessageBox.Show("Айтрекер не подключён. Запуск невозможен.",
                "Нет связи с айтрекером",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        // 2) остальные — предупреждение
        var missing = vm.GetMissingOptionalDevices(); // сделаем ниже
        if (missing.Count > 0)
        {
            var msg = "Не все устройства подключены:\n" +
                    string.Join("\n", missing.Select(x => "• " + x)) +
                    "\n\nЗапустить эксперимент всё равно?";

            var res = MessageBox.Show(msg,
                "Не все устройства подключены",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.OK)
                return;
        }
        _abortedByUser = false;
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();

        ShimmerGsrClient? preShimmer = null;
        DeviceFile? preShimmerDev = null;

        try
        {
            // загрузим exp заранее, чтобы окно проверки знало список устройств
            var exp = JsonSerializer.Deserialize<ExperimentFile>(File.ReadAllText(item.ExpJsonPath))
                    ?? throw new InvalidOperationException("Failed to parse exp.json");

            // 1) окно статусов (коннектит Shimmer и проверяет трекер)
            var check = new DeviceCheckWindow(item.ExpDir, exp, _runCts.Token) { Owner = this };
            if (check.ShowDialog() != true)
            {
                vm.StatusText = "Запуск отменён";
                return;
            }
            var skipShimmer = check.SkipShimmer;

            preShimmer = check.ShimmerClient;
            preShimmerDev = check.ShimmerDevice;

            // 2) запускаем эксперимент, передав уже подключенный shimmer
            _activeRunTask = RunExperimentAsync(item.ExpDir, _runCts.Token, preShimmer, preShimmerDev, skipShimmer);
            await _activeRunTask;


            await ((MainViewModel)DataContext).ReloadExperimentsAsync(selectUid: item.UidFolder);
            vm.StatusText = "Готово: результат добавлен";
        }
        catch (OperationCanceledException)
        {
            ExitRunMode();
            var saved = PromptSavePendingResult(_abortedByUser ? "Остановлено пользователем" : "Отмена/выход");
            vm.StatusText = saved ? "Сохранён неполный результат" : "Остановлено";
            await ((MainViewModel)DataContext).ReloadExperimentsAsync(selectUid: item.UidFolder);
        }
        catch (Exception ex)
        {
            ExitRunMode();
            var msg = ex.Message;
            if (ex is FileNotFoundException or DirectoryNotFoundException)
                msg = $"Не найден стимул.\n\n{ex.Message}";
            MessageBox.Show(msg, "Experiment error", MessageBoxButton.OK, MessageBoxImage.Error);

            var saved = PromptSavePendingResult("Ошибка во время эксперимента");
            vm.StatusText = saved ? "Сохранён неполный результат (после ошибки)" : "Ошибка выполнения";

            await ((MainViewModel)DataContext).ReloadExperimentsAsync(selectUid: item.UidFolder);
        }
        finally
        {
            ExitRunMode();
            _runCts?.Dispose();
            _runCts = null;
            _activeRunTask = null;
            // ВАЖНО: если RunExperimentAsync не дошёл до finally (например, участника отменили),
            // чтобы не оставлять Shimmer.exe висеть.
            // RunExperimentAsync ниже я тоже предлагаю подстраховать.
        }
    }


    private void ExpList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ExpList.SelectedItem is not ExperimentListItem item)
        {
            RunBtn.IsEnabled = false;
            BindBtn.IsEnabled = false;
            ExpNameText.Text = "Выберите эксперимент слева";
            ExpDescrText.Text = "";
            ExpPathText.Text = "";
            ExpMetaText.Text = "";
            return;
        }

        RunBtn.IsEnabled = true;
        BindBtn.IsEnabled = true;
        ExpNameText.Text = item.Name ?? item.UidFolder;
        ExpDescrText.Text = item.Description ?? "";
        ExpPathText.Text = $"Experiments root:\n{_experimentsRoot}\n\nExperiment folder:\n{item.ExpDir}";
        ExpMetaText.Text = $"Результатов: {item.ResultsCount} | mod-time: {item.ModTime} | create-time: {item.CreateTime}";
        ExpMetaText.Text =
            $"Устройства: {item.DevicesText} | Результатов: {item.ResultsCount} | mod-time: {item.ModTime} | create-time: {item.CreateTime}";

    }

    private void ExpList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RunBtn.IsEnabled)
            Run_Click(sender, e);
    }

    private async Task ReloadExperimentsAsync(string? selectUid)
    {
        Directory.CreateDirectory(_experimentsRoot);
        vm.StatusText = $"Папка Experiments: {_experimentsRoot}";

        _items = await Task.Run(() => ScanExperiments(_experimentsRoot));

        ExpList.ItemsSource = _items;

        if (!string.IsNullOrWhiteSpace(selectUid))
        {
            var found = _items.FirstOrDefault(x => string.Equals(x.UidFolder, selectUid, StringComparison.OrdinalIgnoreCase));
            if (found != null)
                ExpList.SelectedItem = found;
        }

        if (ExpList.SelectedItem == null && _items.Count > 0)
            ExpList.SelectedIndex = 0;
    }

    private static List<ExperimentListItem> ScanExperiments(string root)
    {
        var list = new List<ExperimentListItem>();

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var expJson = Path.Combine(dir, "exp.json");
            if (!File.Exists(expJson)) continue; // важно: как в старом модуле — exp.json должен быть в корне папки

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
                    DevicesText = BuildDevicesText(exp)
                });
            }
            catch
            {
                // битый exp.json — пропускаем
            }
        }

        // похоже на поведение пользователя: удобнее видеть свежие сверху
        return list.OrderByDescending(x => x.ModTime).ToList();
    }

    private void EnterRunMode()
    {
        if (_isRunning) return;
        _isRunning = true;

        _prevStyle = WindowStyle;
        _prevState = WindowState;
        _prevTopmost = Topmost;
        _prevBg = Background;

        SelectLayer.Visibility = Visibility.Collapsed;
        RunLayer.Visibility = Visibility.Visible;

        Background = Brushes.Black;
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        Topmost = true;

        Activate();
        Focus();
        ShowText("Подготовка…");
    }

    private void ExitRunMode()
    {
        if (!_isRunning) return;

        Topmost = _prevTopmost;
        WindowStyle = _prevStyle;
        WindowState = _prevState;
        Background = _prevBg ?? Brushes.White;

        RunLayer.Visibility = Visibility.Collapsed;
        SelectLayer.Visibility = Visibility.Visible;

        _isRunning = false;
    }
    private static void PermutateGroupsLikeVala(List<StimulFile> stimuls, Random rng)
    {
        // собираем пулы по random-group (калибровки kind=0 не трогаем)
        var pools = new Dictionary<string, List<StimulFile>>(StringComparer.Ordinal);

        foreach (var st in stimuls)
        {
            var kind = st.Kind ?? 0;
            if (kind == 0) continue;

            var g = st.RandomGroup;
            if (string.IsNullOrWhiteSpace(g)) continue;

            if (!pools.TryGetValue(g, out var list))
                pools[g] = list = new List<StimulFile>();

            list.Add(st);
        }

        if (pools.Count == 0) return;

        // перемешиваем каждый пул (Fisher–Yates)
        foreach (var kv in pools)
        {
            var list = kv.Value;
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        // подставляем элементы в исходные позиции группы (как в Vala по смыслу)
        var cursor = pools.ToDictionary(k => k.Key, _ => 0);

        for (int i = 0; i < stimuls.Count; i++)
        {
            var st = stimuls[i];
            var kind = st.Kind ?? 0;
            if (kind == 0) continue;

            var g = st.RandomGroup;
            if (string.IsNullOrWhiteSpace(g)) continue;

            var pool = pools[g];
            int c = cursor[g];
            if (c >= pool.Count) continue;

            stimuls[i] = pool[c];
            cursor[g] = c + 1;
        }
    }

    private async Task RunExperimentAsync(string expDir, CancellationToken ct, ShimmerGsrClient? preShimmer, DeviceFile? preShimmerDev, bool skipShimmer)
    {
        if (!Directory.Exists(expDir))
            throw new DirectoryNotFoundException(expDir);

        var expJsonPath = Path.Combine(expDir, "exp.json");
        var exp = JsonSerializer.Deserialize<ExperimentFile>(await File.ReadAllTextAsync(expJsonPath, ct))
                ?? throw new InvalidOperationException("Failed to parse exp.json");

        if (exp.Stimuls.Count == 0)
            throw new InvalidOperationException("No stimuls in exp.json");

        // эксперимент обязан начинаться с калибровки
        if ((exp.Stimuls[0].Kind ?? 0) != 0)
            throw new InvalidOperationException("Эксперимент должен начинаться с калибровки (kind=0 или отсутствует).");

        if (exp.Devices.Count == 0)
            throw new InvalidOperationException("No devices in exp.json");

        var stimulsRun = exp.Stimuls.ToList();
        PermutateGroupsLikeVala(stimulsRun, Random.Shared);


        // eye-tracker uid (как раньше)
        static bool IsEyeTrackerType(string? t)
        {
            if (string.IsNullOrWhiteSpace(t)) return false;
            return t.Equals("PathFinder", StringComparison.OrdinalIgnoreCase)
                || t.Equals("Gazepoint", StringComparison.OrdinalIgnoreCase)
                || t.Contains("tracker", StringComparison.OrdinalIgnoreCase);
        }

        var trackerUid =
            exp.Devices.FirstOrDefault(d => IsEyeTrackerType(d.DevType))?.Uid
            ?? throw new InvalidOperationException("В exp.json не найден ай-трекер (dev-type PathFinder/Gazepoint/...).");


        // keyboard/mouse uid (если есть)
        var mouseKbdUid =
            exp.Devices.FirstOrDefault(d => string.Equals(d.DevType, "MouseKeyboard", StringComparison.OrdinalIgnoreCase))?.Uid;

        // диалог участника
        var dlg = new ParticipantDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // ✅ пока эксперимент идёт — пишем ВСЁ во временную папку
        var resultUid = Guid.NewGuid().ToString();

        var tempRoot = Path.Combine(Path.GetTempPath(), "NeuroBureau.Experiment", "pending-results");
        Directory.CreateDirectory(tempRoot);

        var resultDir = Path.Combine(tempRoot, resultUid);
        Directory.CreateDirectory(resultDir);

        FfmpegRecorder? deskRec = null;
        FfmpegRecorder? camRec = null;

        // ffmpeg нужен и для глобальной записи (desktop/camera), и для per-stim SCREEN_RECORD
        string? ffmpegExe = null;
        bool screenRecordFfmpegWarned = false;

        bool needFfmpeg =
            _recordDesktop ||
            _recordCamera ||
            stimulsRun.Any(s => (s.Kind ?? 0) == StimulusKinds.ScreenRecord);

        if (needFfmpeg)
        {
            ffmpegExe = CameraDeviceProvider.FindFfmpegExe();
            if (string.IsNullOrWhiteSpace(ffmpegExe))
            {
                MessageBox.Show(
                    "Для записи (включена запись экрана/камеры и/или есть стимулы SCREEN_RECORD) требуется ffmpeg, но он не найден.\n" +
                    "Положи ffmpeg.exe рядом с приложением или добавь в PATH.\n" +
                    "Запись будет пропущена.",
                    "Запись", MessageBoxButton.OK, MessageBoxImage.Warning);

                // чтобы не спамить предупреждениями внутри цикла SCREEN_RECORD
                screenRecordFfmpegWarned = true;
            }
        }

        if (_recordDesktop || _recordCamera)
        {
            if (string.IsNullOrWhiteSpace(ffmpegExe))
            {
                // предупреждение уже показали выше, просто не стартуем
            }
            else
            {
                if (_recordDesktop)
                {
                    try
                    {
                        var outPath = Path.Combine(resultDir, "desktop.mkv");
                        deskRec = await FfmpegRecorder.StartDesktopAsync(ffmpegExe, outPath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Не удалось запустить запись экрана.\n" + ex.Message,
                            "Запись экрана", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                if (_recordCamera)
                {
                    if (string.IsNullOrWhiteSpace(_cameraDeviceName))
                    {
                        MessageBox.Show("Запись камеры включена, но камера не выбрана в настройках.",
                            "Запись камеры", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        try
                        {
                            var camOut = Path.Combine(resultDir, "camera.mkv");

                            camRec = await FfmpegRecorder.StartCameraAsync(
                                ffmpegExe: ffmpegExe!,
                                cameraDeviceName: _cameraDeviceName!,     // то, что ты сохраняешь из ComboBox
                                outputPath: camOut,
                                recordAudio: _recordAudio,                // чекбокс “звук”
                                audioDeviceName: _audioDeviceName,        // выбранный микрофон
                                fps: 30
                            );
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("Не удалось запустить запись камеры.\n" + ex.Message,
                                "Запись камеры", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                }
            }
        }


        DeviceFile? shimmerDev = skipShimmer
            ? null
            : (preShimmerDev ?? exp.Devices.FirstOrDefault(d =>
                string.Equals(d.DevType, "ShimmerGSR", StringComparison.Ordinal)));

        ShimmerGsrClient? shimmer = skipShimmer ? null : preShimmer;

        if (shimmerDev != null && shimmer == null)
        {
            // global sensors.json
            var sensorsPath = TrackerPaths.FindExistingOrDefault("sensors.json");
            SensorsListFile globalSensors;
            try
            {
                globalSensors = JsonSerializer.Deserialize<SensorsListFile>(File.ReadAllText(sensorsPath)) ?? new SensorsListFile();
            }
            catch
            {
                globalSensors = new SensorsListFile();
            }

            // <exp>/sensors.json (биндинг)
            var bindPath = Path.Combine(expDir, "sensors.json");
            ExpBindConfigFile bind = new ExpBindConfigFile();
            if (File.Exists(bindPath))
            {
                try
                {
                    bind = JsonSerializer.Deserialize<ExpBindConfigFile>(File.ReadAllText(bindPath)) ?? new ExpBindConfigFile();
                }
                catch { bind = new ExpBindConfigFile(); }
            }

            string? boundName = null;
            if (!string.IsNullOrWhiteSpace(shimmerDev.Name))
                boundName = bind.List.FirstOrDefault(x => string.Equals(x.Device, shimmerDev.Name, StringComparison.Ordinal))?.Name;

            // ищем нужный глобальный сенсор ShimmerGSR
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
                throw new InvalidOperationException("ShimmerGSR указан в exp.json, но не найден в глобальном sensors.json.");

            if (string.IsNullOrWhiteSpace(globalShimmer.Params))
                throw new InvalidOperationException("У глобального ShimmerGSR нет params (нужны name/port).");

            // parse ShimmerParams {name, port}
            string btName;
            int port;
            using (var doc = JsonDocument.Parse(globalShimmer.Params))
            {
                btName = doc.RootElement.GetProperty("name").GetString() ?? throw new InvalidOperationException("ShimmerParams.name пустой");
                port = doc.RootElement.GetProperty("port").GetInt32();
            }

            if (port <= 0 || port > 65535)
                throw new InvalidOperationException($"Некорректный порт Shimmer: {port}");

            // ShimmerGSRParams берём из exp.json device.params (строка JSON) — отправляем "как есть"
            var measParamsJson = shimmerDev.Params ?? "{}";

            var shimmerDir = Path.Combine(AppContext.BaseDirectory, "Shimmer");
            shimmer = new ShimmerGsrClient(btName, port, measParamsJson, shimmerDir);

            await shimmer.ReadyAsync(ct); // start_program -> connect -> params -> start
        }

        EnterRunMode();
        await ShowTextRenderedAsync("Подготовка…", ct);

        // старт таймера для mouse/keyboard
        _mkStopwatch = Stopwatch.StartNew();

        var (wPx, hPx, wMm, hMm) = MonitorService.GetSelectedMetrics(_writeDesktop);


        var result = new ResultFile
        {
            Rgba = "rgb(0,0,0)",
            TimeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ScreenWidthPx = wPx,
            ScreenHeightPx = hPx,
            ScreenWidthMm = wMm,
            ScreenHeightMm = hMm,
        };
        SetPendingTempResult(resultDir, resultUid, expDir, expJsonPath, exp, result);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Имя"] = dlg.Vm.Name.Trim(),
            ["Возраст"] = dlg.Vm.Age.Trim(),
            ["Пол"] = dlg.Vm.Sex.Trim(),
            ["Комментарий"] = dlg.Vm.Comment.Trim(),
        };

        foreach (var ch in exp.Characteristics)
        {
            if (string.IsNullOrWhiteSpace(ch.Name)) continue;
            map.TryGetValue(ch.Name!, out var v);
            result.CharsData.Add(new CharValue { Name = ch.Name!, Val = v ?? "" });
        }

        TrackerClient? tracker = null;
        Stopwatch? sw = null;
        bool completed = false;

        try
        {
            tracker = new TrackerClient("http://127.0.0.1:4242");

            //ShowText("Подключение к трекеру…");
            await ShowTextRenderedAsync("Подключение к трекеру…", ct);
            // включаем флаги как у тебя было
            await tracker.EnableAsync("ENABLE_SEND_COUNTER", true, ct); await Task.Delay(50, ct);
            await tracker.EnableAsync("ENABLE_SEND_TIME", true, ct); await Task.Delay(50, ct);
            await tracker.EnableAsync("ENABLE_SEND_POG_BEST", true, ct); await Task.Delay(50, ct);
            await tracker.EnableAsync("ENABLE_SEND_POG_LEFT", true, ct); await Task.Delay(50, ct);
            await tracker.EnableAsync("ENABLE_SEND_POG_RIGHT", true, ct); await Task.Delay(50, ct);
            await tracker.EnableAsync("ENABLE_SEND_EYE_LEFT", true, ct); await Task.Delay(50, ct);
            await tracker.EnableAsync("ENABLE_SEND_EYE_RIGHT", true, ct); await Task.Delay(50, ct);
            await tracker.EnableAsync("ENABLE_SEND_PUPILMM", true, ct); await Task.Delay(50, ct);
            await tracker.EnableAsync("ENABLE_SEND_OPENMM", true, ct); await Task.Delay(50, ct);

            await tracker.EnableAsync("ENABLE_SEND_DATA", true, ct);
            await Task.Delay(200, ct);

            int lastAck = 0;

            var trackerDatPath = Path.Combine(resultDir, "tracker.dat");
            await using var trackerDat = new FileStream(
                trackerDatPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);

            bool trackerReady = false;

            foreach (var st in stimulsRun)
            {
                ct.ThrowIfCancellationRequested();

                var kind = st.Kind ?? 0;

                if (kind == 0)
                {
                    trackerReady = false;

                    await DoCalibrationAsync(tracker, ct);

                    await Task.Delay(1500, ct);
                    await ShowTextRenderedAsync("Ожидание валидных данных…", ct);

                    lastAck = 0;

                    lastAck = await WaitTrackerReadyAsync(
                        tracker,
                        lastAck,
                        timeout: TimeSpan.FromSeconds(60),
                        stableFor: TimeSpan.FromMilliseconds(500),
                        ct: ct);

                    trackerReady = true;
                    ShowText("");
                    continue;
                }

                // if (kind != 1) continue;

                sw ??= Stopwatch.StartNew();

                bool isScreenRecord = kind == StimulusKinds.ScreenRecord;

                string? stimulFile = null;
                bool isVideo = false;
                bool isImage = false;

                if (!isScreenRecord)
                {
                    string stimulDir = Path.Combine(expDir, st.Uid);
                    stimulFile = ResolveStimulusFile(stimulDir, st.Filename);
                    isVideo = IsVideoFile(stimulFile);
                    isImage = IsImageFile(stimulFile);

                    if (!isVideo && !isImage)
                        throw new InvalidOperationException($"Неподдерживаемый тип стимула: {Path.GetFileName(stimulFile)}");
                }

                var stimulResDir = Path.Combine(resultDir, st.Uid);
                Directory.CreateDirectory(stimulResDir);


                // per-stim для трекера
                var perStimPath = Path.Combine(stimulResDir, trackerUid);
                await using FileStream? perStim = st.WriteData
                    ? new FileStream(perStimPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true)
                    : null;

                // per-stim для mouse/keyboard
                var perStimMkPath = (st.WriteData && !string.IsNullOrWhiteSpace(mouseKbdUid))
                    ? Path.Combine(stimulResDir, mouseKbdUid!)
                    : null;
                // per-stim для ShimmerGSR (имя файла = uid из exp.json устройства ShimmerGSR)
                // per-stim для Shimmer (имя файла = uid устройства из exp.json)
                var perStimGsrPath = (st.WriteData && shimmerDev != null && shimmer != null)
                    ? Path.Combine(stimulResDir, shimmerDev.Uid)
                    : null;

                await using FileStream? perStimGsr = (perStimGsrPath != null)
                    ? new FileStream(perStimGsrPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: false)
                    : null;

                // включаем запись
                if (shimmer != null) shimmer.SetStream(perStimGsr);


                await using FileStream? perStimMk = (perStimMkPath != null)
                    ? new FileStream(perStimMkPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: false)
                    : null;

                // ✅ ВОТ КУДА "ПИСАТЬ": события клавы/мыши уходят в perStimMk через MkWrite()
                MkSetStream(perStimMk);
                MkMarker();

                try
                {
                    if (!trackerReady)
                        throw new InvalidOperationException("Попытка показать стимул до READY трекера.");

                    // bool isVideo = IsVideoFile(stimulFile);

                    // ✅ 1) Запускаем стимул и заранее получаем Task ожидания конца стимула
                    Task waitStimulus;
                    FfmpegRecorder? screenRecord = null;
                    WindowState? prevWindowState = null;
                    IDisposable? hotkeyScope = null;

                    if (isScreenRecord)
                    {
                        // per-stim запись экрана -> results/<resultUid>/<stimUid>/stimul.mkv
                        if (!string.IsNullOrWhiteSpace(ffmpegExe))
                        {
                            try
                            {
                                var outPath = Path.Combine(stimulResDir, "stimul.mkv");
                                screenRecord = await FfmpegRecorder.StartDesktopAsync(ffmpegExe!, outPath);
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show(
                                    "Не удалось запустить запись экрана для стимула SCREEN_RECORD.\n" + ex.Message,
                                    "Запись", MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        }
                        else if (!screenRecordFfmpegWarned)
                        {
                            screenRecordFfmpegWarned = true;
                            MessageBox.Show(
                                "Стимул SCREEN_RECORD требует ffmpeg, но ffmpeg не найден.\n" +
                                "Запись этого стимула будет пропущена.",
                                "Запись", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }

                        // Для SCREEN_RECORD используем тот же механизм ожидания (таймаут или Next),
                        // но окно сворачиваем на всё время записи.
                        hotkeyScope = BeginGlobalHotkeys();
                        try
                        {
                            prevWindowState = WindowState;
                            WindowState = WindowState.Minimized;
                        }
                        catch { }

                        if (st.NextTimeout)
                        {
                            waitStimulus = Task.Delay(Math.Max(0, st.TimeoutMs), ct);
                        }
                        else
                        {
                            _nextTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            using var reg = ct.Register(() => _nextTcs.TrySetCanceled(ct));
                            waitStimulus = _nextTcs.Task;
                        }
                    }
                    else if (isVideo)
                    {
                        waitStimulus = StartVideoWaitTask(stimulFile!, ct); // ждать MediaEnded / или отмену
                    }
                    else
                    {
                        ShowImage(stimulFile!);

                        if (st.NextTimeout)
                        {
                            waitStimulus = Task.Delay(Math.Max(0, st.TimeoutMs), ct);
                        }
                        else
                        {
                            _nextTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            using var reg = ct.Register(() => _nextTcs.TrySetCanceled(ct));
                            waitStimulus = _nextTcs.Task;
                        }
                    }


                    using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    var pollTask = Task.Run(async () =>
                    {
                        while (!pollCts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                var lines = await tracker.GetRecordsAsync(nRecords: 200, ackNumber: lastAck, ct: pollCts.Token);

                                foreach (var line in lines)
                                {
                                    if (!line.Contains("<REC", StringComparison.OrdinalIgnoreCase)) continue;

                                    var cntS = TrackerClient.TryGetAttr(line, "CNT");
                                    if (cntS == null || !int.TryParse(cntS, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cnt))
                                        continue;

                                    lastAck = Math.Max(lastAck, cnt);

                                    var timeS = TrackerClient.TryGetAttr(line, "TIME");
                                    if (timeS == null || !double.TryParse(timeS, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                                        continue;
                                    if (!double.IsFinite(t))
                                        continue;

                                    var td = BuildTrackerDataFromRec(line, (float)t);
                                    td.WriteTo(trackerDat);
                                    if (perStim != null) td.WriteTo(perStim);
                                }
                            }
                            catch
                            {
                                // игнорируем ошибки опроса во время стимула
                            }

                            await Task.Delay(5, pollCts.Token);
                        }
                    }, pollCts.Token);

                    try
                    {
                        // ✅ 2) Единая точка ожидания:
                        // - картинка: таймаут или Next
                        // - видео: до конца
                        await waitStimulus;
                    }
                    finally
                    {
                        pollCts.Cancel();
                        try { await pollTask; } catch { }

                        if (isVideo) StopVideo(); // ✅ обязательно перед следующим стимулом

                        if (screenRecord != null)
                        {
                            try { await screenRecord.StopAsync(); } catch { }
                        }

                        hotkeyScope?.Dispose();

                        if (prevWindowState != null)
                        {
                            try
                            {
                                WindowState = prevWindowState.Value;
                                Activate();
                            }
                            catch { }
                        }

                        if (isScreenRecord)
                            ShowText("");
                    }
                }

                finally
                {
                    // сначала отключить запись, потом закрыть файл
                    if (shimmer != null) shimmer.SetStream(null);
                    perStimGsr?.Dispose();

                    // ✅ после стимула перестаём писать события (файл perStimMk будет закрыт await using)
                    MkSetStream(null);
                }
            }
            completed = true;

            // сохраняем result.json

        }
        finally
        {
            try { if (camRec != null) await camRec.StopAsync(); } catch { }
            try { if (deskRec != null) await deskRec.StopAsync(); } catch { }

            // на всякий случай
            MkSetStream(null);

            if (tracker != null)
            {
                try { await tracker.EnableAsync("ENABLE_SEND_DATA", false, CancellationToken.None); }
                catch { }
            }
            if (shimmer != null)
            {
                try { shimmer.SetStream(null); } catch { }
                await shimmer.DisposeAsync();
            }
            sw?.Stop();
            result.DurationSec = (int)Math.Round((sw?.Elapsed ?? TimeSpan.Zero).TotalSeconds);
        }
        if (completed)
        {
            await CommitPendingTempResultAsync();
        }

    }

    private async Task ShowTextRenderedAsync(string text, CancellationToken ct)
    {
        ShowText(text);

        // важный момент: заставляем WPF реально отрисовать кадр
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render, ct);

        // небольшой “антифликер”
        await Task.Delay(30, ct);
    }

    private void ShowText(string text)
    {
        OverlayText.Text = text;
        OverlayText.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StopVideo(); // ✅ важно
        // чтобы не вспыхивала старая картинка
        StimImage.Source = null;
        StimImage.Visibility = Visibility.Collapsed;
    }



    private static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm";
    }

    private void StopVideoUi()
    {
        // LibVLC
        try { _vlcPlayer?.Stop(); } catch { }
        try
        {
            if (_vlcPlayer != null) _vlcPlayer.Media = null;
        }
        catch { }

        StimVlc.Visibility = Visibility.Collapsed;

        // (опционально) если MediaElement ещё есть в XAML — прибьём тоже
        try { StimVideo.Stop(); } catch { }
        StimVideo.Source = null;
        StimVideo.Visibility = Visibility.Collapsed;
    }

    private void StopVideo()
    {
        if (Dispatcher.CheckAccess()) StopVideoUi();
        else Dispatcher.BeginInvoke((Action)StopVideoUi);
    }


    // Вставьте этот метод в класс MainWindow (в MainWindow.xaml.cs)

    private void SaveResultColorToDisk(string? resultUid, string hexColor)
    {
        if (string.IsNullOrWhiteSpace(resultUid)) return;
        
        // Получаем текущую папку эксперимента из ViewModel
        if (DataContext is not MainViewModel vm || vm.SelectedExperiment == null) return;
        var expDir = vm.SelectedExperiment.ExpDir;

        // Путь к файлу: Experiment/results/UID/result.json
        var resultPath = Path.Combine(expDir, "results", resultUid, "result.json");

        if (File.Exists(resultPath))
        {
            try
            {
                // 1. Читаем файл
                var json = File.ReadAllText(resultPath);
                
                // Опции для чтения (чтобы не падать на комментариях и запятых)
                var readOpts = new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                var resultObj = JsonSerializer.Deserialize<ResultFile>(json, readOpts);

                if (resultObj != null)
                {
                    // 2. Меняем цвет
                    resultObj.Rgba = hexColor;

                    // 3. Сохраняем обратно (с отступами, чтобы было красиво)
                    var writeOpts = new JsonSerializerOptions 
                    { 
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
                    };
                    
                    var newJson = JsonSerializer.Serialize(resultObj, writeOpts);
                    File.WriteAllText(resultPath, newJson);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка сохранения цвета: {ex.Message}");
            }
        }
    }
    /// <summary>
    /// Запускает видео и возвращает Task, который завершится на MediaEnded (или упадёт на MediaFailed).
    /// Таймауты тут НЕ используются.
    /// </summary>
    private Task StartVideoWaitTask(string path, CancellationToken ct)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.Invoke(() => StartVideoWaitTask(path, ct));

        EnsureVlc();

        var full = Path.GetFullPath(path);

        // очистка старого кадра/картинки
        OverlayText.Visibility = Visibility.Collapsed;
        StimImage.Source = null;
        StimImage.Visibility = Visibility.Collapsed;
        StopVideoUi();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Media? media = null;
        CancellationTokenRegistration reg = default;

        EventHandler<EventArgs>? ended = null;
        EventHandler<EventArgs>? failed = null;


        void Cleanup()
        {
            try { reg.Dispose(); } catch { }

            if (_vlcPlayer != null)
            {
                if (ended != null) _vlcPlayer.EndReached -= ended;
                if (failed != null) _vlcPlayer.EncounteredError -= failed;
            }

            try { media?.Dispose(); } catch { }
            media = null;
        }

        ended = (_, __) =>
        {
            // событие не UI-тред → уводим в UI
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Cleanup();
                StopVideoUi();
                tcs.TrySetResult(true);
            }));
        };

        failed = (_, __) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Cleanup();
                StopVideoUi();
                tcs.TrySetException(new InvalidOperationException("LibVLC: EncounteredError при воспроизведении видео"));
            }));
        };

        reg = ct.Register(() =>
        {
            void DoCancel()
            {
                Cleanup();
                StopVideoUi();
                tcs.TrySetCanceled(ct);
            }

            if (Dispatcher.CheckAccess()) DoCancel();
            else Dispatcher.BeginInvoke((Action)DoCancel);
        });

        _vlcPlayer!.EndReached += ended;
        _vlcPlayer.EncounteredError += failed;

        media = new Media(_vlc!, new Uri(full));
        StimVlc.Visibility = Visibility.Visible;
        _vlcPlayer.Play(media);

        return tcs.Task;
    }




    private void ShowImage(string path)
    {
        OverlayText.Visibility = Visibility.Collapsed;
        StopVideo(); // ✅ важно
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
        StimImage.Source = bmp;
        StimImage.Visibility = Visibility.Visible;
    }

    private static TrackerData BuildTrackerDataFromRec(string line, float time)
    {
        float GetF(string name) =>
            float.TryParse(TrackerClient.TryGetAttr(line, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : float.NaN;

        int GetI(string name) =>
            int.TryParse(TrackerClient.TryGetAttr(line, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        var r = new TrackerData();
        r.time = time;

        int bpogv = GetI("BPOGV");
        int lpogv = GetI("LPOGV");
        int rpogv = GetI("RPOGV");
        int lpupilv = GetI("LPUPILV");
        int rpupilv = GetI("RPUPILV");
        int lpmmv = GetI("LPMMV");
        int rpmmv = GetI("RPMMV");
        int lopenv = GetI("LOPENMMV");
        int ropenv = GetI("ROPENMMV");

        r.valid = 0;

        float bx = GetF("BPOGX");
        float by = GetF("BPOGY");
        if (bpogv != 0 && float.IsFinite(bx) && float.IsFinite(by) && bx >= 0 && bx <= 1 && by >= 0 && by <= 1)
            r.valid |= (int)TrackerDataValidity.COORD_VALID;
        r.x = float.IsFinite(bx) ? bx : 0;
        r.y = float.IsFinite(by) ? by : 0;
        r.z = 0;

        float lx = GetF("LPOGX");
        float ly = GetF("LPOGY");
        if (lpogv != 0 && float.IsFinite(lx) && float.IsFinite(ly) && lx >= 0 && lx <= 1 && ly >= 0 && ly <= 1)
            r.valid |= (int)TrackerDataValidity.LEFT_PUPIL_COORD_VALID;
        r.lx = float.IsFinite(lx) ? lx : 0;
        r.ly = float.IsFinite(ly) ? ly : 0;

        float rx = GetF("RPOGX");
        float ry = GetF("RPOGY");
        if (rpogv != 0 && float.IsFinite(rx) && float.IsFinite(ry) && rx >= 0 && rx <= 1 && ry >= 0 && ry <= 1)
            r.valid |= (int)TrackerDataValidity.RIGHT_PUPIL_COORD_VALID;
        r.rx = float.IsFinite(rx) ? rx : 0;
        r.ry = float.IsFinite(ry) ? ry : 0;

        r.leyex = GetF("LEYEX");
        r.leyey = GetF("LEYEY");
        r.leyez = GetF("LEYEZ");
        r.reyex = GetF("REYEX");
        r.reyey = GetF("REYEY");
        r.reyez = GetF("REYEZ");

        float lopen = GetF("LOPENMM");
        float ropen = GetF("ROPENMM");
        r.lopen = float.IsFinite(lopen) ? lopen : 0;
        r.ropen = float.IsFinite(ropen) ? ropen : 0;

        // 3D validity: требуем finite и > 0, иначе distance будет мусорным.
        bool l3d = lpupilv != 0 && float.IsFinite(r.leyez) && r.leyez > 0;
        bool r3d = rpupilv != 0 && float.IsFinite(r.reyez) && r.reyez > 0;
        if (l3d) r.valid |= (int)TrackerDataValidity.LEFT_PUPIL_3D_COORD_VALID;
        if (r3d) r.valid |= (int)TrackerDataValidity.RIGHT_PUPIL_3D_COORD_VALID;

        if (l3d && r3d) r.z = (r.leyez + r.reyez) / 2f;
        else if (l3d) r.z = r.leyez;
        else if (r3d) r.z = r.reyez;

        if (lpmmv != 0)
        {
            var v = GetF("LPMM");
            if (float.IsFinite(v)) { r.valid |= (int)TrackerDataValidity.LEFT_PUPIL_SIZE_VALID; r.lp = v; }
        }
        if (rpmmv != 0)
        {
            var v = GetF("RPMM");
            if (float.IsFinite(v)) { r.valid |= (int)TrackerDataValidity.RIGHT_PUPIL_SIZE_VALID; r.rp = v; }
        }

        if (lopenv != 0 && float.IsFinite(r.lopen)) r.valid |= (int)TrackerDataValidity.LEFT_OPEN_VALID;
        if (ropenv != 0 && float.IsFinite(r.ropen)) r.valid |= (int)TrackerDataValidity.RIGHT_OPEN_VALID;

        return r;
    }

    private static string ResolveStimulusFile(string stimulDir, string? expectedFilename)
    {
        if (!Directory.Exists(stimulDir))
        {
            var expDir = Directory.GetParent(stimulDir)?.FullName;
            if (!string.IsNullOrWhiteSpace(expDir))
            {
                var directFile = TryResolveStimulusFileInDir(expDir, expectedFilename)
                    ?? TryResolveStimulusFileInDir(expDir, Path.GetFileName(stimulDir));
                if (!string.IsNullOrWhiteSpace(directFile))
                    return directFile;
            }

            throw new DirectoryNotFoundException($"Stimulus folder not found: {stimulDir}");
        }

        if (string.IsNullOrWhiteSpace(expectedFilename))
            throw new FileNotFoundException("Stimulus filename is empty", stimulDir);

        // 1) пробуем строго как в exp.json
        var expectedPath = Path.Combine(stimulDir, expectedFilename);
        if (File.Exists(expectedPath))
            return expectedPath;

        // 2) если вдруг имя пришло URL-экранированным — пробуем разэкранировать
        string unescaped;
        try { unescaped = Uri.UnescapeDataString(expectedFilename); }
        catch { unescaped = expectedFilename; }

        if (!string.Equals(unescaped, expectedFilename, StringComparison.Ordinal))
        {
            var p2 = Path.Combine(stimulDir, unescaped);
            if (File.Exists(p2))
                return p2;
        }

        // 3) НИКАКИХ переименований. Только fallback (чтобы не падать).
        var files = Directory.EnumerateFiles(stimulDir, "*", SearchOption.TopDirectoryOnly)
            .Where(f => !string.Equals(Path.GetFileName(f), "Thumbs.db", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0)
            throw new FileNotFoundException($"No files in stimulus folder: {stimulDir}");

        var expectedExt = Path.GetExtension(unescaped);
        var sameExt = !string.IsNullOrEmpty(expectedExt)
            ? files.Where(f => string.Equals(Path.GetExtension(f), expectedExt, StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<string>();

        bool expectedIsVideo = !string.IsNullOrEmpty(expectedExt) && IsVideoFile("x" + expectedExt);
        bool expectedIsImage = !string.IsNullOrEmpty(expectedExt) && IsImageFile("x" + expectedExt);

        // ✅ если ожидаем конкретный тип (по расширению) — НЕ подменяем его другим
        if (expectedIsVideo || expectedIsImage)
        {
            if (sameExt.Count == 0)
                throw new FileNotFoundException($"Ожидался файл типа {expectedExt}, но в папке его нет: {stimulDir}");

            // детерминированный выбор среди файлов нужного расширения
            // (если в папке бардак и несколько файлов — хотя бы не подменяем тип)
            return sameExt
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .First();
        }

        // иначе — старый мягкий fallback (если расширение странное/пустое)
        var chosen =
            files.Count == 1 ? files[0] :
            files.FirstOrDefault(IsImageFile)
                ?? files.FirstOrDefault(IsVideoFile)
                ?? files[0];

        return chosen;
    }

    private static string? TryResolveStimulusFileInDir(string dir, string? expectedFilename)
    {
        if (string.IsNullOrWhiteSpace(expectedFilename))
            return null;

        var expectedPath = Path.Combine(dir, expectedFilename);
        if (File.Exists(expectedPath))
            return expectedPath;

        string unescaped;
        try { unescaped = Uri.UnescapeDataString(expectedFilename); }
        catch { unescaped = expectedFilename; }

        if (!string.Equals(unescaped, expectedFilename, StringComparison.Ordinal))
        {
            var unescapedPath = Path.Combine(dir, unescaped);
            if (File.Exists(unescapedPath))
                return unescapedPath;
        }

        return null;
    }

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
    }

    private static void GetPrimaryScreen(out int wPx, out int hPx, out int wMm, out int hMm)
    {
        const int HORZSIZE = 4;   // mm
        const int VERTSIZE = 6;   // mm

        const int HORZRES = 8;    // px (может быть "логическим" при DPI virtualization)
        const int VERTRES = 10;

        const int DESKTOPVERTRES = 117; // реальные px
        const int DESKTOPHORZRES = 118; // реальные px

        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            // реальные пиксели (важно при масштабировании Windows)
            wPx = GetDeviceCaps(hdc, DESKTOPHORZRES);
            hPx = GetDeviceCaps(hdc, DESKTOPVERTRES);

            // fallback на случай странных драйверов
            if (wPx <= 0 || hPx <= 0)
            {
                wPx = GetDeviceCaps(hdc, HORZRES);
                hPx = GetDeviceCaps(hdc, VERTRES);
            }

            // физический размер (мм)
            wMm = GetDeviceCaps(hdc, HORZSIZE);
            hMm = GetDeviceCaps(hdc, VERTSIZE);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private static async Task<int> WaitTrackerReadyAsync(
    TrackerClient tracker,
    int lastAck,
    TimeSpan timeout,
    TimeSpan stableFor,
    CancellationToken ct)
    {
        // === 1. FLUSH (Чистка с игнорированием ошибок сети) ===
        int currentAck = 0;
        for (int i = 0; i < 50; i++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var dump = await tracker.GetRecordsAsync(nRecords: 200, ackNumber: currentAck, ct: ct);
                if (dump.Count == 0) break;

                foreach (var line in dump)
                {
                    if (!line.Contains("<REC", StringComparison.OrdinalIgnoreCase)) continue;
                    var cntS = TrackerClient.TryGetAttr(line, "CNT");
                    if (int.TryParse(cntS, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
                        currentAck = Math.Max(currentAck, c);
                }
                if (dump.Count < 200) break;
            }
            catch (Exception)
            {
                // Игнорируем ошибки при чистке, просто ждем и пробуем дальше
                await Task.Delay(50, ct);
            }
        }

        // === 2. ОЖИДАНИЕ (С игнорированием ошибок сети) ===
        var deadline = DateTime.UtcNow + timeout;
        DateTime? stableStart = null;

        int GetVal(string xml, string name) =>
            int.TryParse(TrackerClient.TryGetAttr(xml, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            List<string> lines = new();
            try
            {
                lines = await tracker.GetRecordsAsync(nRecords: 50, ackNumber: currentAck, ct: ct);
            }
            catch
            {
                // ОШИБКА СЕТИ: Считаем, что данных нет, и пробуем снова через паузу
                stableStart = null;
                await Task.Delay(250, ct);
                continue;
            }

            if (lines.Count == 0)
            {
                stableStart = null;
                await Task.Delay(50, ct);
                continue;
            }

            bool chunkHasValid = false;
            foreach (var line in lines)
            {
                if (!line.Contains("<REC", StringComparison.OrdinalIgnoreCase)) continue;
                var cntS = TrackerClient.TryGetAttr(line, "CNT");
                if (int.TryParse(cntS, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
                    currentAck = Math.Max(currentAck, c);

                var validMask = GetVal(line, "BPOGV") | GetVal(line, "LPOGV") | GetVal(line, "RPOGV") |
                                GetVal(line, "LPUPILV") | GetVal(line, "RPUPILV");

                if (validMask != 0) chunkHasValid = true;
            }

            if (chunkHasValid)
            {
                stableStart ??= DateTime.UtcNow;
                if (DateTime.UtcNow - stableStart.Value >= stableFor)
                    return currentAck;
            }
            else
            {
                stableStart = null;
            }
            await Task.Delay(30, ct);
        }

        throw new InvalidOperationException($"Таймаут ожидания данных ({timeout.TotalSeconds}с). Связь есть, но валидных глаз нет.");
    }


    private async Task DoCalibrationAsync(TrackerClient tracker, CancellationToken ct)
    {
        // Очищаем текст, чтобы не висела надпись "Подготовка".
        // Visibility не трогаем, чтобы не пересчитывать Layout (меньше шанс фриза/мигания)
        OverlayText.Text = "";

        // ВАЖНО: Мы НЕ трогаем Topmost и НЕ вызываем Hide/Show.
        // Окно остается висеть черным фоном. Калибровка трекера (PathFinder)
        // должна сама открыться поверх нас.

        try
        {
            // 1. Отправляем команду старта
            try
            {
                await tracker.StartCalibrationAsync(ct);
            }
            catch (Exception ex)
            {
                // Игнорируем ошибку отправки, вдруг уже калибруется
                System.Diagnostics.Debug.WriteLine($"Start calibration: {ex.Message}");
            }

            // =======================================================================
            // ФАЗА 1: Ждем "ТИШИНУ" (начало калибровки - разрыв потока данных)
            // =======================================================================

            var silenceStart = DateTime.MinValue;
            var startPhaseDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

            while (DateTime.UtcNow < startPhaseDeadline)
            {
                ct.ThrowIfCancellationRequested();

                bool hasData = false;
                try
                {
                    var recs = await tracker.GetRecordsAsync(nRecords: 10, ackNumber: 0, ct: ct);
                    if (recs != null && recs.Count > 0) hasData = true;
                }
                catch { hasData = false; }

                if (!hasData)
                {
                    if (silenceStart == DateTime.MinValue) silenceStart = DateTime.UtcNow;

                    // Если данных нет > 1000мс - значит калибровка началась
                    if ((DateTime.UtcNow - silenceStart).TotalMilliseconds > 1000) break;
                }
                else
                {
                    silenceStart = DateTime.MinValue;
                }

                await Task.Delay(100, ct);
            }

            // =======================================================================
            // ФАЗА 2: Ждем "СИГНАЛ" (конец калибровки - возобновление потока)
            // =======================================================================

            var endPhaseDeadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            int successSequence = 0;

            while (DateTime.UtcNow < endPhaseDeadline)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var recs = await tracker.GetRecordsAsync(nRecords: 10, ackNumber: 0, ct: ct);

                    if (recs != null && recs.Count > 0)
                    {
                        successSequence++;
                        if (successSequence >= 5) break; // Связь восстановилась
                    }
                    else
                    {
                        successSequence = 0;
                    }
                }
                catch { successSequence = 0; }

                await Task.Delay(250, ct);
            }
        }
        finally
        {
            // Убеждаемся, что наше окно активно и в фокусе после возврата
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
            Activate();
            Focus();

            // Маленькая пауза перед следующим шагом
            await Task.Delay(100, ct);
        }
    }



    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            // Cancel any ongoing experiment operations
            AbortExperimentFromUi();
            _runCts?.Cancel();
            _nextTcs?.TrySetCanceled();
        }
        catch { }

        base.OnClosing(e);
    }

    protected override async void OnClosed(EventArgs e)
    {
        try
        {
            UnregisterGlobalHotkeys();
            _hwndSource?.RemoveHook(WndProc);
            _hwndSource = null;

            // Wait for any active run task to complete
            if (_activeRunTask != null)
            {
                try
                {
                    await Task.WhenAny(_activeRunTask, Task.Delay(2000));
                }
                catch { }
            }
        }
        catch { }

        try
        {
            // Clean up VLC resources
            _vlcPlayer?.Stop();
            _vlcPlayer?.Dispose();
            _vlcPlayer = null;
            
            _vlc?.Dispose();
            _vlc = null;
        }
        catch { }

        try
        {
            // Close all other windows to ensure proper shutdown
            for (int i = System.Windows.Application.Current.Windows.Count - 1; i >= 0; i--)
            {
                var window = System.Windows.Application.Current.Windows[i];
                if (window != this)
                {
                    try
                    {
                        window.Close();
                    }
                    catch { }
                }
            }
        }
        catch { }

        base.OnClosed(e);
        
        // Force application shutdown to ensure process terminates
        System.Windows.Application.Current.Shutdown();
    }

}
