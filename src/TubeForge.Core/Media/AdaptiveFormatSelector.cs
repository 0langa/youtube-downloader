namespace TubeForge.Core.Media;

public sealed record AudioVideoSelection(StreamFormat Video, StreamFormat? Audio)
{
    public bool RequiresMuxing => Audio is not null;

    public MediaContainer OutputContainer => Audio is null
        ? Video.Container
        : AdaptiveFormatSelector.ResolveOutputContainer(Video, Audio) ?? Video.Container;

    public long? ExpectedLength => Video.ContentLength is not null && Audio?.ContentLength is not null
        ? Video.ContentLength.Value + Audio.ContentLength.Value
        : RequiresMuxing ? null : Video.ContentLength;
}

public static class AdaptiveFormatSelector
{
    public static AudioVideoSelection? SelectBest(IEnumerable<StreamFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        var materialized = formats.ToArray();
        var audioFormats = materialized
            .Where(format => format.Kind == StreamKind.AudioOnly)
            .ToArray();

        foreach (var video in materialized
                     .Where(format => format.Kind == StreamKind.VideoOnly)
                     .OrderByDescending(format => format.Height ?? 0)
                     .ThenByDescending(format => format.FramesPerSecond ?? 0)
                     .ThenByDescending(format => format.IsHdr)
                     .ThenByDescending(format => format.Container == MediaContainer.Mp4)
                     .ThenByDescending(format => format.Bitrate ?? 0)
                     .ThenBy(format => format.FormatId))
        {
            var audio = SelectCompanionAudio(video, audioFormats);
            if (audio is not null)
            {
                return new AudioVideoSelection(video, audio);
            }
        }

        var progressive = FormatRanker.RecommendedProgressive(materialized);
        return progressive is null ? null : new AudioVideoSelection(progressive, null);
    }

    /// <summary>
    /// Selects the highest-quality audio track that can be stream-copied with
    /// <paramref name="video"/>. Native MP4/WebM output wins only when bitrate and sample rate
    /// are equal; a higher-quality cross-container track is muxed losslessly into Matroska.
    /// </summary>
    public static StreamFormat? SelectCompanionAudio(
        StreamFormat video,
        IEnumerable<StreamFormat> audioFormats)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(audioFormats);
        return audioFormats
            .Where(audio => AreMkvMuxCompatible(video, audio))
            // Loudness-compressed and dubbed tracks are ranked out before bitrate is considered:
            // a DRC duplicate always declares a marginally higher bitrate than its original.
            .OrderBy(audio => audio.IsDrc)
            .ThenByDescending(audio => audio.IsOriginalAudio)
            .ThenByDescending(audio => audio.Bitrate ?? 0)
            .ThenByDescending(audio => audio.AudioSampleRate ?? 0)
            .ThenByDescending(audio => audio.AudioChannels ?? 0)
            .ThenByDescending(audio => AreMuxCompatible(video, audio))
            .ThenByDescending(audio => audio.AudioCodec == AudioCodec.Opus)
            .ThenBy(audio => audio.FormatId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Resolves the lossless output container for a video/audio pair: the shared native container
    /// when the pair is natively muxable, otherwise Matroska. Returns null when the pair cannot be
    /// combined without re-encoding.
    /// </summary>
    public static MediaContainer? ResolveOutputContainer(StreamFormat video, StreamFormat audio)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(audio);
        if (AreMuxCompatible(video, audio))
        {
            return video.Container;
        }

        return AreMkvMuxCompatible(video, audio) ? MediaContainer.Mkv : null;
    }

    public static bool AreMuxCompatible(StreamFormat video, StreamFormat audio)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(audio);
        if (video.Kind != StreamKind.VideoOnly || audio.Kind != StreamKind.AudioOnly ||
            video.Container != audio.Container)
        {
            return false;
        }

        return video.Container switch
        {
            MediaContainer.Mp4 =>
                video.VideoCodec is VideoCodec.H264 or VideoCodec.Av1 &&
                audio.AudioCodec == AudioCodec.Aac,
            MediaContainer.WebM =>
                video.VideoCodec is VideoCodec.Vp9 or VideoCodec.Av1 &&
                audio.AudioCodec is AudioCodec.Opus or AudioCodec.Vorbis,
            _ => false
        };
    }

    /// <summary>
    /// Any decoded video codec combined with any decoded audio codec can be stream-copied into
    /// Matroska without re-encoding. Used as the universal cross-container fallback.
    /// </summary>
    public static bool AreMkvMuxCompatible(StreamFormat video, StreamFormat audio)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(audio);
        return video.Kind == StreamKind.VideoOnly &&
            audio.Kind == StreamKind.AudioOnly &&
            video.VideoCodec is VideoCodec.H264 or VideoCodec.Vp9 or VideoCodec.Av1 &&
            audio.AudioCodec is AudioCodec.Aac or AudioCodec.Opus or AudioCodec.Vorbis;
    }
}
