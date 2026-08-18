namespace TubeForge.Core.Media;

public sealed record StreamFormat
{
    public required int FormatId { get; init; }

    public required Uri Url { get; init; }

    public string? HttpUserAgent { get; init; }

    public required MediaContainer Container { get; init; }

    public required StreamKind Kind { get; init; }

    public VideoCodec VideoCodec { get; init; }

    public AudioCodec AudioCodec { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public int? FramesPerSecond { get; init; }

    public long? Bitrate { get; init; }

    public long? ContentLength { get; init; }

    public int? AudioSampleRate { get; init; }

    public int? AudioChannels { get; init; }

    /// <summary>
    /// True when the provider published this track with dynamic range compression applied.
    /// A DRC track is a loudness-normalised duplicate of an original track that carries the same
    /// format identifier and a marginally higher declared bitrate, so it must never win a
    /// bitrate comparison against the original.
    /// </summary>
    public bool IsDrc { get; init; }

    /// <summary>Audio track language identifier, when the provider exposes multiple tracks.</summary>
    public string? AudioLanguage { get; init; }

    /// <summary>True when the provider marks this audio track as the video's original language.</summary>
    public bool IsOriginalAudio { get; init; }

    public bool IsHdr { get; init; }

    public string QualityLabel { get; init; } = string.Empty;

    public bool IsLiveHls { get; init; }

    public bool IsLiveManifestPending { get; init; }

    public bool HasVideo => Kind is StreamKind.Progressive or StreamKind.VideoOnly;

    public bool HasAudio => Kind is StreamKind.Progressive or StreamKind.AudioOnly;
}
