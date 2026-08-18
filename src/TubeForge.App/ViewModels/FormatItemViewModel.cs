using TubeForge.Core.Media;

namespace TubeForge.App.ViewModels;

public sealed record FormatItemViewModel(StreamFormat Format, StreamFormat? AudioFormat = null)
{
    public bool RequiresMuxing => AudioFormat is not null;

    public MediaContainer OutputContainer => AudioFormat is null
        ? Format.Container
        : AdaptiveFormatSelector.ResolveOutputContainer(Format, AudioFormat) ?? Format.Container;

    public string Label => FormatDisplay.Label(DisplayFormat());

    public string TechnicalLabel => string.Join(" · ", new[]
    {
        Format.Kind switch
        {
            StreamKind.Progressive => "ready to play",
            StreamKind.AudioOnly => "native audio",
            StreamKind.VideoOnly when RequiresMuxing => OutputContainer switch
            {
                MediaContainer.Mkv => "MKV stream copy; no re-encoding",
                MediaContainer.WebM => "WebM stream copy; no re-encoding",
                _ => "indexed MP4 stream copy; no re-encoding"
            },
            StreamKind.VideoOnly when Format.Container == MediaContainer.Mp4 =>
                "video track only; indexed MP4 stream copy",
            StreamKind.VideoOnly => "video track only",
            _ => "stream"
        },
        VideoCodecLabel(),
        AudioCodecLabel(),
        RequiresMuxing && AudioFormat?.Bitrate is > 0
            ? $"{Math.Round(AudioFormat.Bitrate.Value / 1000d):0} kbps audio"
            : string.Empty,
        AudioChannelLabel()
    }.Where(value => !string.IsNullOrEmpty(value)));

    public string SizeLabel => CombinedLength() is > 0
        ? FormatSize(CombinedLength()!.Value)
        : "size unknown";

    public string AutomationName => $"{Label}; {TechnicalLabel}; {SizeLabel}";

    public override string ToString() => AutomationName;

    private string VideoCodecLabel() => Format.VideoCodec switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.Vp9 => "VP9",
        VideoCodec.Av1 => "AV1",
        VideoCodec.Unknown => "video codec unknown",
        _ => string.Empty
    };

    private string AudioCodecLabel() => (AudioFormat?.AudioCodec ?? Format.AudioCodec) switch
    {
        AudioCodec.Aac => "AAC",
        AudioCodec.Opus => "Opus",
        AudioCodec.Vorbis => "Vorbis",
        AudioCodec.Unknown => "audio codec unknown",
        _ => string.Empty
    };

    /// <summary>
    /// Names multichannel audio so a surround track is a visible, deliberate choice rather than a
    /// surprise in what the listener expected to be a stereo download.
    /// </summary>
    private string AudioChannelLabel() => (AudioFormat ?? Format).AudioChannels switch
    {
        null or <= 2 => string.Empty,
        6 => "5.1 surround",
        8 => "7.1 surround",
        var channels => $"{channels} channels"
    };

    private StreamFormat DisplayFormat() => RequiresMuxing
        ? Format with
        {
            Kind = StreamKind.Progressive,
            Container = OutputContainer,
            AudioCodec = AudioFormat!.AudioCodec,
            ContentLength = CombinedLength()
        }
        : Format;

    private long? CombinedLength() => Format.ContentLength is not null && AudioFormat?.ContentLength is not null
        ? Format.ContentLength.Value + AudioFormat.ContentLength.Value
        : Format.ContentLength;

    private static string FormatSize(long bytes)
    {
        var megabytes = bytes / 1024d / 1024d;
        return megabytes >= 1024
            ? $"{megabytes / 1024:0.00} GB"
            : $"{megabytes:0.#} MB";
    }
}
