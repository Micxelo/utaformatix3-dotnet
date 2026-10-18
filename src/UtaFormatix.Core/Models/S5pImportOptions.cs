namespace UtaFormatix.Core.Models;

public sealed record S5pImportOptions(
    bool Recover = false,
    bool AllowNumericStrings = false,
    long MaxFileBytes = 256L * 1024 * 1024,
    int MaxDepth = 128,
    int MaxJsonTokens = 10_000_000,
    int MaxPitchSamples = 10_000_000);
