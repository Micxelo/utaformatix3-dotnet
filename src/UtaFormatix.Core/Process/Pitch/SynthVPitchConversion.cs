using UtaFormatix.Core.Models;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.Process.Pitch;

internal sealed record SvpDefaultVibratoParameters(
    double? VibratoStart,
    double? EaseInLength,
    double? EaseOutLength,
    double? Depth,
    double? Frequency);

internal sealed record SvpNoteWithVibrato(
    long NoteStartTick,
    long NoteLengthTick,
    double? VibratoStart,
    double? EaseInLength,
    double? EaseOutLength,
    double? Depth,
    double? Frequency,
    double? Phase)
{
    public long NoteEndTick => NoteStartTick + NoteLengthTick;
}

internal static class SynthVPitchConversion
{
    private const long SamplingIntervalTick = 4L;
    private const double VibratoDefaultStartSec = 0.25;
    private const double VibratoDefaultEaseInSec = 0.2;
    private const double VibratoDefaultEaseOutSec = 0.2;
    private const double VibratoDefaultDepthSemitone = 1.0;
    private const double VibratoDefaultFrequencyHz = 5.5;
    private const double VibratoDefaultPhaseRad = 0.0;

    public static List<(long Tick, double Value)> ProcessSvpInputPitchData(
        List<(long Tick, double Value)> points,
        string? mode,
        List<SvpNoteWithVibrato> notesWithVibrato,
        List<Tempo> tempos,
        List<(long Tick, double Value)> vibratoEnvPoints,
        string? vibratoEnvMode,
        SvpDefaultVibratoParameters? vibratoDefaultParameters)
    {
        var merged = Merge(points);
        var interpolated = Interpolate(merged, mode);
        var vibratoEnv = ExtendEveryTick(Interpolate(Merge(vibratoEnvPoints), vibratoEnvMode));

        return RemoveRedundantPoints(
            AppendVibrato(interpolated, notesWithVibrato, vibratoDefaultParameters, tempos, vibratoEnv));
    }

    private static List<(long Tick, double Value)> Merge(List<(long Tick, double Value)> points)
    {
        if (points.Count == 0) return points;

        return points
            .GroupBy(p => p.Tick)
            .Select(g => (Tick: g.Key, Value: g.Average(p => p.Value)))
            .OrderBy(p => p.Tick)
            .ToList();
    }

    private static List<(long Tick, double Value)> Interpolate(
        List<(long Tick, double Value)> points, string? mode)
    {
        if (points.Count == 0) return points;

        return mode switch
        {
            "linear" => points.InterpolateLinear(SamplingIntervalTick),
            "cosine" => points.InterpolateCosineEaseInOut(SamplingIntervalTick),
            "cubic" => points.InterpolateCosineEaseInOut(SamplingIntervalTick),
            _ => points.InterpolateCosineEaseInOut(SamplingIntervalTick),
        };
    }

    private static Dictionary<long, double> ExtendEveryTick(List<(long Tick, double Value)> points)
    {
        var result = new Dictionary<long, double>();
        if (points.Count == 0) return result;

        foreach (var point in points)
        {
            if (result.Count == 0 || (result.Count > 0 && result.TryGetValue(result.Keys.Last(), out var lastVal) && lastVal == 1.0))
            {
                result[point.Tick] = point.Value;
            }
            else
            {
                var lastKey = result.Keys.Last();
                var lastValue = result[lastKey];
                if (lastValue != 1.0)
                {
                    for (var t = lastKey; t < point.Tick; t++)
                        result[t] = lastValue;
                }
                result[point.Tick] = point.Value;
            }
        }

        return result;
    }

    private static List<(long Tick, double Value)> AppendVibrato(
        List<(long Tick, double Value)> pitchPoints,
        List<SvpNoteWithVibrato> notes,
        SvpDefaultVibratoParameters? vibratoDefaultParameters,
        List<Tempo> tempos,
        Dictionary<long, double> vibratoEnv)
    {
        var transformer = new TickTimeTransformer(tempos);

        var rangesList = new List<(LongRange Range, SvpNoteWithVibrato? Note)>();
        var lastTick = 0L;

        foreach (var note in notes)
        {
            if (lastTick < note.NoteStartTick)
                rangesList.Add((new LongRange(lastTick, note.NoteStartTick), null));
            rangesList.Add((new LongRange(note.NoteStartTick, note.NoteEndTick), note));
            lastTick = note.NoteEndTick;
        }
        rangesList.Add((new LongRange(lastTick, long.MaxValue), null));

        var result = new List<(long Tick, double Value)>();
        var pitchIndex = 0;

        foreach (var (range, note) in rangesList)
        {
            while (pitchIndex < pitchPoints.Count && pitchPoints[pitchIndex].Tick < range.Start)
                pitchIndex++;

            var startIndex = pitchIndex;
            while (pitchIndex < pitchPoints.Count && pitchPoints[pitchIndex].Tick >= range.Start && pitchPoints[pitchIndex].Tick < range.End)
                pitchIndex++;

            if (startIndex < pitchIndex)
            {
                var subset = pitchPoints.GetRange(startIndex, pitchIndex - startIndex);
                result.AddRange(AppendVibratoInNote(subset, note, vibratoDefaultParameters, transformer, tempos, vibratoEnv));
            }
        }

        return result;
    }

    private static List<(long Tick, double Value)> AppendVibratoInNote(
        List<(long Tick, double Value)> points,
        SvpNoteWithVibrato? note,
        SvpDefaultVibratoParameters? defaultParameters,
        TickTimeTransformer tickTimeTransformer,
        List<Tempo> tempos,
        Dictionary<long, double> vibratoEnv)
    {
        if (note is null || note.NoteStartTick < 0L) return points;

        var noteStartSec = tickTimeTransformer.TickToSec(note.NoteStartTick);
        var noteEndSec = tickTimeTransformer.TickToSec(note.NoteEndTick);

        var vibratoStartSec = (note.VibratoStart ?? defaultParameters?.VibratoStart ?? VibratoDefaultStartSec) + noteStartSec;
        var vibratoStartTick = tickTimeTransformer.SecToTick(vibratoStartSec);
        var easeInLength = note.EaseInLength ?? defaultParameters?.EaseInLength ?? VibratoDefaultEaseInSec;
        var easeOutLength = note.EaseOutLength ?? defaultParameters?.EaseOutLength ?? VibratoDefaultEaseOutSec;
        var depth = (note.Depth ?? defaultParameters?.Depth ?? VibratoDefaultDepthSemitone) * 0.5;
        if (depth == 0.0) return points;
        var phase = note.Phase ?? VibratoDefaultPhaseRad;
        var frequency = note.Frequency ?? defaultParameters?.Frequency ?? VibratoDefaultFrequencyHz;

        var secPerTick = tempos.LastOrDefault(t => t.TickPosition <= note.NoteStartTick)?.Bpm.BpmToSecPerTick()
            ?? Constants.DefaultBpm.BpmToSecPerTick();

        double Vibrato(long tick)
        {
            var sec = tickTimeTransformer.TickToSec(tick);
            if (sec < vibratoStartSec) return 0.0;
            var easeInFactor = Math.Clamp((sec - vibratoStartSec) / easeInLength, 0.0, 1.0);
            var easeOutFactor = Math.Clamp((noteEndSec - sec) / easeOutLength, 0.0, 1.0);
            var rad = 2 * Math.PI * frequency * secPerTick * (tick - vibratoStartTick) + phase;
            var envelope = vibratoEnv.TryGetValue(tick, out var envVal) ? envVal : 1.0;
            return envelope * depth * easeInFactor * easeOutFactor * Math.Sin(rad);
        }

        var basePoints = points.Count > 0 ? points : [(note.NoteStartTick, 0.0), (note.NoteEndTick, 0.0)];

        var pointsWithEnd = basePoints[^1].Tick != note.NoteEndTick
            ? [.. basePoints, (note.NoteEndTick, basePoints[^1].Value)]
            : basePoints;

        var result = new List<(long Tick, double Value)>();
        (long Tick, double Value)? prev = null;

        foreach (var point in pointsWithEnd)
        {
            if (prev is null)
            {
                result.Add((point.Tick, point.Value + Vibrato(point.Tick)));
            }
            else
            {
                var tick = prev.Value.Tick + SamplingIntervalTick;
                while (tick < point.Tick)
                {
                    result.Add((tick, prev.Value.Value + Vibrato(tick)));
                    tick += SamplingIntervalTick;
                }
                result.Add((point.Tick, point.Value + Vibrato(point.Tick)));
            }
            prev = point;
        }

        return result;
    }

    private static List<(long Tick, double Value)> RemoveRedundantPoints(List<(long Tick, double Value)> points)
    {
        var result = new List<(long Tick, double Value)>();
        foreach (var point in points)
        {
            var previousValue = result.Count > 0 ? result[^1].Value : (double?)null;
            if (point.Value != previousValue)
                result.Add(point);
        }
        return result;
    }

    public static List<(long Tick, double Value)> AppendPitchPointsForSvpOutput(
        this List<(long Tick, double Value)> points) =>
        AppendPitchPointsForInterpolation(points, SamplingIntervalTick)
            .ReduceRepeatedPitchPoints();

    private static List<(long Tick, double Value)> AppendPitchPointsForInterpolation(
        List<(long Tick, double Value)> points, long intervalTick)
    {
        if (points.Count == 0) return points;

        var result = new List<(long Tick, double Value)> { points[0] };

        for (var i = 0; i < points.Count - 1; i++)
        {
            var lastPoint = points[i];
            var thisPoint = points[i + 1];
            var tickDiff = thisPoint.Tick - lastPoint.Tick;

            (long Tick, double Value)? newPoint = tickDiff switch
            {
                _ when tickDiff < intervalTick => null,
                _ when tickDiff < 2 * intervalTick => ((thisPoint.Tick + lastPoint.Tick) / 2, lastPoint.Value),
                _ => (thisPoint.Tick - intervalTick, lastPoint.Value),
            };

            if (newPoint is not null)
                result.Add(newPoint.Value);
            result.Add(thisPoint);
        }

        return result;
    }

    private static List<(long Tick, double Value)> ReduceRepeatedPitchPoints(
        this List<(long Tick, double Value)> points)
    {
        var toBeRemoved = new HashSet<(long Tick, double Value)>();
        double? currentRepeatedValue = null;
        (long Tick, double Value)? prevPoint = null;

        foreach (var point in points)
        {
            if (prevPoint is null)
            {
                prevPoint = point;
                continue;
            }

            if (currentRepeatedValue is null)
            {
                if (prevPoint.Value.Value == point.Value)
                    currentRepeatedValue = point.Value;
                prevPoint = point;
                continue;
            }

            if (currentRepeatedValue.Value == point.Value)
            {
                toBeRemoved.Add(prevPoint.Value);
            }
            else
            {
                currentRepeatedValue = null;
            }

            prevPoint = point;
        }

        return points.Where(p => !toBeRemoved.Contains(p)).ToList();
    }

    private sealed record LongRange(long Start, long End);
}
