namespace NeuroBureau.Experiment;

internal sealed class CameraProfile
{
    public string DeviceId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string InputFormat { get; set; } = string.Empty;
    public string VideoSize { get; set; } = string.Empty;
    public string Framerate { get; set; } = string.Empty;
    public string? Rtbufsize { get; set; }
    public string ToDisplayString()
        => string.IsNullOrWhiteSpace(VideoSize)
            ? $"{Fps} fps (авто-размер)"
            : $"{VideoSize} @ {Fps} fps";
}

