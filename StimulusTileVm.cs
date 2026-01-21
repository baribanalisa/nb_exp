using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
namespace NeuroBureau.Experiment;

public sealed class StimulusTileVm
{
    public string Title { get; init; } = "";
    public bool IsVideo { get; init; }
    public bool HasFile { get; init; }
    public ImageSource? Thumbnail { get; init; }
    public Brush PreviewBackground { get; init; } = Brushes.Transparent;

    // пригодится позже (клик по плитке и т.п.)
    public string? FilePath { get; init; }
    public string? Uid { get; init; }
}
