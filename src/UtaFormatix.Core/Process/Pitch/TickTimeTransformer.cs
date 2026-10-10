using UtaFormatix.Core.Models;

namespace UtaFormatix.Core.Process.Pitch;

internal static class TempoExtensions
{
    public static double BpmToSecPerTick(this double bpm) =>
        60.0 / Constants.TicksInBeat / bpm;
}

internal sealed class TickTimeTransformer
{
    private sealed class Segment(long rangeStart, long rangeEnd, double offset, double secPerTick)
    {
        public long RangeStart { get; } = rangeStart;
        public long RangeEnd { get; } = rangeEnd;
        public double Offset { get; } = offset;
        public double SecPerTick { get; } = secPerTick;
    }

    private readonly List<Segment> _segments;

    public TickTimeTransformer(List<Tempo> tempos)
    {
        var segments = new List<Segment>();

        for (var i = 0; i < tempos.Count; i++)
        {
            var thisTempo = tempos[i];
            var thisTick = thisTempo.TickPosition;
            var nextTick = i + 1 < tempos.Count ? tempos[i + 1].TickPosition : long.MaxValue;
            var rate = thisTempo.Bpm.BpmToSecPerTick();

            if (segments.Count == 0)
            {
                segments.Add(new Segment(thisTick, nextTick, 0.0, rate));
            }
            else
            {
                var last = segments[^1];
                var offset = last.Offset + (last.RangeEnd - last.RangeStart) * last.SecPerTick;
                segments.Add(new Segment(thisTick, nextTick, offset, rate));
            }
        }

        _segments = segments;
    }

    public double TickToSec(long tick)
    {
        var seg = FindSegmentForTick(tick);
        return seg.Offset + (tick - seg.RangeStart) * seg.SecPerTick;
    }

    public double TickToMilliSec(long tick) => TickToSec(tick) * 1000.0;

    public double TickDistanceToSec(long tickStart, long tickEnd) =>
        TickToSec(tickEnd) - TickToSec(tickStart);

    public double TickDistanceToMilliSec(long tickStart, long tickEnd) =>
        TickDistanceToSec(tickStart, tickEnd) * 1000.0;

    public long SecToTick(double sec)
    {
        var seg = _segments.LastOrDefault(s => s.Offset <= sec) ?? _segments[0];
        return (long)((sec - seg.Offset) / seg.SecPerTick) + seg.RangeStart;
    }

    public long MilliSecToTick(double milliSec) => SecToTick(milliSec / 1000.0);

    private Segment FindSegmentForTick(long tick)
    {
        foreach (var seg in _segments)
        {
            if (tick >= seg.RangeStart && tick < seg.RangeEnd)
                return seg;
        }
        return _segments[0];
    }
}
