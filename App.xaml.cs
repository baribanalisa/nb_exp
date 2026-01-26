using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
// === ИСПРАВЛЕНИЕ КОНФЛИКТОВ ===
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
// ==============================

namespace NeuroBureau.Experiment; // <--- Должно быть такое же

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowAndLog(e.Exception);
        e.Handled = true; // не даём приложению закрыться
    }

    private void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            ShowAndLog(ex);
        else
            ShowAndLog(new Exception("UnhandledException: unknown exception object"));
    }

    private static void ShowAndLog(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NeuroBureau.Experiment"
            );
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* ignore */ }
        try
        {
            // пытаемся предложить сохранить неполный результат, если он есть
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (Application.Current?.MainWindow is MainWindow mw)
                    mw.TryOfferSavePendingResult("Сбой приложения");
            });
        }
        catch { /* ignore */ }

        MessageBox.Show(ex.ToString(), "Сбой", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
