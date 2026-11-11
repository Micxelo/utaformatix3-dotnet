using UtaFormatix.Core.Models;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.Process.Pitch;

internal sealed record OpenUtauNotePitchData(
    List<OpenUtauNotePitchData.Point> Points,
    UtauNoteVibratoParams Vibrato)
{
    public sealed record Point(double X, double Y, Shape Shape);

    public enum Shape
    {
        EaseIn,
        EaseOut,
        EaseInOut,
        Linear,
    }

    public static Shape ParseShape(string? textValue) => textValue switch
    {
        "i" => Shape.EaseIn,
        "o" => Shape.EaseOut,
        "io" => Shape.EaseInOut,
        "l" => Shape.Linear,
        _ => Shape.EaseInOut,
    };

    public static string ShapeToText(Shape shape) => shape switch
    {
        Shape.EaseIn => "i",
        Shape.EaseOut => "o",
        Shape.EaseInOut => "io",
        Shape.Linear => "l",
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
            var points = new List<(long Tick, double Value)>();
            var lastPointShape = OpenUtauNotePitchData.Shape.EaseInOut;
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
                var y = rawPoint.Y / 10.0;
                var thisPoint = (x, y);
                var lastPoint = points.Count > 0 ? points[^1] : ((long Tick, double Value)?)null;

                if (lastPoint is not null && thisPoint.y != lastPoint.Value.Value)
                {
                    AppendInterpolated(points, lastPoint.Value, thisPoint, lastPointShape, pitchContext);
                }
                else
                {
                    points.Add(thisPoint);
                }

                lastPointShape = rawPoint.Shape;
            }

            var pointCount = points.Count;
            AppendStartAndEndPoint(points, note);
            pitchContext.Samples(points.Count - pointCount, ".data");

            var pointsBefore = points.Where(p => p.Tick < note.TickOn).ToList();
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

        var curvePoints = pitchData.Points
            .Select(p => (p.X, p.Y / 100.0))
            .ToList();
        var resampledCurvePoints = curvePoints.Count == 0
            ? []
            : Resampled(curvePoints, null, partContext.At($".curves[{curveIndex}]"));

        var pitchPoints = allPointsFromNote
            .Concat(resampledCurvePoints)
            .GroupBy(p => p.Tick)
            .Select(g => (Tick: g.Key, Value: partContext.Finite(g.Sum(p => p.Value), "")))
            .OrderBy(p => p.Tick)
            .Where(p => p.Tick >= 0)
            .ToList();

        if (pitchPoints.Count == 0) return null;

        return new PitchModel(
            pitchPoints.Select(p => (p.Tick, (double?)p.Value)).ToList(),
            IsAbsolute: false);
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
        for (var i = 0; i < pitch.Data.Count; i++)
        {
            var (tick, value) = pitch.Data[i];
            if (value is { } definedValue &&
                !valuesByTick.TryAdd(tick, definedValue) && valuesByTick[tick] != definedValue)
                throw context.ConflictingPitch($".values[{i}]", tick);
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
            var cents = RoundToCents(relativePitch);
            if (!double.IsFinite(cents) || cents < int.MinValue || cents > int.MaxValue)
                throw context.Range($".values[{i}]");
            points.Add((tick, cents));
        }
        var curve = PitchCalculation.AppendPitchPointsForInterpolation(points, SamplingIntervalTick,
                () => context.Samples(1, ""))
            .ReduceRepeatedPitchPoints();

        // Hold points can collide with other points when the input is out of order.
        valuesByTick.Clear();
        foreach (var (tick, cents) in curve)
        {
            if (!valuesByTick.TryAdd(tick, cents) && valuesByTick[tick] != cents)
                throw context.ConflictingPitch("", tick);
        }
        return curve;
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

    private static void AppendStartAndEndPoint(List<(long Tick, double Value)> points, Note note)
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

        if (!hasStartPoint)
        {
            if (firstTick > start)
            {
                points.Insert(0, (start, points[0].Value));
            }
            else if (lastTick < start)
            {
                points.Add((start, 0.0));
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
                points.Add((end, 0.0));
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
        HashSet<long>? keyPointPositions,
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
                    if (keyPointPositions?.Contains(p.Tick) == true)
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
