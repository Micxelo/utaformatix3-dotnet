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
        List<Tempo> tempos)
    {
        var notePointsList = new List<List<(long Tick, double Value)>>();
        var tickTimeTransformer = new TickTimeTransformer(tempos);
        var lastKeyPos = -SafeSamplingIntervalTick;

        for (var i = 0; i < Math.Min(notes.Count, pitchData.Notes.Count); i++)
        {
            var note = notes[i];
            var notePitch = pitchData.Notes[i];
            var points = new List<(long Tick, double Value)>();
            var lastPointShape = OpenUtauNotePitchData.Shape.EaseInOut;
            var noteStartInMillis = tickTimeTransformer.TickToMilliSec(note.TickOn);
            var keyPointPositions = new List<long>();

            foreach (var rawPoint in notePitch.Points)
            {
                var x = tickTimeTransformer.MilliSecToTick(noteStartInMillis + rawPoint.X);
                x = Math.Max(x, lastKeyPos + SafeSamplingIntervalTick);
                lastKeyPos = x;
                keyPointPositions.Add(x);
                var y = rawPoint.Y / 10.0;
                var thisPoint = (x, y);
                var lastPoint = points.Count > 0 ? points[^1] : ((long Tick, double Value)?)null;

                if (lastPoint is not null && thisPoint.y != lastPoint.Value.Value)
                {
                    var interpolated = InterpolatePoints((lastPoint.Value.Tick, lastPoint.Value.Value), (x, y), lastPointShape);
                    points.AddRange(interpolated.Skip(1));
                }
                else
                {
                    points.Add(thisPoint);
                }

                lastPointShape = rawPoint.Shape;
            }

            AppendStartAndEndPoint(points, note);

            var pointsBefore = points.Where(p => p.Tick < note.TickOn).ToList();
            var pointsIn = points.Where(p => p.Tick >= note.TickOn && p.Tick <= note.TickOff).ToList();
            var pointsAfter = points.Where(p => p.Tick > note.TickOff).ToList();

            var pointsInNoteWithVibrato = pointsIn
                .AppendUtauNoteVibrato(notePitch.Vibrato, note, tickTimeTransformer, SamplingIntervalTick);

            var pointsWithVibrato = pointsBefore.Concat(pointsInNoteWithVibrato).Concat(pointsAfter).ToList();
            var pointsResampled = Resampled(pointsWithVibrato, SamplingIntervalTick, keyPointPositions);
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
                        ? prevNote.Key - note.Key
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
                .Select(g => (g.Key, Value: g.Sum(p => p.Value)))
                .ToList();

            allPointsFromNote.AddRange(pointsInSection);
            sectionBorder = nextSectionBorder;
        }

        var curvePoints = pitchData.Points
            .Select(p => (p.X, p.Y / 100.0))
            .ToList();
        var resampledCurvePoints = ResampledSimple(curvePoints, SamplingIntervalTick);

        var pitchPoints = allPointsFromNote
            .Concat(resampledCurvePoints)
            .GroupBy(p => p.Tick)
            .Select(g => (Tick: g.Key, Value: g.Sum(p => p.Value)))
            .OrderBy(p => p.Tick)
            .Where(p => p.Tick >= 0)
            .ToList();

        if (pitchPoints.Count == 0) return null;

        return new PitchModel(
            pitchPoints.Select(p => (p.Tick, (double?)p.Value)).ToList(),
            IsAbsolute: false);
    }

    public static PitchModel? MergePitchFromUstxParts(PitchModel? first, PitchModel? second)
    {
        if (first is null) return second;
        if (second is null) return first;

        var data = first.Data
            .Concat(second.Data)
            .Where(p => p.Value is not null)
            .Select(p => (p.Tick, p.Value!.Value))
            .GroupBy(p => p.Tick)
            .Select(g => (Tick: g.Key, Value: g.Sum(p => p.Value)))
            .OrderBy(p => p.Tick)
            .ToList();

        return new PitchModel(
            data.Select(p => (p.Tick, (double?)p.Value)).ToList(),
            first.IsAbsolute);
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
        this PitchModel? pitch, List<Note> notes)
    {
        var data = pitch?.GetRelativeData(notes);
        if (data is null || data.Count == 0) return [];

        return data
            .Select(p => (Tick: p.Tick, Value: Math.Round(p.Value * 100, MidpointRounding.AwayFromZero)))
            .ToList()
            .AppendPitchPointsForOpenUtauOutput()
            .ReduceRepeatedPitchPoints();
    }

    private static List<(long Tick, double Value)> InterpolatePoints(
        (long Tick, double Value) lastPoint,
        (long Tick, double Value) thisPoint,
        OpenUtauNotePitchData.Shape shape)
    {
        var input = new List<(long Tick, double Value)> { lastPoint, thisPoint };
        return shape switch
        {
            OpenUtauNotePitchData.Shape.EaseIn => input.InterpolateCosineEaseIn(SamplingIntervalTick),
            OpenUtauNotePitchData.Shape.EaseOut => input.InterpolateCosineEaseOut(SamplingIntervalTick),
            OpenUtauNotePitchData.Shape.EaseInOut => input.InterpolateCosineEaseInOut(SamplingIntervalTick),
            OpenUtauNotePitchData.Shape.Linear => input.InterpolateLinear(SamplingIntervalTick),
            _ => input.InterpolateCosineEaseInOut(SamplingIntervalTick),
        };
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
        long interval,
        List<long> keyPointPositions)
    {
        var result = points
            .GroupBy(p => p.Tick / interval * interval)
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
                    : (Tick: g.Key, Value: g.Average(p => p.Value));
            })
            .OrderBy(p => p.Tick)
            .ToList();

        var interpolated = new List<(long Tick, double Value)>();
        foreach (var point in result)
        {
            var lastPoint = interpolated.Count > 0 ? interpolated[^1] : ((long Tick, double Value)?)null;
            if (lastPoint is null)
            {
                interpolated.Add(point);
            }
            else
            {
                var pair = new List<(long Tick, double Value)> { lastPoint.Value, point };
                var interp = pair.InterpolateLinear(interval);
                interpolated.AddRange(interp.Skip(1));
            }
        }

        return interpolated;
    }

    private static List<(long Tick, double Value)> ResampledSimple(
        List<(long Tick, double Value)> points, long interval)
    {
        if (points.Count == 0) return [];

        return points
            .GroupBy(p => p.Tick / interval * interval)
            .Select(g => (Tick: g.Key, Value: g.Average(p => p.Value)))
            .OrderBy(p => p.Tick)
            .ToList();
    }

    private static List<(long Tick, double Value)> AppendPitchPointsForOpenUtauOutput(
        this List<(long Tick, double Value)> points) =>
        PitchCalculation.AppendPitchPointsForInterpolation(points, SamplingIntervalTick);
}
