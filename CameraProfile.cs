namespace NeuroBureau.Experiment;

public sealed class CameraProfile
{
    public string DeviceId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string InputFormat { get; set; } = string.Empty;
    public string VideoSize { get; set; } = string.Empty;
    public string Framerate { get; set; } = string.Empty;
    public string? Rtbufsize { get; set; }

    public string FpsDisplay => string.IsNullOrWhiteSpace(Framerate) ? "auto" : Framerate.Trim();

    public string ToDisplayString()
        => string.IsNullOrWhiteSpace(VideoSize)
            ? $"{FpsDisplay} fps (авто-размер)"
            : $"{VideoSize} @ {FpsDisplay} fps";
}
