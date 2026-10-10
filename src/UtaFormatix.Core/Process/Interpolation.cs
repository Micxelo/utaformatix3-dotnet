namespace UtaFormatix.Core.Process;

internal static class Interpolation
{
    public static List<(long Tick, double Value)> InterpolateLinear(
        this List<(long Tick, double Value)> points, long samplingIntervalTick) =>
        points.Interpolate(samplingIntervalTick, (x0, y0, x1, y1, x) =>
            y0 + (x - x0) * (y1 - y0) / (x1 - x0));

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
        if (points.Count == 0) return points;

        var result = new List<(long Tick, double Value)>();

        for (var i = 0; i < points.Count - 1; i++)
        {
            var (x0, y0) = points[i];
            var (x1, y1) = points[i + 1];

            result.Add((x0, y0));

            for (var x = x0 + 1; x < x1; x++)
            {
                if ((x - x0) % samplingIntervalTick == 0)
                    result.Add((x, mapping(x0, y0, x1, y1, x)));
            }
        }

        result.Add(points[^1]);
        return result;
    }
}
