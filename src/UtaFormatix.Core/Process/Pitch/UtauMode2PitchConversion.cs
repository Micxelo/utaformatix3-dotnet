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
        PitchModel? pitch, List<Note> notes, List<Tempo> tempos, UstPitchContext? context = null)
    {
        if (pitch is null) return null;
        var absolutePitch = context is null ? pitch.GetAbsoluteData(notes) : context.Absolute(pitch, notes);
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
            context?.Distance(minNegTick, 0, "PBS") ?? -minNegTick,
            BpmForNote(tempos, firstNote)));

        for (var i = 1; i < notes.Count; i++)
        {
            var note = notes[i];
            var filtered = absolutePitch.Where(p => p.Tick >= note.TickOn && p.Tick < note.TickOff).ToList();
            var firstAtNote = absolutePitch.FirstOrDefault(p => p.Tick >= note.TickOn);
            var offsetTick = firstAtNote.Tick >= note.TickOn ? firstAtNote.Tick : note.TickOn;
            var offset = context?.Distance(note.TickOn, offsetTick, "PBS") ?? offsetTick - note.TickOn;

            dotPitData.Add((
                ToRelative(filtered, note.Key),
                offset,
                BpmForNote(tempos, note)));
        }

        var simplified = dotPitData
            .Select((d, index) =>
            {
                context?.AtNote(index);
                return (context is null ? RdpSimplification.SimplifyShapeTo(d.Pitch, UstConstants.Mode2PitchMaxPointCount)
                    : context.Simplify(d.Pitch, UstConstants.Mode2PitchMaxPointCount), d.Offset, d.Bpm);
            })
            .ToList();

        var result = new List<UtauMode2NotePitchData?>();

        foreach (var (pit, offset, bpm) in simplified)
        {
            context?.AtNote(result.Count);
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
                var distance = context?.Distance(pit[j].Tick, pit[j + 1].Tick, "PBW") ?? pit[j + 1].Tick - pit[j].Tick;
                var width = MilliSecFromTick(distance, bpm);
                var shift = pit[j + 1].Value * 10;
                context?.Finite(width, "PBW");
                if (context is not null && width < 0) throw context.Range("PBW");
                context?.Finite(shift, "PBY");
                widths.Add(width);
                shifts.Add(shift);
                curveTypes.Add("");
            }

            context?.Finite(MilliSecFromTick(offset, bpm), "PBS");
            context?.Finite(pit[0].Value * 10, "PBS");
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
        UtauMode2TrackPitchData? pitchData, List<Note> notes, List<Tempo> tempos, UstPitchContext? context = null)
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
            context?.AtNote(i);
            var points = new List<(long Tick, double Value)>();
            var noteStartInMillis = tickTimeTransformer.TickToMilliSec(note.TickOn);
            context?.Finite(noteStartInMillis, "PBS");

            if (notePitch?.Start is not null)
            {
                context?.Source(notePitch.Widths.Count + 1L, "PBW");
                var posInMillis = noteStartInMillis + notePitch.Start.Value;
                var tickPos = Math.Max(
                    context?.Tick(tickTimeTransformer, posInMillis, "PBS") ?? tickTimeTransformer.SecToTick(posInMillis / 1000.0),
                    context?.Add(lastKeyPos, UstConstants.SafeSamplingIntervalTick, "PBS") ?? lastKeyPos + UstConstants.SafeSamplingIntervalTick);
                lastKeyPos = tickPos;

                double startShift;
                if (note.TickOn == lastNote?.TickOff)
                    startShift = (double)lastNote.Key - note.Key;
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
                        context?.Tick(tickTimeTransformer, posInMillis, $"PBW[{index}]") ?? tickTimeTransformer.SecToTick(posInMillis / 1000.0),
                        context?.Add(lastKeyPos, UstConstants.SafeSamplingIntervalTick, $"PBW[{index}]") ?? lastKeyPos + UstConstants.SafeSamplingIntervalTick);
                    lastKeyPos = tickPos;

                    var thisPoint = (tickPos, shift / 10.0);
                    var lastPoint = points[^1];

                    if (Math.Abs(thisPoint.Item2 - lastPoint.Item2) > double.Epsilon)
                    {
                        context?.Curve(lastPoint.Item1, thisPoint.Item1, lastPoint.Item2, thisPoint.Item2, curveType);
                        if (context?.ValidateOnly == true) points.Add(thisPoint);
                        else
                        {
                            var interpolated = Interpolate(lastPoint, thisPoint, curveType);
                            context?.Values(interpolated, "PBY");
                            points.AddRange(interpolated.Skip(1));
                        }
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
                .AppendUtauNoteVibrato(notePitch?.VibratoParams, note, tickTimeTransformer, UstConstants.SamplingIntervalTick, context)
                .Shape();
            context?.Values(pendingPitchPoints, "PBY");
            context?.Samples(pendingPitchPoints.Count, "pitch");
            if (context?.ValidateOnly == true) pendingPitchPoints.Clear();

            lastNote = note;
        }

        pitchPoints.AddRange(pendingPitchPoints);
        return context?.ValidateOnly == true ? null : new PitchModel(pitchPoints.Select(p => (p.Tick, (double?)p.Value)).ToList(), false);
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

    internal static List<(long Tick, double Value)> AppendUtauNoteVibrato(
        this List<(long Tick, double Value)> points,
        UtauNoteVibratoParams? vibratoParams,
        Note thisNote,
        TickTimeTransformer tickTimeTransformer,
        long sampleIntervalTick, IVibratoPitchContext? context = null)
    {
        if (vibratoParams is null) return points;

        var noteLength = tickTimeTransformer.TickDistanceToMilliSec(thisNote.TickOn, thisNote.TickOff);
        var vibratoLength = noteLength * vibratoParams.Length / 100.0;
        context?.Finite(noteLength, "VBR");
        context?.Finite(vibratoLength, "VBR[0]");
        if (context is not null && noteLength <= 0 && vibratoParams.Length > 0 && vibratoParams.Depth > 0)
            throw context.Range("VBR");
        if (vibratoLength <= 0) return points;

        var frequency = 1.0 / vibratoParams.Period;
        context?.Finite(frequency, "VBR[1]");
        if (!double.IsFinite(frequency)) return points;

        var depth = vibratoParams.Depth / 100.0;
        if (depth <= 0) return points;

        var easeInLength = noteLength * vibratoParams.FadeIn / 100.0;
        var easeOutLength = noteLength * vibratoParams.FadeOut / 100.0;
        var phase = vibratoParams.PhaseShift / 100.0;
        var shift = vibratoParams.Shift / 100.0;
        context?.Finite(easeInLength, "VBR[3]");
        context?.Finite(easeOutLength, "VBR[4]");
        context?.Finite(depth * (1 + Math.Abs(shift)), "VBR[6]");
        context?.Finite(2 * Math.PI * (Math.Abs(frequency) * (Math.Abs(noteLength) + Math.Abs(vibratoLength)) + Math.Abs(phase)), "VBR[1]");

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
            thisNote.TickOn, context?.Add(thisNote.TickOn, sampleIntervalTick, "VBR") ?? thisNote.TickOn + sampleIntervalTick);
        if (context is not null && (!double.IsFinite(sampleIntervalInMillis) || sampleIntervalInMillis <= 0))
            throw context.Range("VBR");

        var millisPoints = points
            .Select(p => (MilliSec: tickTimeTransformer.TickToMilliSec(p.Tick) - noteStartInMillis, Value: p.Value))
            .ToList();
        if (context is not null)
            foreach (var p in millisPoints) context.Finite(p.MilliSec, "VBR");
        if (context?.ValidateOnly == true) return points;

        var result = new List<(double MilliSec, double Value)>();

        for (var i = 0; i < millisPoints.Count; i++)
        {
            var inputPoint = millisPoints[i];
            var newPoint = (MilliSec: inputPoint.MilliSec, Value: inputPoint.Value + Vibrato(inputPoint.MilliSec));

            if (i > 0)
            {
                // Use the previous base pitch. The emitted point already includes vibrato.
                var previousBase = millisPoints[i - 1];
                var pos = previousBase.MilliSec + sampleIntervalInMillis;
                if (context is not null && pos <= previousBase.MilliSec && pos < newPoint.MilliSec)
                    throw context.Range("VBR");
                // Reject enormous ranges before starting a sampling loop. The
                // per-point check below also handles floating-point endpoints.
                var estimated = (newPoint.MilliSec - pos) / sampleIntervalInMillis;
                context?.Preview(Math.Max(0, Math.Floor(estimated) - 2), "VBR");
                while (pos < newPoint.MilliSec)
                {
                    context?.Samples(1, "VBR");
                    var value = previousBase.Value + Vibrato(pos);
                    context?.Finite(value, "VBR");
                    result.Add((pos, value));
                    var next = pos + sampleIntervalInMillis;
                    if (context is not null && next <= pos) throw context.Range("VBR");
                    pos = next;
                }
            }

            context?.Finite(newPoint.Value, "VBR");
            context?.Samples(1, "VBR");
            result.Add(newPoint);
        }

        return result
            .Select(p => (context?.Tick(tickTimeTransformer, p.MilliSec + noteStartInMillis, "VBR")
                ?? tickTimeTransformer.SecToTick((p.MilliSec + noteStartInMillis) / 1000.0), p.Value))
            .ToList();
    }
}
