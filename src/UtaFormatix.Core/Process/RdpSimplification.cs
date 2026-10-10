namespace UtaFormatix.Core.Process;

internal static class RdpSimplification
{
    private static double PerpendicularDistance(
        (long Tick, double Value) pt,
        (long Tick, double Value) lineStart,
        (long Tick, double Value) lineEnd)
    {
        var dx = (double)(lineEnd.Tick - lineStart.Tick);
        var dy = lineEnd.Value - lineStart.Value;

        var mag = Math.Sqrt(dx * dx + dy * dy);
        if (mag > 0.0)
        {
            dx /= mag;
            dy /= mag;
        }

        var pvx = pt.Tick - lineStart.Tick;
        var pvy = pt.Value - lineStart.Value;

        var pvdot = dx * pvx + dy * pvy;

        var ax = pvx - pvdot * dx;
        var ay = pvy - pvdot * dy;

        return Math.Sqrt(ax * ax + ay * ay);
    }

    public static List<(long Tick, double Value)> SimplifyShape(
        List<(long Tick, double Value)> pointList, double epsilon)
    {
        if (pointList.Count < 2) return pointList;

        var dmax = 0.0;
        var index = 0;
        var end = pointList.Count - 1;

        for (var i = 1; i < end; i++)
        {
            var d = PerpendicularDistance(pointList[i], pointList[0], pointList[end]);
            if (d > dmax)
            {
                index = i;
                dmax = d;
            }
        }

        if (dmax > epsilon)
        {
            var firstLine = pointList.Take(index + 1).ToList();
            var lastLine = pointList.Skip(index).ToList();
            var recResults1 = SimplifyShape(firstLine, epsilon);
            var recResults2 = SimplifyShape(lastLine, epsilon);

            return recResults1.Take(recResults1.Count - 1).Concat(recResults2).ToList();
        }

        return [pointList[0], pointList[^1]];
    }

    public static List<(long Tick, double Value)> SimplifyShapeTo(
        List<(long Tick, double Value)> pointList, long maxPointCount)
    {
        const double step = 0.05;
        var epsilon = step;
        while (true)
        {
            var result = SimplifyShape(pointList, epsilon);
            if (result.Count < maxPointCount) return result;
            epsilon += step;
        }
    }
}
