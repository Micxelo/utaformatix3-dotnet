namespace UtaFormatix.Core.Process;

internal static class Interpolation
{
    public static List<(long Tick, double Value)> InterpolateLinear(
        this List<(long Tick, double Value)> points, long samplingIntervalTick) =>
        points.Interpolate(samplingIntervalTick, (x0, y0, x1, y1, x) =>
        {
            var value = y0 + (x - x0) * (y1 - y0) / (x1 - x0);
            if (double.IsFinite(value)) return value;
            // The unscaled product or difference can overflow even though
            // the weighted sum of two finite endpoints is representable.
            var fraction = (double)(x - x0) / (x1 - x0);
            return (1.0 - fraction) * y0 + fraction * y1;
        });

    public static List<(long Tick, double Value)> InterpolateCosineEaseInOut(
        this List<(long Tick, double Value)> points, long samplingIntervalTick) =>
        points.Interpolate(samplingIntervalTick, (x0, y0, x1, y1, x) =>
        {
            var yOffset = (y0 + y1) / 2.0;
            var aFreq = Math.PI / (x1 - x0);
            var amp = (y0 - y1) / 2.0;
            return amp * Math.Cos(aFreq * (x - x0)) + yOffset;
        });

    public static List<(long Tick, double Value)> InterpolateCosineEaseIn(
        this List<(long Tick, double Value)> points, long samplingIntervalTick) =>
        points.Interpolate(samplingIntervalTick, (x0, y0, x1, y1, x) =>
        {
            var yOffset = y1;
            var aFreq = Math.PI / (x1 - x0) / 2.0;
            var amp = y0 - y1;
            return amp * Math.Cos(aFreq * (x - x0)) + yOffset;
        });

    public static List<(long Tick, double Value)> InterpolateCosineEaseOut(
        this List<(long Tick, double Value)> points, long samplingIntervalTick) =>
        points.Interpolate(samplingIntervalTick, (x0, y0, x1, y1, x) =>
        {
            var yOffset = y0;
            var aFreq = Math.PI / (x1 - x0) / 2.0;
            var amp = y0 - y1;
            var phase = Math.PI / 2.0;
            return amp * Math.Cos(aFreq * (x - x0) + phase) + yOffset;
        });

    private static List<(long Tick, double Value)> Interpolate(
        this List<(long Tick, double Value)> points,
        long samplingIntervalTick,
        Func<long, double, long, double, long, double> mapping)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samplingIntervalTick);
        if (points.Count == 0) return points;

        var result = new List<(long Tick, double Value)>();

        for (var i = 0; i < points.Count - 1; i++)
        {
            var (x0, y0) = points[i];
            var (x1, y1) = points[i + 1];

            result.Add((x0, y0));

            // Compare the distance before advancing, including at Int64.MaxValue.
            for (var x = x0; x < x1 && unchecked((ulong)x1 - (ulong)x) > (ulong)samplingIntervalTick;)
            {
                x += samplingIntervalTick;
                result.Add((x, mapping(x0, y0, x1, y1, x)));
            }
        }

        result.Add(points[^1]);
        return result;
    }
}
