namespace UtaFormatix.Core.Models;

/// <summary>Limits pitch processing work across all tracks in a USTX conversion.</summary>
public sealed record UstxPitchOptions(int MaxPitchSamples = 10_000_000);
