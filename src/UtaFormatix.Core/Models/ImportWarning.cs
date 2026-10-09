namespace UtaFormatix.Core.Models;

public abstract record ImportWarning
{
    public sealed record TempoNotFound : ImportWarning;

    public sealed record TempoIgnoredInFile(string FilePath, Tempo TempoValue) : ImportWarning;

    public sealed record TempoIgnoredInTrack(int TrackIndex, Tempo TempoValue) : ImportWarning;

    public sealed record TempoIgnoredInPreMeasure(Tempo TempoValue) : ImportWarning;

    public sealed record DefaultTempoFixed(double OriginalBpm) : ImportWarning;

    public sealed record TimeSignatureNotFound : ImportWarning;

    public sealed record TimeSignatureIgnoredInTrack(int TrackIndex, TimeSignature TimeSignatureValue) : ImportWarning;

    public sealed record TimeSignatureIgnoredInPreMeasure(TimeSignature TimeSignatureValue) : ImportWarning;

    public sealed record IncompatibleFormatSerializationVersion(
        string CurrentVersion,
        string DataVersion) : ImportWarning;
}
