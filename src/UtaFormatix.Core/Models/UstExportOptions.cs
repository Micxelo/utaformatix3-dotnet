namespace UtaFormatix.Core.Models;

public sealed record UstExportOptions(string Encoding = "shift_jis")
{
    // Shared across tracks; includes source points, resampling and simplification work.
    public int MaxPitchSamples { get; init; } = 10_000_000;
}
