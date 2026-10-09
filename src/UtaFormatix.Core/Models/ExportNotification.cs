namespace UtaFormatix.Core.Models;

public abstract record ExportNotification
{
    public sealed record PhonemeResetRequiredVsq : ExportNotification;
    public sealed record PhonemeResetRequiredV4 : ExportNotification;
    public sealed record PhonemeResetRequiredV5 : ExportNotification;
    public sealed record TimeSignatureIgnored : ExportNotification;
    public sealed record PitchDataExported : ExportNotification;
    public sealed record DataOverLengthLimitIgnored : ExportNotification;
}
