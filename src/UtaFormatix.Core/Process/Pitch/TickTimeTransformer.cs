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
    private readonly bool _orderedTimeline;

    public TickTimeTransformer(List<Tempo> tempos, Action<int, double, double>? validateSegment = null)
    {
        _orderedTimeline = validateSegment is not null;
        var segments = new List<Segment>();

        for (var i = 0; i < tempos.Count; i++)
        {
            var thisTempo = tempos[i];
            var thisTick = thisTempo.TickPosition;
            var nextTick = i + 1 < tempos.Count ? tempos[i + 1].TickPosition : long.MaxValue;
            var rate = thisTempo.Bpm.BpmToSecPerTick();

            var offset = 0.0;
            if (segments.Count > 0)
            {
                var previous = segments[^1];
                offset = previous.Offset + (previous.RangeEnd - previous.RangeStart) * previous.SecPerTick;
            }
            validateSegment?.Invoke(i, rate, offset);
            segments.Add(new Segment(thisTick, nextTick, offset, rate));
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

    public long SecToTickChecked(double sec)
    {
        var seg = _orderedTimeline
            ? FindLastSegment(sec, static (segment, value) => segment.Offset <= value)
            : _segments.LastOrDefault(s => s.Offset <= sec) ?? _segments[0];
        return checked(checked((long)((sec - seg.Offset) / seg.SecPerTick)) + seg.RangeStart);
    }

    public long MilliSecToTick(double milliSec) => SecToTick(milliSec / 1000.0);

    private Segment FindSegmentForTick(long tick)
    {
        if (_orderedTimeline)
            return FindLastSegment(tick, static (segment, value) => segment.RangeStart <= value);
        foreach (var seg in _segments)
        {
            if (tick >= seg.RangeStart && tick < seg.RangeEnd)
                return seg;
        }
        return _segments[0];
    }

    private Segment FindLastSegment<T>(T value, Func<Segment, T, bool> atOrBefore)
    {
        var start = 0;
        var end = _segments.Count;
        while (start < end)
        {
            var mid = start + (end - start) / 2;
            if (atOrBefore(_segments[mid], value)) start = mid + 1;
            else end = mid;
        }
        return _segments[Math.Max(0, start - 1)];
    }
}
