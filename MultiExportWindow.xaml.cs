// File: MultiExportWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using WinForms = System.Windows.Forms;

namespace NeuroBureau.Experiment;

public partial class MultiExportWindow : Window
{
    public MultiExportViewModel Vm { get; }

    public MultiExportWindow(string expDir, IReadOnlyCollection<string> initialSelectedResultUids)
    {
        InitializeComponent();
        Vm = new MultiExportViewModel(expDir, initialSelectedResultUids ?? Array.Empty<string>());
        DataContext = Vm;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await Vm.InitializeAsync();
    }

    private void BrowseOutputDir_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Выберите папку для экспорта",
            UseDescriptionForTitle = true,
            SelectedPath = Vm.OutputDir
        };

        if (dlg.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            Vm.OutputDir = dlg.SelectedPath;
    }

    private void SelectAllStimuli_Click(object sender, RoutedEventArgs e) => Vm.SetAllStimuliSelected(true);
    private void ClearAllStimuli_Click(object sender, RoutedEventArgs e) => Vm.SetAllStimuliSelected(false);

    private void SelectAllResults_Click(object sender, RoutedEventArgs e) => Vm.SetAllResultsSelected(true);
    private void ClearAllResults_Click(object sender, RoutedEventArgs e) => Vm.SetAllResultsSelected(false);

    private void StimulusItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MultiExportStimulusItem item)
        {
            item.IsSelected = !item.IsSelected;
        }
    }

    private void ResultItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MultiExportResultItem item)
        {
            item.IsSelected = !item.IsSelected;
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Vm.ValidateTemplate();

            if (!Vm.CanStartExport)
            {
                MessageBox.Show(
                    "Проверь: выбраны стимулы/результаты, корректна папка и шаблон имени файла.",
                    "Мультиэкспорт",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            Vm.IsBusy = true;
            Vm.StatusText = "Экспорт…";

            // сохранить настройки
            AppConfigManager.SaveMultiExportSettings(Vm.ToSettings());

            var opts = Vm.BuildOptions();
            var stimuli = Vm.GetSelectedStimuli();
            var results = Vm.GetSelectedResults();

            var svc = new MultiExportService(Vm.ExpDir, Vm.Experiment!);

            await Task.Run(() =>
            {
                svc.Export(
                    opts,
                    stimuli,
                    results,
                    s => Dispatcher.BeginInvoke(() => Vm.StatusText = s),
                    default);
            });

            Vm.StatusText = "Экспорт завершён!";
            MessageBox.Show("Экспорт завершён!", "Мультиэкспорт", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"Ошибка: {ex.Message}";
            MessageBox.Show($"Ошибка при экспорте:\n{ex.Message}", "Мультиэкспорт", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Vm.IsBusy = false;
        }
    }
}
