using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.Process.Pitch;

internal sealed record UtauMode2TrackPitchData(List<UtauMode2NotePitchData?> Notes);

internal sealed record UtauMode2NotePitchData(
    double? Bpm,
    double? Start,
    double? StartShift,
    List<double> Widths,
    List<double> Shifts,
    List<string> CurveTypes,
    UtauNoteVibratoParams? VibratoParams);

internal sealed record UtauNoteVibratoParams(
    double Length,
    double Period,
    double Depth,
    double FadeIn,
    double FadeOut,
    double PhaseShift,
    double Shift);

internal static class UtauMode2PitchConversion
{
    private static double MilliSecFromTick(long tick, double bpm) =>
        tick * 60000.0 / (bpm * Constants.TicksInBeat);

    private static double BpmForNote(List<Tempo> tempos, Note note) =>
        tempos.Last(t => t.TickPosition <= note.TickOn).Bpm;

    public static UtauMode2TrackPitchData? PitchToUtauMode2Track(
        PitchModel? pitch, List<Note> notes, List<Tempo> tempos)
    {
        if (pitch is null) return null;
        var absolutePitch = pitch.GetAbsoluteData(notes);
        if (absolutePitch is null) return null;
        if (notes.Count == 0) return null;

        List<(long Tick, double Value)> ToRelative(
            List<(long Tick, double? Value)> from, int key) =>
            from.Select(p => (p.Tick, (p.Value ?? key) - (double)key)).ToList();

        var firstNote = notes[0];
        var minNegTick = absolutePitch
            .Where(p => p.Tick < 0)
            .Select(p => p.Tick)
            .DefaultIfEmpty(0L)
            .Min();

        var dotPitData = new List<(List<(long Tick, double Value)> Pitch, long Offset, double Bpm)>();

        dotPitData.Add((
            ToRelative(absolutePitch.Where(p => p.Tick < firstNote.TickOff).ToList(), firstNote.Key),
            -minNegTick,
            BpmForNote(tempos, firstNote)));

        for (var i = 1; i < notes.Count; i++)
        {
            var note = notes[i];
            var filtered = absolutePitch.Where(p => p.Tick >= note.TickOn && p.Tick < note.TickOff).ToList();
            var firstAtNote = absolutePitch.FirstOrDefault(p => p.Tick >= note.TickOn);
            var offset = (firstAtNote.Tick >= note.TickOn ? firstAtNote.Tick : note.TickOn) - note.TickOn;

            dotPitData.Add((
                ToRelative(filtered, note.Key),
                offset,
                BpmForNote(tempos, note)));
        }

        var simplified = dotPitData
            .Select(d => (RdpSimplification.SimplifyShapeTo(d.Pitch, UstConstants.Mode2PitchMaxPointCount), d.Offset, d.Bpm))
            .ToList();

        var result = new List<UtauMode2NotePitchData?>();

        foreach (var (pit, offset, bpm) in simplified)
        {
            if (pit.Count == 0)
            {
                result.Add(null);
                continue;
            }

            var widths = new List<double>();
            var shifts = new List<double>();
            var curveTypes = new List<string>();

            for (var j = 0; j < pit.Count - 1; j++)
            {
                widths.Add(MilliSecFromTick(pit[j + 1].Tick - pit[j].Tick, bpm));
                shifts.Add(pit[j + 1].Value * 10);
                curveTypes.Add("");
            }

            result.Add(new UtauMode2NotePitchData(
                bpm,
                MilliSecFromTick(offset, bpm),
                pit[0].Value * 10,
                widths,
                shifts,
                curveTypes,
                null));
        }

        return new UtauMode2TrackPitchData(result);
    }

    public static PitchModel? PitchFromUtauMode2Track(
        UtauMode2TrackPitchData? pitchData, List<Note> notes, List<Tempo> tempos)
    {
        if (pitchData is null) return null;

        var tickTimeTransformer = new TickTimeTransformer(tempos);
        var pitchPoints = new List<(long Tick, double Value)>();
        Note? lastNote = null;
        var pendingPitchPoints = new List<(long Tick, double Value)>();
        var lastKeyPos = -UstConstants.SafeSamplingIntervalTick;

        var count = Math.Min(notes.Count, pitchData.Notes.Count);

        for (var i = 0; i < count; i++)
        {
            var note = notes[i];
            var notePitch = pitchData.Notes[i];
            var points = new List<(long Tick, double Value)>();
            var noteStartInMillis = tickTimeTransformer.TickToMilliSec(note.TickOn);

            if (notePitch?.Start is not null)
            {
                var posInMillis = noteStartInMillis + notePitch.Start.Value;
                var tickPos = Math.Max(
                    tickTimeTransformer.SecToTick(posInMillis / 1000.0),
                    lastKeyPos + UstConstants.SafeSamplingIntervalTick);
                lastKeyPos = tickPos;

                double startShift;
                if (note.TickOn == lastNote?.TickOff)
                    startShift = (double)(lastNote.Key - note.Key);
                else
                    startShift = (notePitch.StartShift ?? 0.0) / 10.0;

                points.Add((tickPos, startShift));

                for (var index = 0; index < notePitch.Widths.Count; index++)
                {
                    var width = notePitch.Widths[index];
                    var shift = index < notePitch.Shifts.Count ? notePitch.Shifts[index] : 0.0;
                    var curveType = index < notePitch.CurveTypes.Count ? notePitch.CurveTypes[index] : "";

                    posInMillis += width;
                    tickPos = Math.Max(
                        tickTimeTransformer.SecToTick(posInMillis / 1000.0),
                        lastKeyPos + UstConstants.SafeSamplingIntervalTick);
                    lastKeyPos = tickPos;

                    var thisPoint = (tickPos, shift / 10.0);
                    var lastPoint = points[^1];

                    if (Math.Abs(thisPoint.Item2 - lastPoint.Item2) > double.Epsilon)
                    {
                        var interpolated = Interpolate(lastPoint, thisPoint, curveType);
                        points.AddRange(interpolated.Skip(1));
                    }
                    else
                    {
                        points.Add(thisPoint);
                    }
                }
            }

            var firstPendingTick = points.Count > 0 ? points[0].Tick : long.MaxValue;
            pitchPoints.AddRange(pendingPitchPoints.Where(p => p.Tick < firstPendingTick));

            pendingPitchPoints = points
                .FixPointsAtLastNote(note, lastNote)
                .AppendStartPoint(note)
                .AppendEndPoint(note)
                .AppendUtauNoteVibrato(notePitch?.VibratoParams, note, tickTimeTransformer, UstConstants.SamplingIntervalTick)
                .Shape();

            lastNote = note;
        }

        pitchPoints.AddRange(pendingPitchPoints);
        return new PitchModel(pitchPoints.Select(p => (p.Tick, (double?)p.Value)).ToList(), false);
    }

    private static List<(long Tick, double Value)> FixPointsAtLastNote(
        this List<(long Tick, double Value)> points, Note thisNote, Note? lastNote)
    {
        if (lastNote is null || lastNote.TickOff != thisNote.TickOn)
            return points;

        var fixedPoints = points.Select(p =>
            p.Tick < thisNote.TickOn
                ? (p.Tick, p.Value + thisNote.Key - lastNote.Key)
                : p
        ).ToList();

        (long Tick, double Value)? lastPoint = fixedPoints.Count > 0 ? fixedPoints[^1] : null;
        if (lastPoint is not null && lastPoint.Value.Tick < thisNote.TickOn)
            fixedPoints.Add((thisNote.TickOn, 0.0));

        return fixedPoints;
    }

    private static List<(long Tick, double Value)> AppendStartPoint(
        this List<(long Tick, double Value)> points, Note thisNote)
    {
        if (points.Count == 0)
            return [(thisNote.TickOn, 0.0)];

        var first = points[0];
        if (first.Tick > thisNote.TickOn)
        {
            var result = new List<(long Tick, double Value)>(points.Count + 1);
            result.Add((thisNote.TickOn, first.Value));
            result.AddRange(points);
            return result;
        }

        return points;
    }

    private static List<(long Tick, double Value)> AppendEndPoint(
        this List<(long Tick, double Value)> points, Note thisNote)
    {
        if (points.Count == 0)
            return [(thisNote.TickOff, 0.0)];

        var last = points[^1];
        if (last.Tick < thisNote.TickOff)
        {
            var result = new List<(long Tick, double Value)>(points.Count + 1);
            result.AddRange(points);
            result.Add((thisNote.TickOff, last.Value));
            return result;
        }

        return points;
    }

    private static List<(long Tick, double Value)> Shape(
        this List<(long Tick, double Value)> points)
    {
        var sorted = points.OrderBy(p => p.Tick).ToList();
        var result = new List<(long Tick, double Value)>();

        foreach (var point in sorted)
        {
            if (result.Count > 0 && result[^1].Tick == point.Tick)
            {
                var last = result[^1];
                result[^1] = (last.Tick, (last.Value + point.Value) / 2.0);
            }
            else
            {
                result.Add(point);
            }
        }

        return result;
    }

    private static List<(long Tick, double Value)> Interpolate(
        (long Tick, double Value) lastPoint,
        (long Tick, double Value) thisPoint,
        string curveType)
    {
        var input = new List<(long Tick, double Value)> { lastPoint, thisPoint };
        return curveType switch
        {
            "s" => input.InterpolateLinear(UstConstants.SamplingIntervalTick),
            "j" => input.InterpolateCosineEaseIn(UstConstants.SamplingIntervalTick),
            "r" => input.InterpolateCosineEaseOut(UstConstants.SamplingIntervalTick),
            _ => input.InterpolateCosineEaseInOut(UstConstants.SamplingIntervalTick)
        };
    }

    private static List<(long Tick, double Value)> AppendUtauNoteVibrato(
        this List<(long Tick, double Value)> points,
        UtauNoteVibratoParams? vibratoParams,
        Note thisNote,
        TickTimeTransformer tickTimeTransformer,
        long sampleIntervalTick)
    {
        if (vibratoParams is null) return points;

        var noteLength = tickTimeTransformer.TickDistanceToMilliSec(thisNote.TickOn, thisNote.TickOff);
        var vibratoLength = noteLength * vibratoParams.Length / 100.0;
        if (vibratoLength <= 0) return points;

        var frequency = 1.0 / vibratoParams.Period;
        if (!double.IsFinite(frequency)) return points;

        var depth = vibratoParams.Depth / 100.0;
        if (depth <= 0) return points;

        var easeInLength = noteLength * vibratoParams.FadeIn / 100.0;
        var easeOutLength = noteLength * vibratoParams.FadeOut / 100.0;
        var phase = vibratoParams.PhaseShift / 100.0;
        var shift = vibratoParams.Shift / 100.0;

        var start = noteLength - vibratoLength;

        double Vibrato(double t)
        {
            if (t < start) return 0.0;

            var easeInFactor = easeInLength > 0
                ? Math.Clamp((t - start) / easeInLength, 0.0, 1.0)
                : 1.0;
            var easeOutFactor = easeOutLength > 0
                ? Math.Clamp((noteLength - t) / easeOutLength, 0.0, 1.0)
                : 1.0;

            var x = 2 * Math.PI * (frequency * (t - start) - phase);
            return depth * easeInFactor * easeOutFactor * (Math.Sin(x) + shift);
        }

        var noteStartInMillis = tickTimeTransformer.TickToMilliSec(thisNote.TickOn);

        var sampleIntervalInMillis = tickTimeTransformer.TickDistanceToMilliSec(
            thisNote.TickOn, thisNote.TickOn + sampleIntervalTick);

        var millisPoints = points
            .Select(p => (MilliSec: tickTimeTransformer.TickToMilliSec(p.Tick) - noteStartInMillis, Value: p.Value))
            .ToList();

        var result = new List<(double MilliSec, double Value)>();

        foreach (var inputPoint in millisPoints)
        {
            var newPoint = (MilliSec: inputPoint.MilliSec, Value: inputPoint.Value + Vibrato(inputPoint.MilliSec));

            if (result.Count > 0)
            {
                var lastPoint = result[^1];
                var pos = lastPoint.MilliSec + sampleIntervalInMillis;
                while (pos < newPoint.MilliSec)
                {
                    result.Add((pos, lastPoint.Value + Vibrato(pos)));
                    pos += sampleIntervalInMillis;
                }
            }

            result.Add(newPoint);
        }

        return result
            .Select(p => (tickTimeTransformer.SecToTick((p.MilliSec + noteStartInMillis) / 1000.0), p.Value))
            .ToList();
    }
}
