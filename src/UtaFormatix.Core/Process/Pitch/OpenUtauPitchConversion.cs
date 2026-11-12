using UtaFormatix.Core.Models;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.Process.Pitch;

internal sealed record OpenUtauNotePitchData(
    List<OpenUtauNotePitchData.Point> Points,
    UtauNoteVibratoParams Vibrato,
    int Tuning)
{
    public sealed record Point(double X, double Y, Shape Shape);

    public enum Shape
    {
        EaseIn,
        EaseOut,
        EaseInOut,
        Linear,
        Spline,
    }

    public static Shape ParseShape(string? textValue) => textValue switch
    {
        "i" => Shape.EaseIn,
        "o" => Shape.EaseOut,
        "io" => Shape.EaseInOut,
        "l" => Shape.Linear,
        "sp" => Shape.Spline,
        _ => Shape.EaseInOut,
    };

    public static string ShapeToText(Shape shape) => shape switch
    {
        Shape.EaseIn => "i",
        Shape.EaseOut => "o",
        Shape.EaseInOut => "io",
        Shape.Linear => "l",
        Shape.Spline => "sp",
        _ => "io",
    };
}

internal sealed record OpenUtauPartPitchData(
    List<OpenUtauPartPitchData.Point> Points,
    List<OpenUtauNotePitchData> Notes)
{
    public sealed record Point(long X, int Y);
}

internal static class OpenUtauPitchConversion
{
    private const long SamplingIntervalTick = 5L;
    private const long SafeSamplingIntervalTick = 5L;

    public static PitchModel? PitchFromUstxPart(
        List<Note> notes,
        OpenUtauPartPitchData pitchData,
        TickTimeTransformer time,
        UstxPitchContext partContext,
        int curveIndex)
    {
        var notePointsList = new List<List<(long Tick, double Value)>>();
        var lastKeyPos = -SafeSamplingIntervalTick;

        for (var i = 0; i < Math.Min(notes.Count, pitchData.Notes.Count); i++)
        {
            var note = notes[i];
            var notePitch = pitchData.Notes[i];
            var noteContext = partContext.At($".notes[{i}]");
            var pitchContext = noteContext.At(".pitch");
            var controls = new List<(long Tick, double Value)>(notePitch.Points.Count);
            var noteStartInMillis = 0.0;
            if (notePitch.Points.Count > 0)
            {
                noteStartInMillis = pitchContext.Finite(time.TickToMilliSec(note.TickOn), ".data");
                var nearbyTick = note.TickOn <= long.MaxValue - SamplingIntervalTick
                    ? note.TickOn + SamplingIntervalTick
                    : note.TickOn - SamplingIntervalTick;
                var nearbyTime = pitchContext.Finite(time.TickToMilliSec(nearbyTick), ".data");
                if (nearbyTime == noteStartInMillis)
                    throw pitchContext.Range(".data");
            }
            var keyPointPositions = new HashSet<long>();

            for (var j = 0; j < notePitch.Points.Count; j++)
            {
                var rawPoint = notePitch.Points[j];
                var field = $".data[{j}].x";
                var x = pitchContext.Tick(time, noteStartInMillis + rawPoint.X, field);
                x = Math.Max(x, pitchContext.Add(lastKeyPos, SafeSamplingIntervalTick, field));
                lastKeyPos = x;
                keyPointPositions.Add(x);
                controls.Add((x, rawPoint.Y / 10.0));
            }

            var points = new List<(long Tick, double Value)>();
            var hasSpline = notePitch.Points.Any(point => point.Shape == OpenUtauNotePitchData.Shape.Spline);
            if (controls.Count > 0)
                points.Add(controls[0]);
            for (var j = 1; j < controls.Count; j++)
            {
                var start = controls[j - 1];
                var end = controls[j];
                var shape = notePitch.Points[j - 1].Shape;
                if (shape == OpenUtauNotePitchData.Shape.Spline && controls.Count > 2)
                {
                    // OpenUtau uses held boundary values as neighbors when it extends a curve.
                    var before = j > 1 ? controls[j - 2] : (Math.Min(note.TickOn, start.Tick), start.Value);
                    var after = j + 1 < controls.Count ? controls[j + 1] : (Math.Max(note.TickOff, end.Tick), end.Value);
                    AppendSpline(points, before, start, end, after, pitchContext.At($".data[{j - 1}]"));
                }
                else if (start.Value != end.Value)
                {
                    // Two-control-point splines use the regular ease-in-out curve.
                    AppendInterpolated(points, start, end, shape, pitchContext);
                }
                else
                {
                    points.Add(end);
                }
            }

            var pointCount = points.Count;
            AppendStartAndEndPoint(points, note, holdLastValue: hasSpline);
            pitchContext.Samples(points.Count - pointCount, ".data");

            var pointsBefore = points.Where(p => p.Tick < note.TickOn).ToList();
            var preOnsetTuning = i > 0 && notes[i - 1].TickOff == note.TickOn
                ? ((double)notePitch.Tuning - pitchData.Notes[i - 1].Tuning) / 100.0
                : 0.0;
            // Before an adjacent note starts, its bends are relative to the previous tuned key.
            if (preOnsetTuning != 0)
            {
                for (var j = 0; j < pointsBefore.Count; j++)
                {
                    var (tick, value) = pointsBefore[j];
                    pointsBefore[j] = (tick, value + preOnsetTuning);
                }
            }
            var pointsIn = points.Where(p => p.Tick >= note.TickOn && p.Tick <= note.TickOff).ToList();
            var pointsAfter = points.Where(p => p.Tick > note.TickOff).ToList();

            var vibrato = notePitch.Vibrato;
            var pointsInNoteWithVibrato = vibrato.Length > 0 && vibrato.Depth > 0
                ? pointsIn.AppendUtauNoteVibrato(vibrato, note, time, SamplingIntervalTick, noteContext)
                : pointsIn;

            var pointsWithVibrato = pointsBefore.Concat(pointsInNoteWithVibrato).Concat(pointsAfter).ToList();
            var pointsResampled = Resampled(pointsWithVibrato, keyPointPositions, pitchContext);
            notePointsList.Add(pointsResampled);
        }

        var notePitchSections = new List<List<(Note Note, List<(long Tick, double Value)> Points)>>();
        var currentSection = new List<(Note Note, List<(long Tick, double Value)> Points)>();
        notePitchSections.Add(currentSection);

        for (var i = 0; i < Math.Min(notes.Count, notePointsList.Count); i++)
        {
            var noteWithPoints = (notes[i], notePointsList[i]);
            if (currentSection.Count == 0)
            {
                currentSection.Add(noteWithPoints);
                continue;
            }

            var lastNote = currentSection[^1].Note;
            if (lastNote.TickOff < noteWithPoints.Item1.TickOn)
            {
                currentSection = new List<(Note Note, List<(long Tick, double Value)> Points)> { noteWithPoints };
                notePitchSections.Add(currentSection);
            }
            else
            {
                currentSection.Add(noteWithPoints);
            }
        }

        var allPointsFromNote = new List<(long Tick, double Value)>();
        var sectionBorder = 0L;

        foreach (var section in notePitchSections)
        {
            if (section.Count == 0) continue;

            Note? lastNote = null;
            var pointsByNote = new List<List<(long Tick, double Value)>>();

            foreach (var (note, rawPoints) in section)
            {
                var prevNote = lastNote;
                var adjustedPoints = rawPoints.Select(p =>
                {
                    var baseY = prevNote is not null && p.Tick < note.TickOn
                        ? (double)prevNote.Key - note.Key
                        : 0;
                    return (p.Tick, p.Value - baseY);
                }).ToList();

                pointsByNote.Add(adjustedPoints);
                lastNote = note;
            }

            var nextSectionBorder = section[^1].Note.TickOff;

            var pointsInSection = pointsByNote
                .SelectMany(list => list)
                .GroupBy(p => p.Tick)
                .Where(g => g.Key >= sectionBorder && g.Key <= nextSectionBorder)
                .Select(g => (g.Key, Value: partContext.Finite(g.Sum(p => p.Value), ".notes")))
                .ToList();

            allPointsFromNote.AddRange(pointsInSection);
            sectionBorder = nextSectionBorder;
        }

        var notePoints = allPointsFromNote
            .GroupBy(p => p.Tick)
            .Select(g => (Tick: g.Key, Value: partContext.Finite(g.Sum(p => p.Value), "")))
            .OrderBy(p => p.Tick)
            .ToList();
        var pitchPoints = pitchData.Points.Count == 0 ? notePoints
            : AddPitd(notePoints, ResamplePitd(pitchData.Points, partContext.At($".curves[{curveIndex}]")), partContext);
        var nonnegativePoints = pitchPoints.Where(p => p.Tick >= 0)
            .Select(p => (p.Tick, (double?)p.Value)).ToList();

        return nonnegativePoints.Count == 0 ? null : new PitchModel(nonnegativePoints, IsAbsolute: false);
    }

    private static List<(long Tick, double Value)> ResamplePitd(
        List<OpenUtauPartPitchData.Point> controls, UstxPitchContext context)
    {
        context.Samples(1, "");
        var start = controls[0];
        var result = new List<(long Tick, double Value)> { (start.X, start.Y / 100.0) };
        for (var i = 1; i < controls.Count; i++)
        {
            var end = controls[i];
            if (end.X == start.X) continue; // The input validator has rejected conflicting values.

            var span = context.Distance(start.X, end.X, "");
            var remainder = start.X % SamplingIntervalTick;
            var step = remainder < 0 ? -remainder : SamplingIntervalTick - remainder;
            var intermediateCount = span > step ? (span - step - 1) / SamplingIntervalTick + 1 : 0;
            context.Samples(intermediateCount + 1, "");

            var from = start.Y / 100.0;
            var to = end.Y / 100.0;
            // Keep both original endpoints and sample the interior on the regular grid.
            for (var j = 0L; j < intermediateCount; j++)
            {
                var offset = step + j * SamplingIntervalTick;
                result.Add((start.X + offset, from + offset * (to - from) / span));
            }
            result.Add((end.X, to));
            start = end;
        }
        return result;
    }

    private static List<(long Tick, double Value)> AddPitd(
        List<(long Tick, double Value)> notePoints,
        List<(long Tick, double Value)> deviations,
        UstxPitchContext context)
    {
        if (notePoints.Count == 0) return deviations;
        var result = new List<(long Tick, double Value)>(Math.Max(notePoints.Count, deviations.Count));
        var index = 0;
        foreach (var (tick, deviation) in deviations)
        {
            while (index < notePoints.Count && notePoints[index].Tick < tick)
                result.Add(notePoints[index++]);

            var bend = 0.0;
            if (index < notePoints.Count && notePoints[index].Tick == tick)
            {
                bend = notePoints[index++].Value;
            }
            else if (index > 0 && index < notePoints.Count)
            {
                var before = notePoints[index - 1];
                var after = notePoints[index];
                var span = after.Tick - before.Tick;
                // Interpolate the bend at an off-grid PITD point, without bridging rests.
                if (span <= SamplingIntervalTick)
                {
                    var phase = (double)(tick - before.Tick) / span;
                    bend = (1 - phase) * before.Value + phase * after.Value;
                }
            }
            result.Add((tick, context.Finite(bend + deviation, "")));
        }
        result.AddRange(notePoints.Skip(index));
        return result;
    }

    public static PitchModel? MergePitchFromUstxParts(List<PitchModel> parts, UstxPitchContext context)
    {
        if (parts.Count == 0) return null;
        if (parts.Count == 1) return parts[0];

        var data = parts
            .SelectMany(pitch => pitch.Data)
            .Where(p => p.Value is not null)
            .Select(p => (p.Tick, p.Value!.Value))
            .GroupBy(p => p.Tick)
            .Select(g => (Tick: g.Key, Value: context.Finite(g.Sum(p => p.Value), "")))
            .OrderBy(p => p.Tick)
            .ToList();

        return new PitchModel(
            data.Select(p => (p.Tick, (double?)p.Value)).ToList(),
            IsAbsolute: false);
    }

    public static PitchModel ApplyNoteTuning(PitchModel pitch, List<Note> notes,
        Dictionary<long, int> tuningByOnset, UstxPitchContext context)
    {
        var data = pitch.Data;
        var result = new List<(long Tick, double? Value)>(data.Count);
        var index = 0;
        var tuning = tuningByOnset.GetValueOrDefault(notes[0].TickOn);
        for (var i = 1; i < notes.Count; i++)
        {
            var note = notes[i];
            var nextTuning = tuningByOnset.GetValueOrDefault(note.TickOn);
            if (nextTuning == tuning) continue;

            // Match the base-note switch used by relative pitch, including across rests.
            var previousEnd = notes[i - 1].TickOff;
            var border = previousEnd + (note.TickOn - previousEnd) / 2;
            while (index < data.Count && data[index].Tick < border - 1)
                AppendOriginal();

            // Anchor both sides so interpolation preserves the step between notes.
            AppendBoundary(border - 1);
            tuning = nextTuning;
            AppendBoundary(border);
        }

        while (index < data.Count)
            AppendOriginal();
        return new PitchModel(result, IsAbsolute: false);

        void AppendOriginal()
        {
            var (tick, value) = data[index++];
            result.Add((tick, value + tuning / 100.0));
        }

        void AppendBoundary(long tick)
        {
            if (result.Count > 0 && result[^1].Tick == tick) return;
            if (index < data.Count && data[index].Tick == tick)
            {
                AppendOriginal();
                return;
            }

            context.Samples(1, "");
            var before = data[Math.Max(0, index - 1)];
            var after = data[Math.Min(index, data.Count - 1)];
            var ratio = before.Tick == after.Tick ? 0.0 : (double)(tick - before.Tick) / (after.Tick - before.Tick);
            var value = before.Value.GetValueOrDefault() * (1 - ratio) + after.Value.GetValueOrDefault() * ratio;
            result.Add((tick, context.Finite(value + tuning / 100.0, "")));
        }
    }

    public static PitchModel? ReduceRepeatedPitchPointsFromUstxTrack(this PitchModel? pitch)
    {
        if (pitch is null) return null;
        var reduced = pitch.Data
            .Select(p => (p.Tick, p.Value ?? 0.0))
            .ToList()
            .ReduceRepeatedPitchPoints();
        return new PitchModel(
            reduced.Select(p => (p.Tick, (double?)p.Value)).ToList(),
            pitch.IsAbsolute);
    }

    public static List<(long Tick, double Value)> ToOpenUtauPitchData(
        this PitchModel? pitch, List<Note> notes, UstxPitchContext context)
    {
        if (pitch is null) return [];
        context.Samples(pitch.Data.Count, "");

        // Distinct input values are ambiguous even if they round to the same cent.
        var valuesByTick = new Dictionary<long, double>();
        var isOrdered = true;
        for (var i = 0; i < pitch.Data.Count; i++)
        {
            var (tick, value) = pitch.Data[i];
            if (i > 0 && tick < pitch.Data[i - 1].Tick) isOrdered = false;
            if (value is { } definedValue &&
                !valuesByTick.TryAdd(tick, definedValue) && valuesByTick[tick] != definedValue)
                throw context.ConflictingPitch($".values[{i}]", tick);
        }

        // Both the base-note cursor and the emitted PITD curve require chronological points.
        int[]? inputIndices = null;
        if (!isOrdered)
        {
            var source = pitch.Data;
            inputIndices = Enumerable.Range(0, source.Count).OrderBy(i => source[i].Tick).ToArray();
            pitch = pitch with { Data = inputIndices.Select(i => source[i]).ToList() };
        }
        var data = PitchCalculation.ConvertRelativity(pitch, notes, toAbsolute: false);
        if (data is null || data.Count == 0) return [];

        var count = data.Count(point => point.Value.HasValue);
        context.Samples(count, "");
        var points = new List<(long Tick, double Value)>(count);
        for (var i = 0; i < data.Count; i++)
        {
            var (tick, value) = data[i];
            if (value is not { } relativePitch) continue;
            var inputIndex = inputIndices?[i] ?? i;
            context.OutputTick(tick, $".ticks[{inputIndex}]");
            var cents = RoundToCents(relativePitch);
            if (!double.IsFinite(cents) || cents < int.MinValue || cents > int.MaxValue)
                throw context.Range($".values[{inputIndex}]");
            if (points.Count > 0 && points[^1].Tick == tick)
            {
                if (points[^1].Value != cents)
                    throw context.ConflictingPitch($".values[{inputIndex}]", tick);
                continue;
            }
            points.Add((tick, cents));
        }
        return PitchCalculation.AppendPitchPointsForInterpolation(points, SamplingIntervalTick,
                () => context.Samples(1, ""))
            .ReduceRepeatedPitchPoints();
    }

    private static double RoundToCents(double pitch)
    {
        var cents = pitch * 100;
        var rounded = Math.Round(cents);
        // Kotlin roundToInt chooses the nearest integer, with ties toward +infinity.
        // Adjust only exact ties; adding 0.5 would misround adjacent doubles.
        return cents - rounded == 0.5 ? rounded + 1 : rounded;
    }

    private static void AppendInterpolated(
        List<(long Tick, double Value)> points,
        (long Tick, double Value) lastPoint,
        (long Tick, double Value) thisPoint,
        OpenUtauNotePitchData.Shape shape,
        UstxPitchContext context)
    {
        var span = context.Distance(lastPoint.Tick, thisPoint.Tick, "");
        // Reserve the endpoint and intermediate points before allocating them.
        context.Samples(span > 0 ? (span - 1) / SamplingIntervalTick + 1 : 1, "");
        var input = new List<(long Tick, double Value)> { lastPoint, thisPoint };
        var interpolated = shape switch
        {
            OpenUtauNotePitchData.Shape.EaseIn => input.InterpolateCosineEaseIn(SamplingIntervalTick),
            OpenUtauNotePitchData.Shape.EaseOut => input.InterpolateCosineEaseOut(SamplingIntervalTick),
            OpenUtauNotePitchData.Shape.EaseInOut => input.InterpolateCosineEaseInOut(SamplingIntervalTick),
            OpenUtauNotePitchData.Shape.Linear => input.InterpolateLinear(SamplingIntervalTick),
            _ => input.InterpolateCosineEaseInOut(SamplingIntervalTick),
        };
        for (var i = 1; i < interpolated.Count; i++)
        {
            var point = interpolated[i];
            context.Finite(point.Value, "");
            points.Add(point);
        }
    }

    private static void AppendSpline(
        List<(long Tick, double Value)> points,
        (long Tick, double Value) before,
        (long Tick, double Value) start,
        (long Tick, double Value) end,
        (long Tick, double Value) after,
        UstxPitchContext context)
    {
        if (before.Value == start.Value && start.Value == end.Value && end.Value == after.Value)
        {
            points.Add(end);
            return;
        }

        var span = context.Distance(start.Tick, end.Tick, "");
        context.Samples((span - 1) / SamplingIntervalTick + 1, "");
        var leftScale = (double)span / context.Distance(before.Tick, end.Tick, "");
        var rightScale = (double)span / context.Distance(start.Tick, after.Tick, "");
        for (var tick = start.Tick; end.Tick - tick > SamplingIntervalTick;)
        {
            tick += SamplingIntervalTick;
            var t = (double)(tick - start.Tick) / span;
            var u = 1 - t;
            // Cubic Hermite basis with Catmull–Rom tangents scaled by the tick spans.
            var left = t * u * u * leftScale;
            var right = t * t * u * rightScale;
            var value = ((1 + 2 * t) * u * u + right) * start.Value +
                        ((1 + 2 * u) * t * t + left) * end.Value -
                        left * before.Value - right * after.Value;
            points.Add((tick, context.Finite(value, "")));
        }

        points.Add(end);
    }

    private static void AppendStartAndEndPoint(List<(long Tick, double Value)> points, Note note, bool holdLastValue)
    {
        var start = note.TickOn;
        var end = note.TickOff;
        var hasStartPoint = points.Any(p => p.Tick == start);
        var hasEndPoint = points.Any(p => p.Tick == end);

        if (points.Count <= 1)
        {
            if (!hasStartPoint)
                points.Insert(0, (start, points.Count > 0 ? points[0].Value : 0.0));
            if (!hasEndPoint)
                points.Add((end, points.Count > 0 ? points[0].Value : 0.0));
            return;
        }

        var firstTick = points[0].Tick;
        var lastTick = points[^1].Tick;
        var tailValue = holdLastValue ? points[^1].Value : 0.0;

        if (!hasStartPoint)
        {
            if (firstTick > start)
            {
                points.Insert(0, (start, points[0].Value));
            }
            else if (lastTick < start)
            {
                points.Add((start, tailValue));
            }
            else
            {
                var lastPointBefore = points.Last(p => p.Tick < start);
                var firstPointAfter = points.First(p => p.Tick > start);
                var k = (firstPointAfter.Value - lastPointBefore.Value) /
                        (firstPointAfter.Tick - lastPointBefore.Tick);
                var y = lastPointBefore.Value + (start - lastPointBefore.Tick) * k;
                var idx = points.IndexOf(firstPointAfter);
                points.Insert(idx, (start, y));
            }
        }

        if (!hasEndPoint)
        {
            if (points[0].Tick > end)
            {
                points.Insert(0, (end, points[0].Value));
            }
            else if (points[^1].Tick < end)
            {
                // A held tail ends before the next note can begin at TickOff.
                var tailTick = holdLastValue ? end - 1 : end;
                if (points[^1].Tick < tailTick)
                    points.Add((tailTick, tailValue));
            }
            else
            {
                var lastPointBefore = points.Last(p => p.Tick < end);
                var firstPointAfter = points.First(p => p.Tick > end);
                var k = (firstPointAfter.Value - lastPointBefore.Value) /
                        (firstPointAfter.Tick - lastPointBefore.Tick);
                var y = lastPointBefore.Value + (end - lastPointBefore.Tick) * k;
                var idx = points.IndexOf(firstPointAfter);
                points.Insert(idx, (end, y));
            }
        }
    }

    private static List<(long Tick, double Value)> Resampled(
        List<(long Tick, double Value)> points,
        HashSet<long> keyPointPositions,
        UstxPitchContext context)
    {
        var result = points
            .GroupBy(p => p.Tick / SamplingIntervalTick * SamplingIntervalTick)
            .Select(g =>
            {
                var hasKeyPoint = false;
                double keyPointValue = 0;
                foreach (var p in g)
                {
                    if (keyPointPositions.Contains(p.Tick))
                    {
                        hasKeyPoint = true;
                        keyPointValue = p.Value;
                        break;
                    }
                }
                return hasKeyPoint
                    ? (Tick: g.Key, Value: keyPointValue)
                    : (Tick: g.Key, Value: context.Finite(g.Average(p => p.Value), ""));
            })
            .OrderBy(p => p.Tick)
            .ToList();

        var interpolated = new List<(long Tick, double Value)>();
        foreach (var point in result)
        {
            var lastPoint = interpolated.Count > 0 ? interpolated[^1] : ((long Tick, double Value)?)null;
            if (lastPoint is null)
            {
                context.Samples(1, "");
                interpolated.Add(point);
            }
            else
            {
                AppendInterpolated(interpolated, lastPoint.Value, point, OpenUtauNotePitchData.Shape.Linear, context);
            }
        }

        return interpolated;
    }
}
