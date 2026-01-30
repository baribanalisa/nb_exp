// File: MultiExportViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace NeuroBureau.Experiment;

public sealed class MultiExportViewModel : ObservableObject
{
    private readonly HashSet<string> _initialSelectedResultUids;
    private readonly FilenameTemplateResolver _resolver = new();

    public string ExpDir { get; }

    private ExperimentFile? _experiment;
    public ExperimentFile? Experiment
    {
        get => _experiment;
        private set => SetProperty(ref _experiment, value);
    }

    public ObservableCollection<MultiExportStimulusItem> Stimuli { get; } = new();
    public ObservableCollection<MultiExportResultItem> Results { get; } = new();

    public Array Modes { get; } = Enum.GetValues(typeof(MultiExportMode));
    public Array DataFormats { get; } = Enum.GetValues(typeof(ExportDataFormat));
    public Array ImageFormats { get; } = Enum.GetValues(typeof(ExportImageFormat));

    private string _outputDir = "";
    public string OutputDir
    {
        get => _outputDir;
        set
        {
            if (SetProperty(ref _outputDir, (value ?? "").Trim()))
                OnPropertyChanged(nameof(CanStartExport));
        }
    }

    private string _filenameTemplate = "%date%_%name_result%_%name_stimul%_%type%";
    public string FilenameTemplate
    {
        get => _filenameTemplate;
        set
        {
            if (SetProperty(ref _filenameTemplate, value ?? ""))
            {
                ValidateTemplate();
                OnPropertyChanged(nameof(CanStartExport));
            }
        }
    }

    private bool _templateIsValid = true;
    public bool TemplateIsValid
    {
        get => _templateIsValid;
        private set
        {
            if (SetProperty(ref _templateIsValid, value))
            {
                OnPropertyChanged(nameof(TemplateStatusEmoji));
                OnPropertyChanged(nameof(CanStartExport));
            }
        }
    }

    private string _templateStatusText = "ОК";
    public string TemplateStatusText
    {
        get => _templateStatusText;
        private set => SetProperty(ref _templateStatusText, value);
    }

    public string TemplateStatusEmoji => TemplateIsValid ? "✓" : "✕";

    private MultiExportMode _mode = MultiExportMode.SeparateFiles;
    public MultiExportMode Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value))
            {
                ApplyConstraints();
                ApplyRecommendedTemplate();
                OnPropertyChanged(nameof(CanExportSource));
                OnPropertyChanged(nameof(CanExportImages));
                OnPropertyChanged(nameof(CanExportEdf));
                OnPropertyChanged(nameof(CanStartExport));
            }
        }
    }

    private ExportDataFormat _dataFormat = ExportDataFormat.CSV;
    /// <summary>
    /// Формат данных для экспорта (CSV или XLSX)
    /// </summary>
    public ExportDataFormat DataFormat
    {
        get => _dataFormat;
        set => SetProperty(ref _dataFormat, value);
    }

    private ExportImageFormat _imageFormat = ExportImageFormat.PNG;
    /// <summary>
    /// Формат изображений для экспорта (PNG или JPG)
    /// </summary>
    public ExportImageFormat ImageFormat
    {
        get => _imageFormat;
        set => SetProperty(ref _imageFormat, value);
    }

    // Checkbox bindings
    private bool _exportSource;
    public bool ExportSource { get => _exportSource; set => SetProperty(ref _exportSource, value); }

    private bool _exportRaw = true;
    public bool ExportRaw { get => _exportRaw; set => SetProperty(ref _exportRaw, value); }

    private bool _exportActions = true;
    public bool ExportActions { get => _exportActions; set => SetProperty(ref _exportActions, value); }

    private bool _exportAoi = true;
    public bool ExportAoi { get => _exportAoi; set => SetProperty(ref _exportAoi, value); }

    private bool _exportGazeImage = true;
    public bool ExportGazeImage { get => _exportGazeImage; set => SetProperty(ref _exportGazeImage, value); }

    private bool _exportHeatImage = true;
    public bool ExportHeatImage { get => _exportHeatImage; set => SetProperty(ref _exportHeatImage, value); }

    private bool _exportEdf;
    public bool ExportEdf { get => _exportEdf; set => SetProperty(ref _exportEdf, value); }

    // Statuses
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

    private string _statusText = "Загрузка…";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    // Constraints
    public bool CanExportSource => Mode != MultiExportMode.AllInOne;
    public bool CanExportImages => Mode == MultiExportMode.SeparateFiles || Mode == MultiExportMode.FilePerStimul;
    public bool CanExportEdf => HasEeg && Mode == MultiExportMode.SeparateFiles;

    private bool _hasEeg;
    public bool HasEeg
    {
        get => _hasEeg;
        private set
        {
            if (SetProperty(ref _hasEeg, value))
                OnPropertyChanged(nameof(CanExportEdf));
        }
    }

    public bool CanStartExport =>
        !IsBusy &&
        Stimuli.Any(s => s.IsSelected) &&
        Results.Any(r => r.IsSelected) &&
        TemplateIsValid &&
        !string.IsNullOrWhiteSpace(OutputDir);

    public int SelectedStimuliCount => Stimuli.Count(s => s.IsSelected);

    public int SelectedResultsCount => Results.Count(r => r.IsSelected);

    public MultiExportViewModel(string expDir, IReadOnlyCollection<string> initialSelectedResultUids)
    {
        ExpDir = expDir;
        _initialSelectedResultUids = new HashSet<string>(initialSelectedResultUids, StringComparer.OrdinalIgnoreCase);

        var saved = AppConfigManager.LoadMultiExportSettings();
        NormalizeSavedSettings(saved);
        _outputDir = saved.OutputDir;
        _mode = saved.Mode;
        _filenameTemplate = GetRecommendedTemplate(_mode);
        _dataFormat = saved.DataFormat;
        _imageFormat = saved.ImageFormat;
        _exportSource = saved.ExportSource;
        _exportRaw = saved.ExportRaw;
        _exportActions = saved.ExportActions;
        _exportAoi = saved.ExportAoi;
        _exportGazeImage = saved.ExportGazeImage;
        _exportHeatImage = saved.ExportHeatImage;
        _exportEdf = saved.ExportEdf;
    }

    private static void NormalizeSavedSettings(MultiExportSettings settings)
    {
        if (settings.ExportSource &&
            !settings.ExportRaw &&
            !settings.ExportActions &&
            !settings.ExportAoi &&
            !settings.ExportGazeImage &&
            !settings.ExportHeatImage)
        {
            settings.ExportSource = false;
            settings.ExportRaw = true;
            settings.ExportActions = true;
            settings.ExportAoi = true;
            settings.ExportGazeImage = true;
            settings.ExportHeatImage = true;
        }
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        StatusText = "Загрузка exp.json…";

        await Task.Run(() =>
        {
            var path = Path.Combine(ExpDir, "exp.json");
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            Experiment = JsonSerializer.Deserialize<ExperimentFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Не удалось распарсить exp.json");
        });

        HasEeg = (Experiment?.Devices ?? new List<DeviceFile>())
            .Any(d => (d.DevType ?? "").IndexOf("eeg", StringComparison.OrdinalIgnoreCase) >= 0);

        LoadStimuli();
        LoadResults();

        ApplyConstraints();
        ValidateTemplate();

        IsBusy = false;
        UpdateStatusText();
        OnPropertyChanged(nameof(CanStartExport));
    }

    public void ValidateTemplate()
    {
        if (Experiment == null)
        {
            TemplateIsValid = false;
            TemplateStatusText = "exp.json не загружен";
            return;
        }

        if (_resolver.TryValidate(FilenameTemplate, Experiment, out var err))
        {
            TemplateIsValid = true;
            TemplateStatusText = "ОК";
        }
        else
        {
            TemplateIsValid = false;
            TemplateStatusText = err;
        }
    }

    public MultiExportOptions BuildOptions()
    {
        return new MultiExportOptions
        {
            OutputDir = OutputDir,
            FilenameTemplate = FilenameTemplate,
            Mode = Mode,
            DataFormat = DataFormat,
            ImageFormat = ImageFormat,

            ExportSource = ExportSource,
            ExportRaw = ExportRaw,
            ExportActions = ExportActions,
            ExportAoi = ExportAoi,
            ExportGazeImage = ExportGazeImage,
            ExportHeatImage = ExportHeatImage,
            ExportEdf = ExportEdf
        };
    }

    public MultiExportSettings ToSettings()
    {
        return new MultiExportSettings
        {
            OutputDir = OutputDir,
            FilenameTemplate = FilenameTemplate,
            Mode = Mode,
            DataFormat = DataFormat,
            ImageFormat = ImageFormat,

            ExportSource = ExportSource,
            ExportRaw = ExportRaw,
            ExportActions = ExportActions,
            ExportAoi = ExportAoi,
            ExportGazeImage = ExportGazeImage,
            ExportHeatImage = ExportHeatImage,
            ExportEdf = ExportEdf
        };
    }

    public IReadOnlyList<StimulFile> GetSelectedStimuli()
        => Stimuli.Where(s => s.IsSelected).Select(s => s.Stimulus).ToList();

    public IReadOnlyList<MultiExportResult> GetSelectedResults()
        => Results.Where(r => r.IsSelected).Select(r => new MultiExportResult(r.Uid, r.Result)).ToList();

    public void SetAllStimuliSelected(bool selected)
    {
        foreach (var s in Stimuli) s.IsSelected = selected;
        OnPropertyChanged(nameof(SelectedStimuliCount));
        OnPropertyChanged(nameof(CanStartExport));
        UpdateStatusText();
    }

    public void SetAllResultsSelected(bool selected)
    {
        foreach (var r in Results) r.IsSelected = selected;
        OnPropertyChanged(nameof(SelectedResultsCount));
        OnPropertyChanged(nameof(CanStartExport));
        UpdateStatusText();
    }

    private void ApplyConstraints()
    {
        // картинки — только SeparateFiles / FilePerStimul
        if (!CanExportImages)
        {
            ExportGazeImage = false;
            ExportHeatImage = false;
        }

        // EDF — только SeparateFiles + EEG
        if (!CanExportEdf)
            ExportEdf = false;

        // Source запрещен в AllInOne
        if (!CanExportSource)
            ExportSource = false;
    }

    private void ApplyRecommendedTemplate()
    {
        var recommended = GetRecommendedTemplate(Mode);
        if (string.Equals(FilenameTemplate, recommended, StringComparison.Ordinal))
            return;

        FilenameTemplate = recommended;
    }

    private static string GetRecommendedTemplate(MultiExportMode mode)
    {
        return mode switch
        {
            MultiExportMode.SeparateFiles => "%name_stimul%_%name_result%_%type%",
            MultiExportMode.FilePerStimul => "%name_stimul%_%type%",
            MultiExportMode.FilePerResult => "%name_result%_%type%",
            MultiExportMode.AllInOne => "%type%",
            _ => "%name_stimul%_%name_result%_%type%"
        };
    }

    private void LoadStimuli()
    {
        Stimuli.Clear();

        if (Experiment == null) return;

        // фильтр: IMAGE (мягко: по расширению файла)
        foreach (var st in Experiment.Stimuls ?? new List<StimulFile>())
        {
            // kind=0 — калибровка
            if ((st.Kind ?? 0) == 0) continue;

            var resolvedPath = ResolveStimulusPath(ExpDir, st.Uid, st.Filename);
            if (string.IsNullOrWhiteSpace(resolvedPath)) continue;
            if (!IsImageFile(resolvedPath)) continue;

            var item = new MultiExportStimulusItem(st, isSelected: true, resolvedPath);
            item.PropertyChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(CanStartExport));
                OnPropertyChanged(nameof(SelectedStimuliCount));
                UpdateStatusText();
            };
            Stimuli.Add(item);
        }
    }

    private void LoadResults()
    {
        Results.Clear();

        var dir = Path.Combine(ExpDir, "results");
        if (!Directory.Exists(dir)) return;

        var items = new List<MultiExportResultItem>();

        foreach (var rdir in Directory.EnumerateDirectories(dir))
        {
            var uid = Path.GetFileName(rdir);
            var rjson = Path.Combine(rdir, "result.json");
            if (!File.Exists(rjson)) continue;

            ResultFile? rf = null;
            try
            {
                rf = JsonSerializer.Deserialize<ResultFile>(File.ReadAllText(rjson), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { }

            if (rf == null) continue;

            bool preselect = _initialSelectedResultUids.Count == 0 || _initialSelectedResultUids.Contains(uid);

            var item = new MultiExportResultItem(uid, rf, preselect);
            item.PropertyChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(CanStartExport));
                OnPropertyChanged(nameof(SelectedResultsCount));
                UpdateStatusText();
            };
            items.Add(item);
        }

        // Сортируем по дате (новые сверху)
        foreach (var item in items.OrderByDescending(r => r.Date))
        {
            Results.Add(item);
        }
    }

    private static bool IsImageFile(string pathOrName)
    {
        var ext = Path.GetExtension(pathOrName).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
    }

    private static string? ResolveStimulusPath(string expDir, string uid, string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;

        var p1 = Path.Combine(expDir, uid, filename);
        if (File.Exists(p1)) return p1;

        var p2 = Path.Combine(expDir, filename);
        if (File.Exists(p2)) return p2;

        try
        {
            return Directory.EnumerateFiles(expDir, filename, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private void UpdateStatusText()
    {
        StatusText = $"Выбрано: стимулы={SelectedStimuliCount}/{Stimuli.Count}, результаты={SelectedResultsCount}/{Results.Count}";
    }
}
