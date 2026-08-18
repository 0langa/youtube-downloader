using TubeForge.Core.Media;

namespace TubeForge.YouTube.Extraction;

public sealed record WatchPageData(
    VideoMetadata Metadata,
    Uri? PlayerScriptUrl,
    int CipheredFormatCount,
    string PlayabilityStatus,
    ExtractionDiagnostics? Diagnostics = null);

public sealed record ExtractionDiagnostics(
    string Stage,
    int TransformPlanCount = 0,
    int ProbeAttemptCount = 0,
    IReadOnlyList<ClientProbeOutcome>? ClientOutcomes = null);

/// <summary>
/// Records what happened to one provider client during analysis. A client that answers with a
/// full ladder but whose media rejects ranged reads must be excluded, and without this record
/// that exclusion is invisible — the ladder simply looks smaller than it should.
/// </summary>
public sealed record ClientProbeOutcome(
    string Client,
    ClientProbeResult Result,
    int FormatCount = 0);

public enum ClientProbeResult
{
    Accepted,
    NoResponse,
    NoFormats,
    LiveManifestMissing,
    MediaUnreachable
}
