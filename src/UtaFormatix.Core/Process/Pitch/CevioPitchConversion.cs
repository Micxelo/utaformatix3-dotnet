using UtaFormatix.Core.Models;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.Process.Pitch;

public sealed class CevioTrackPitchData
{
    public sealed record Event(long? Index, long? Repeat, double Value);

    public List<Event> Events { get; }
    public List<Tempo> Tempos { get; }
    public long TickPrefix { get; }

    public CevioTrackPitchData(List<Event> events, List<Tempo> tempos, long tickPrefix)
    {
        Events = events;
        Tempos = tempos;
        TickPrefix = tickPrefix;
    }
}

internal sealed record EventDouble(double? Index, double? Repeat, double? Value)
{
    public CevioTrackPitchData.Event Round() =>
        new(
            Index.HasValue ? (long)Math.Round(Index.Value, MidpointRounding.ToPositiveInfinity) : null,
            Repeat.HasValue ? (long)Math.Round(Repeat.Value, MidpointRounding.ToPositiveInfinity) : null,
            Value!.Value);

    public static EventDouble From(CevioTrackPitchData.Event e) =>
        new(e.Index.HasValue ? (double)e.Index.Value : null, e.Repeat.HasValue ? (double)e.Repeat.Value : null, e.Value);
}

internal static class CevioPitchConversion
{
    private const double TimeUnitAsTicksPerBpm = 4.8 / 120.0;
    private const long MinDataLength = 500;
    private const double TempValueAsNull = -1.0;

    public static PitchModel? PitchFromCevioTrack(CevioTrackPitchData data)
    {
        var convertedPoints = new List<(double Pos, double? Value)>();
        double? currentValue = null;

        var withEndings = AppendEndingPoints(data);
        var normalized = NormalizeToTick(withEndings);
        var eventsNormalized = ShapeEvents(normalized);

        double? nextPos = null;
        foreach (var ev in eventsNormalized)
        {
            var pos = ev.Index!.Value - data.TickPrefix;
            var length = ev.Repeat!.Value;
            double? value = ev.Value.HasValue ? ev.Value.Value.LoggedFrequencyToKey() : null;
            if (value != currentValue || nextPos != pos)
            {
                convertedPoints.Add((pos, value));
                currentValue = value;
            }
            nextPos = pos + length;
        }

        var lastMinusPosIndex = -1;
        double? lastMinusPosValue = null;
        for (var i = convertedPoints.Count - 1; i >= 0; i--)
        {
            if (convertedPoints[i].Pos < 0 && convertedPoints[i].Value is not null)
            {
                lastMinusPosIndex = i;
                lastMinusPosValue = convertedPoints[i].Value;
                break;
            }
        }
        if (lastMinusPosIndex >= 0)
        {
            convertedPoints.RemoveRange(0, lastMinusPosIndex + 1);
            if (convertedPoints.Count > 0 && convertedPoints[0].Pos > 0)
                convertedPoints.Insert(0, (0.0, lastMinusPosValue));
        }

        var pitchData = convertedPoints
            .Select(p => ((long)Math.Round(p.Pos, MidpointRounding.ToPositiveInfinity), p.Value))
            .ToList();
        return pitchData.Count > 0 ? new PitchModel(pitchData, IsAbsolute: true) : null;
    }

    private static CevioTrackPitchData AppendEndingPoints(CevioTrackPitchData data)
    {
        var result = new List<CevioTrackPitchData.Event>();
        long? nextPos = null;
        foreach (var ev in data.Events)
        {
            var pos = ev.Index ?? nextPos ?? throw new InvalidOperationException();
            var length = ev.Repeat ?? 1L;
            if (nextPos is not null && nextPos.Value < pos)
                result.Add(new CevioTrackPitchData.Event(nextPos, null, TempValueAsNull));
            result.Add(new CevioTrackPitchData.Event(pos, length, ev.Value));
            nextPos = pos + length;
        }
        if (nextPos is not null)
            result.Add(new CevioTrackPitchData.Event(nextPos, null, TempValueAsNull));
        return new CevioTrackPitchData(result, data.Tempos, data.TickPrefix);
    }

    private static List<EventDouble> NormalizeToTick(CevioTrackPitchData data)
    {
        var tempos = data.Tempos
            .Select(t => t with { TickPosition = t.TickPosition + data.TickPrefix })
            .ToList()
            .Expand();
        var events = data.Events.Select(EventDouble.From).ToList();
        var eventsNormalized = new List<EventDouble>();
        var currentTempoIndex = 0;
        double nextPos = 0;
        double nextTickPos = 0;

        foreach (var ev in events)
        {
            var pos = ev.Index ?? nextPos;
            double tickPos;
            if (ev.Index is null)
            {
                tickPos = nextTickPos;
            }
            else
            {
                while (currentTempoIndex + 1 < tempos.Count && tempos[currentTempoIndex + 1].Pos <= ev.Index.Value)
                    currentTempoIndex++;
                var ticksInTimeUnit = TimeUnitAsTicksPerBpm * tempos[currentTempoIndex].Bpm;
                tickPos = tempos[currentTempoIndex].TickPos + (ev.Index.Value - tempos[currentTempoIndex].Pos) * ticksInTimeUnit;
            }

            var repeat = ev.Repeat ?? 1.0;
            var remainingRepeat = repeat;
            var repeatInTicks = 0.0;
            while (currentTempoIndex + 1 < tempos.Count && tempos[currentTempoIndex + 1].Pos < pos + repeat)
            {
                repeatInTicks += tempos[currentTempoIndex + 1].TickPos - Math.Max(tempos[currentTempoIndex].TickPos, tickPos);
                remainingRepeat -= tempos[currentTempoIndex + 1].Pos - Math.Max(tempos[currentTempoIndex].Pos, pos);
                currentTempoIndex++;
            }
            repeatInTicks += remainingRepeat * TimeUnitAsTicksPerBpm * tempos[currentTempoIndex].Bpm;
            nextPos = pos + repeat;
            nextTickPos = tickPos + repeatInTicks;
            eventsNormalized.Add(new EventDouble(tickPos, repeatInTicks, ev.Value));
        }

        return eventsNormalized
            .Select(e => e with { Value = e.Value == TempValueAsNull ? null : e.Value })
            .ToList();
    }

    private static List<EventDouble> ShapeEvents(List<EventDouble> events)
    {
        var filtered = events.Where(e => e.Repeat!.Value > 0).ToList();
        var result = new List<EventDouble>();
        foreach (var ev in filtered)
        {
            if (result.Count == 0)
            {
                result.Add(ev);
            }
            else if (result[^1].Index == ev.Index)
            {
                result[^1] = ev;
            }
            else
            {
                result.Add(ev);
            }
        }
        return result;
    }

    private static List<(double Pos, double TickPos, double Bpm)> Expand(this List<Tempo> tempos)
    {
        var accumulator = new List<(double Pos, double TickPos, double Bpm)>();
        foreach (var tempo in tempos)
        {
            if (accumulator.Count == 0)
            {
                accumulator.Add((0.0, 0.0, tempo.Bpm));
            }
            else
            {
                var (lastPos, lastTickPos, lastBpm) = accumulator[^1];
                var ticksInTimeUnit = TimeUnitAsTicksPerBpm * lastBpm;
                var newPos = lastPos + (tempo.TickPosition - lastTickPos) / ticksInTimeUnit;
                accumulator.Add((newPos, (double)tempo.TickPosition, tempo.Bpm));
            }
        }
        return accumulator;
    }

    public static CevioTrackPitchData? GenerateForCevio(
        this PitchModel pitch, List<Note> notes, List<Tempo> tempos, long tickPrefix)
    {
        var endTick = notes.Count > 0 ? notes[^1].TickOff : 0;
        if (endTick == 0) return null;
        var absoluteData = pitch.GetAbsoluteData(notes);
        if (absoluteData is null || absoluteData.Count == 0) return null;

        long? nextIndex = null;
        var eventsWithFullParams = new List<EventDouble>();

        for (var i = 0; i < absoluteData.Count; i++)
        {
            var thisPoint = absoluteData[i];
            var hasNext = i + 1 < absoluteData.Count;
            var index = thisPoint.Tick;

            if (nextIndex is not null && nextIndex.Value > index)
            {
                if (eventsWithFullParams.Count > 0)
                {
                    var lastEvent = eventsWithFullParams[^1];
                    eventsWithFullParams.RemoveAt(eventsWithFullParams.Count - 1);
                    var lastEventRepeat = index - lastEvent.Index!.Value;
                    if (lastEventRepeat >= 1)
                        eventsWithFullParams.Add(lastEvent with { Repeat = (double)lastEventRepeat });
                }
            }

            var nextTick = hasNext ? absoluteData[i + 1].Tick : (long?)null;
            var repeat = nextTick.HasValue
                ? Math.Max(nextTick.Value - index, 1)
                : Math.Max(endTick - index, 1);
            nextIndex = index + repeat;
            if (thisPoint.Value is null) continue;
            var value = thisPoint.Value.Value.KeyToLoggedFrequency();
            eventsWithFullParams.Add(new EventDouble((double)index, (double)repeat, value));
        }

        var areEventsConnectedToNext = new List<bool>();
        for (var i = 0; i < eventsWithFullParams.Count; i++)
        {
            if (i + 1 >= eventsWithFullParams.Count)
            {
                areEventsConnectedToNext.Add(false);
            }
            else
            {
                var thisEvent = eventsWithFullParams[i];
                var nextEvent = eventsWithFullParams[i + 1];
                var rpt = thisEvent.Repeat ?? 1.0;
                areEventsConnectedToNext.Add(thisEvent.Index!.Value + rpt >= nextEvent.Index!.Value);
            }
        }

        var denormalized = DenormalizeFromTick(eventsWithFullParams, tempos, tickPrefix);
        var restored = RestoreConnection(denormalized, areEventsConnectedToNext);
        var merged = MergeEventsIfPossible(restored);
        var noRedundantIndex = RemoveRedundantIndex(merged);
        var events = RemoveRedundantRepeat(noRedundantIndex);

        if (events.Count == 0) return null;
        return new CevioTrackPitchData(events, [], tickPrefix);
    }

    public static long GetLength(this CevioTrackPitchData data)
    {
        var lastEventWithIndex = data.Events.Last(e => e.Index is not null);
        var length = lastEventWithIndex.Index!.Value;
        var startIndex = data.Events.IndexOf(lastEventWithIndex);
        for (var i = startIndex; i < data.Events.Count; i++)
            length += data.Events[i].Repeat ?? 1L;
        return length + MinDataLength;
    }

    private static List<CevioTrackPitchData.Event> DenormalizeFromTick(
        List<EventDouble> eventsWithFullParams, List<Tempo> temposInTicks, long tickPrefix)
    {
        var tempos = temposInTicks
            .Select(t => t.TickPosition != 0L ? t with { TickPosition = t.TickPosition + tickPrefix } : t)
            .ToList()
            .Expand();
        var events = eventsWithFullParams
            .Select(e => e with { Index = e.Index.HasValue ? e.Index.Value + tickPrefix : (double?)null })
            .ToList();

        var currentTempoIndex = 0;
        var result = new List<CevioTrackPitchData.Event>();
        foreach (var ev in events)
        {
            var tickPos = ev.Index!.Value;
            while (currentTempoIndex + 1 < tempos.Count && tempos[currentTempoIndex + 1].TickPos <= tickPos)
                currentTempoIndex++;
            var ticksInTimeUnit = TimeUnitAsTicksPerBpm * tempos[currentTempoIndex].Bpm;
            var pos = tempos[currentTempoIndex].Pos + (tickPos - tempos[currentTempoIndex].TickPos) / ticksInTimeUnit;
            var repeatInTicks = ev.Repeat!.Value;
            var remainingRepeatInTicks = repeatInTicks;
            var repeat = 0.0;
            while (currentTempoIndex + 1 < tempos.Count && tempos[currentTempoIndex + 1].TickPos < tickPos + repeatInTicks)
            {
                repeat += tempos[currentTempoIndex + 1].Pos - Math.Max(tempos[currentTempoIndex].Pos, pos);
                remainingRepeatInTicks -= tempos[currentTempoIndex + 1].TickPos -
                    Math.Max(tempos[currentTempoIndex].TickPos, tickPos);
                currentTempoIndex++;
            }
            repeat += remainingRepeatInTicks / (TimeUnitAsTicksPerBpm * tempos[currentTempoIndex].Bpm);
            result.Add(new EventDouble(pos, Math.Max(repeat, 1.0), ev.Value).Round());
        }
        return result;
    }

    private static List<CevioTrackPitchData.Event> RestoreConnection(
        List<CevioTrackPitchData.Event> events, List<bool> connected)
    {
        var result = new List<CevioTrackPitchData.Event>();
        for (var i = 0; i < events.Count; i++)
        {
            var thisEvent = events[i];
            if (i + 1 >= events.Count)
            {
                result.Add(thisEvent);
            }
            else if (connected[i])
            {
                var nextEvent = events[i + 1];
                result.Add(thisEvent with { Repeat = nextEvent.Index!.Value - thisEvent.Index!.Value });
            }
            else
            {
                result.Add(thisEvent);
            }
        }
        return result;
    }

    private static List<CevioTrackPitchData.Event> MergeEventsIfPossible(List<CevioTrackPitchData.Event> events)
    {
        var accEvents = new List<CevioTrackPitchData.Event>();
        foreach (var thisEvent in events)
        {
            if (accEvents.Count == 0)
            {
                accEvents.Add(thisEvent);
                continue;
            }
            var lastEvent = accEvents[^1];
            var areOverlapped = lastEvent.Index!.Value + lastEvent.Repeat!.Value > thisEvent.Index!.Value;
            if (areOverlapped)
            {
                var lastEventAsPoints = new List<(long First, double Second)>();
                for (long idx = lastEvent.Index.Value; idx < lastEvent.Index.Value + lastEvent.Repeat.Value; idx++)
                    lastEventAsPoints.Add((idx, lastEvent.Value));
                var thisEventAsPoints = new List<(long First, double Second)>();
                for (long idx = thisEvent.Index.Value; idx < thisEvent.Index.Value + thisEvent.Repeat!.Value; idx++)
                    thisEventAsPoints.Add((idx, thisEvent.Value));
                var mergedPoints = lastEventAsPoints.Concat(thisEventAsPoints)
                    .GroupBy(p => p.First)
                    .Select(g => (First: g.Key, Second: g.Average(p => p.Second)))
                    .OrderBy(p => p.First)
                    .ToList();
                var mergedEvents = new List<CevioTrackPitchData.Event>();
                foreach (var element in mergedPoints)
                {
                    var last = mergedEvents.Count > 0 ? mergedEvents[^1] : null;
                    if (last is null)
                    {
                        mergedEvents.Add(new CevioTrackPitchData.Event(element.First, 1, element.Second));
                    }
                    else if (lastEvent.Value == element.Second)
                    {
                        mergedEvents[^1] = last with { Repeat = (last.Repeat ?? 1) + 1 };
                    }
                    else
                    {
                        mergedEvents.Add(new CevioTrackPitchData.Event(element.First, 1, element.Second));
                    }
                }
                accEvents.RemoveAt(accEvents.Count - 1);
                accEvents.AddRange(mergedEvents);
            }
            else
            {
                var areAdjacent = lastEvent.Index.Value + lastEvent.Repeat.Value == thisEvent.Index.Value;
                var areValuesSame = lastEvent.Value == thisEvent.Value;
                if (areAdjacent && areValuesSame)
                {
                    accEvents[^1] = lastEvent with { Repeat = lastEvent.Repeat!.Value + thisEvent.Repeat!.Value };
                }
                else
                {
                    accEvents.Add(thisEvent);
                }
            }
        }
        return accEvents;
    }

    private static List<CevioTrackPitchData.Event> RemoveRedundantIndex(List<CevioTrackPitchData.Event> events)
    {
        if (events.Count == 0) return events;
        var result = new List<CevioTrackPitchData.Event> { events[0] };
        for (var i = 1; i < events.Count; i++)
        {
            var lastEvent = result[^1];
            var thisEvent = events[i];
            var areAdjacent = lastEvent.Index!.Value + lastEvent.Repeat!.Value == thisEvent.Index;
            result.Add(areAdjacent ? thisEvent with { Index = null } : thisEvent);
        }
        return result;
    }

    private static List<CevioTrackPitchData.Event> RemoveRedundantRepeat(List<CevioTrackPitchData.Event> events) =>
        events.Select(e => e.Repeat == 1L ? e with { Repeat = null } : e).ToList();
}
