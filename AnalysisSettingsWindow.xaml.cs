using System;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace NeuroBureau.Experiment;

public partial class AnalysisSettingsWindow : Window
{
    public AnalysisDetectionSettings Settings { get; private set; }
    public AnalysisVisualizationSettings VisualizationSettings { get; private set; }

    public AnalysisSettingsWindow(AnalysisDetectionSettings current, AnalysisVisualizationSettings visualizationSettings, int initialTab = 0)
    {
        InitializeComponent();

        Settings = (current ?? new AnalysisDetectionSettings()).Clone();
        VisualizationSettings = (visualizationSettings ?? new AnalysisVisualizationSettings()).Clone();

        EyeCombo.ItemsSource = Enum.GetValues(typeof(EyeSelection)).Cast<EyeSelection>();
        NoiseCombo.ItemsSource = Enum.GetValues(typeof(NoiseReductionType)).Cast<NoiseReductionType>();
        HeatmapFunctionCombo.ItemsSource = Enum.GetValues(typeof(HeatmapFalloff)).Cast<HeatmapFalloff>();
        HeatmapTypeCombo.ItemsSource = Enum.GetValues(typeof(HeatmapType)).Cast<HeatmapType>();

        EyeCombo.SelectedItem = Settings.Eye;
        NoiseCombo.SelectedItem = Settings.NoiseReduction;

        // Heatmap settings initialization
        HeatmapFunctionCombo.SelectedItem = VisualizationSettings.HeatmapFunction;
        HeatmapTypeCombo.SelectedItem = VisualizationSettings.HeatmapMapType;
        HeatmapRadiusBox.Text = VisualizationSettings.HeatmapRadius.ToString(CultureInfo.InvariantCulture);
        HeatmapAlphaBox.Text = VisualizationSettings.HeatmapInitialOpacity.ToString(CultureInfo.InvariantCulture);
        HeatmapThresholdBox.Text = VisualizationSettings.HeatmapThreshold.ToString(CultureInfo.InvariantCulture);

        WindowSizeBox.Text = Settings.WindowSize.ToString(CultureInfo.InvariantCulture);
        GapWindowSizeBox.Text = Settings.GapWindowSize.ToString(CultureInfo.InvariantCulture);
        MaxSpeedBox.Text = Settings.MaxSpeedDegPerSec.ToString(CultureInfo.InvariantCulture);

        // IDT
        IdtDispersionBox.Text = Settings.IdtDispersionThresholdPx.ToString(CultureInfo.InvariantCulture);
        IdtMinDurationBox.Text = Settings.IdtMinDurationMs.ToString(CultureInfo.InvariantCulture);
        IdtWindowBox.Text = Settings.IdtWindowMs.ToString(CultureInfo.InvariantCulture);

        // IVT
        IvtSpeedFixBox.Text = Settings.IvtSpeedFixDegPerSec.ToString(CultureInfo.InvariantCulture);
        IvtMinDurationBox.Text = Settings.IvtMinDurationMs.ToString(CultureInfo.InvariantCulture);
        IvtMergeTimeBox.Text = Settings.IvtMergeTimeMs.ToString(CultureInfo.InvariantCulture);
        IvtMergeAngleBox.Text = Settings.IvtMergeAngleDeg.ToString(CultureInfo.InvariantCulture);

        AlgoTabs.SelectedIndex = Settings.Algorithm == FixationAlgorithm.Idt ? 0 : 1;

        // Визуализация (Фиксации)
        VisMinRadiusBox.Text = VisualizationSettings.MinRadius.ToString(CultureInfo.InvariantCulture);
        VisMaxRadiusBox.Text = VisualizationSettings.MaxRadius.ToString(CultureInfo.InvariantCulture);
        VisMaxDurationBox.Text = VisualizationSettings.MaxDurationSec.ToString(CultureInfo.InvariantCulture);
        VisLineWidthBox.Text = VisualizationSettings.LineWidth.ToString(CultureInfo.InvariantCulture);
        VisAlphaBox.Text = VisualizationSettings.Alpha.ToString(CultureInfo.InvariantCulture);
        VisFontFamilyBox.Text = VisualizationSettings.FontFamily;
        VisFontSizeBox.Text = VisualizationSettings.FontSize.ToString(CultureInfo.InvariantCulture);

               // === НОВОЕ: Визуализация (Пчелиный рой) ===
        BeeRadiusBox.Text = VisualizationSettings.BeeRadius.ToString(CultureInfo.InvariantCulture);
        BeeLineBox.Text = VisualizationSettings.BeeLineWidth.ToString(CultureInfo.InvariantCulture);
        // ===========================================

        // === КГР: фильтрация ===
        KgrFilterEnabledCheck.IsChecked = VisualizationSettings.KgrFilterEnabled;

        KgrUseMedianCheck.IsChecked = VisualizationSettings.KgrUseMedianFilter;
        KgrMedianWindowSecBox.Text = VisualizationSettings.KgrMedianWindowSec.ToString(CultureInfo.InvariantCulture);

        KgrUseEmaCheck.IsChecked = VisualizationSettings.KgrUseEmaFilter;
        KgrSrTauSecBox.Text = VisualizationSettings.KgrSrEmaTauSec.ToString(CultureInfo.InvariantCulture);
        KgrScTauSecBox.Text = VisualizationSettings.KgrScEmaTauSec.ToString(CultureInfo.InvariantCulture);
        KgrHrTauSecBox.Text = VisualizationSettings.KgrHrEmaTauSec.ToString(CultureInfo.InvariantCulture);
        KgrPpgTauSecBox.Text = VisualizationSettings.KgrPpgEmaTauSec.ToString(CultureInfo.InvariantCulture);

        KgrClampHrCheck.IsChecked = VisualizationSettings.KgrClampHr;
        KgrHrMinBox.Text = VisualizationSettings.KgrHrMin.ToString(CultureInfo.InvariantCulture);
        KgrHrMaxBox.Text = VisualizationSettings.KgrHrMax.ToString(CultureInfo.InvariantCulture);
        KgrHrMaxDeltaBox.Text = VisualizationSettings.KgrHrMaxDeltaPerSec.ToString(CultureInfo.InvariantCulture);
        // ======================


        RootTabs.SelectedIndex = Math.Clamp(initialTab, 0, RootTabs.Items.Count - 1);
        Title = RootTabs.SelectedIndex == 1 ? "Настройки отображения" : "Настройки анализа";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";

        try
        {
            Settings.Eye = (EyeCombo.SelectedItem is EyeSelection es) ? es : EyeSelection.AverageEye;
            Settings.NoiseReduction = (NoiseCombo.SelectedItem is NoiseReductionType nr) ? nr : NoiseReductionType.None;

            Settings.WindowSize = ParseInt(WindowSizeBox.Text, min: 1, max: 999, name: "Окно фильтра");
            Settings.GapWindowSize = ParseInt(GapWindowSizeBox.Text, min: 0, max: 999, name: "Gap filling");
            Settings.MaxSpeedDegPerSec = ParseDouble(MaxSpeedBox.Text, min: 0, max: 1_000_000, name: "MaxSpeed");

            if (AlgoTabs.SelectedIndex == 0)
            {
                Settings.Algorithm = FixationAlgorithm.Idt;
                Settings.IdtDispersionThresholdPx = ParseDouble(IdtDispersionBox.Text, min: 0.1, max: 1_000_000, name: "DispersionThreshold");
                Settings.IdtMinDurationMs = ParseInt(IdtMinDurationBox.Text, min: 1, max: 600_000, name: "MinDuration");
                Settings.IdtWindowMs = ParseInt(IdtWindowBox.Text, min: 1, max: 600_000, name: "MinWindow");
            }
            else
            {
                Settings.Algorithm = FixationAlgorithm.Ivt;
                Settings.IvtSpeedFixDegPerSec = ParseDouble(IvtSpeedFixBox.Text, min: 0.1, max: 1_000_000, name: "SpeedFix");
                Settings.IvtMinDurationMs = ParseInt(IvtMinDurationBox.Text, min: 1, max: 600_000, name: "MinDuration");
                Settings.IvtMergeTimeMs = ParseInt(IvtMergeTimeBox.Text, min: 0, max: 600_000, name: "MergeTime");
                Settings.IvtMergeAngleDeg = ParseDouble(IvtMergeAngleBox.Text, min: 0, max: 180, name: "MergeAngle");
            }

            // Сохранение общих настроек визуализации
            VisualizationSettings.MinRadius = ParseDouble(VisMinRadiusBox.Text, min: 1, max: 2_000, name: "Min radius");
            VisualizationSettings.MaxRadius = ParseDouble(VisMaxRadiusBox.Text, min: VisualizationSettings.MinRadius, max: 5_000, name: "Max radius");
            VisualizationSettings.MaxDurationSec = ParseDouble(VisMaxDurationBox.Text, min: 0.01, max: 10_000, name: "Max duration");
            VisualizationSettings.LineWidth = ParseDouble(VisLineWidthBox.Text, min: 0.1, max: 200, name: "Line width");
            VisualizationSettings.Alpha = ParseDouble(VisAlphaBox.Text, min: 0.0, max: 1.0, name: "Alpha");
            VisualizationSettings.FontFamily = string.IsNullOrWhiteSpace(VisFontFamilyBox.Text)
                ? "Segoe UI"
                : VisFontFamilyBox.Text.Trim();
            VisualizationSettings.FontSize = ParseDouble(VisFontSizeBox.Text, min: 6, max: 200, name: "Font size");

                        // === НОВОЕ: Сохранение настроек Пчелиного роя ===
            VisualizationSettings.BeeRadius = ParseDouble(BeeRadiusBox.Text, min: 1, max: 2000, name: "Bee Radius");
            VisualizationSettings.BeeLineWidth = ParseDouble(BeeLineBox.Text, min: 0.1, max: 200, name: "Bee Line Width");
            // ================================================

            // === КГР: сохранение фильтра ===
            VisualizationSettings.KgrFilterEnabled = KgrFilterEnabledCheck.IsChecked == true;

            VisualizationSettings.KgrUseMedianFilter = KgrUseMedianCheck.IsChecked == true;
            VisualizationSettings.KgrMedianWindowSec = ParseDouble(KgrMedianWindowSecBox.Text, min: 0, max: 60, name: "KGR median window (sec)");

            VisualizationSettings.KgrUseEmaFilter = KgrUseEmaCheck.IsChecked == true;
            VisualizationSettings.KgrSrEmaTauSec = ParseDouble(KgrSrTauSecBox.Text, min: 0, max: 600, name: "KGR SR tau (sec)");
            VisualizationSettings.KgrScEmaTauSec = ParseDouble(KgrScTauSecBox.Text, min: 0, max: 600, name: "KGR SC tau (sec)");
            VisualizationSettings.KgrHrEmaTauSec = ParseDouble(KgrHrTauSecBox.Text, min: 0, max: 600, name: "KGR HR tau (sec)");
            VisualizationSettings.KgrPpgEmaTauSec = ParseDouble(KgrPpgTauSecBox.Text, min: 0, max: 600, name: "KGR PPG tau (sec)");

            VisualizationSettings.KgrClampHr = KgrClampHrCheck.IsChecked == true;
            VisualizationSettings.KgrHrMin = ParseDouble(KgrHrMinBox.Text, min: 0, max: 1000, name: "KGR HR min");
            VisualizationSettings.KgrHrMax = ParseDouble(KgrHrMaxBox.Text, min: 0, max: 1000, name: "KGR HR max");
            VisualizationSettings.KgrHrMaxDeltaPerSec = ParseDouble(KgrHrMaxDeltaBox.Text, min: 0, max: 1000, name: "KGR HR max delta (bpm/sec)");
            // ==============================


            // === Сохранение настроек тепловой карты ===
            VisualizationSettings.HeatmapFunction = HeatmapFunctionCombo.SelectedItem is HeatmapFalloff hf
                ? hf
                : HeatmapFalloff.Exponential;
            VisualizationSettings.HeatmapMapType = HeatmapTypeCombo.SelectedItem is HeatmapType ht
                ? ht
                : HeatmapType.Heatmap;
            VisualizationSettings.HeatmapRadius = ParseDouble(HeatmapRadiusBox.Text, min: 1, max: 2000, name: "Heatmap Radius");
            VisualizationSettings.HeatmapInitialOpacity = ParseDouble(HeatmapAlphaBox.Text, min: 0, max: 1, name: "Heatmap Initial Opacity");
            VisualizationSettings.HeatmapThreshold = ParseDouble(HeatmapThresholdBox.Text, min: 0, max: 1, name: "Heatmap Threshold");
            // ==========================================

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private static int ParseInt(string? s, int min, int max, string name)
    {
        if (!int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new Exception($"{name}: ожидалось целое число");
        if (v < min || v > max)
            throw new Exception($"{name}: допустимо {min}..{max}");
        return v;
    }

    private static double ParseDouble(string? s, double min, double max, string name)
    {
        // разрешим и запятую (пользовательский ввод)
        var t = (s ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            throw new Exception($"{name}: ожидалось число");
        if (double.IsNaN(v) || double.IsInfinity(v) || v < min || v > max)
            throw new Exception($"{name}: допустимо {min}..{max}");
        return v;
    }
}