namespace UtaFormatix.Core.Models;

public sealed record Pitch(List<(long Tick, double? Value)> Data, bool IsAbsolute)
{
    public Pitch() : this([], false) { }
}
