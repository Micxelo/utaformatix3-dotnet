namespace UtaFormatix.Core.Process;

internal static class Resampling
{
    public static List<(long Tick, double? Value)> Resampled(
        this List<(long Tick, double? Value)> points,
        long interval,
        Func<(long Tick, double? Value)?, (long Tick, double? Value)?, long, double?> interpolateMethod)
    {
        if (points.Count == 0) return [];

        var leftBound = points.Min(p => p.Tick);
        var rightBound = points.Max(p => p.Tick);

        var result = new List<(long Tick, double? Value)>();

        for (var current = leftBound; current <= rightBound; current += interval)
        {
            (long Tick, double? Value)? prev = null;
            for (var i = points.Count - 1; i >= 0; i--)
            {
                if (points[i].Tick <= current) { prev = points[i]; break; }
            }

            (long Tick, double? Value)? next = null;
            for (var i = 0; i < points.Count; i++)
            {
                if (points[i].Tick >= current) { next = points[i]; break; }
            }

            result.Add((current, interpolateMethod(prev, next, current)));
        }

        return result;
    }

    public static List<(long Tick, double? Value)> DotResampled(
        this List<(long Tick, double? Value)> points, long interval) =>
        points.Resampled(interval, (prev, next, _) =>
            prev?.Value ?? next?.Value);
}
