// File: MultiExportStimulusItem.cs
using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace NeuroBureau.Experiment;

public sealed class MultiExportStimulusItem : ObservableObject
{
    public StimulFile Stimulus { get; }

    public string Uid => Stimulus.Uid;
    public string Filename => Stimulus.Filename ?? "";
    public string DisplayName => string.IsNullOrWhiteSpace(Stimulus.Filename) 
        ? Uid 
        : Path.GetFileNameWithoutExtension(Stimulus.Filename);

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    private BitmapImage? _thumbnail;
    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    public MultiExportStimulusItem(StimulFile stimulus, bool isSelected, string? stimuliDir = null)
    {
        Stimulus = stimulus;
        _isSelected = isSelected;
        
        // Генерируем миниатюру
        if (!string.IsNullOrWhiteSpace(stimuliDir) && !string.IsNullOrWhiteSpace(stimulus.Filename))
        {
            var filePath = Path.Combine(stimuliDir, stimulus.Filename);
            _thumbnail = GenerateThumbnail(filePath);
        }
    }

    private static BitmapImage? GenerateThumbnail(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp"))
                return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 120; // Оптимизация для миниатюры
            bmp.UriSource = new Uri(filePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
