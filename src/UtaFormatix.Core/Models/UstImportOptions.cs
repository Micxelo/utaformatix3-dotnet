namespace UtaFormatix.Core.Models;

public sealed record UstImportOptions(string? Encoding = null)
{
    // Shared across files; counts source/control points and generated pitch samples.
    public int MaxPitchSamples { get; init; } = 10_000_000;
}
