using UtaFormatix.Core.IO;
using UtaFormatix.Core.Models;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.Process.Pitch;

internal sealed record DvSegmentPitchRawData(long TickOffset, List<(int Tick, int Value)> Data);

internal sealed record DvNoteWithPitch(
    Note Note,
    int PorHead,
    int PorTail,
    int BenLen,
    int BenDep,
    List<(int Ms, int MinusCent)> Vibrato) : IRichNote<DvNoteWithPitch>
{
    public DvNoteWithPitch CopyWithNote(Note note) => this with { Note = note };
}

internal static class DeepVocalPitchConversion
{
    private const long SamplingIntervalTick = 4L;
    private const double PortamentoLengthMaxSec = 0.3125;
    private const double BendDownLengthFixedSec = 0.09375;
    private const double BendLengthMinSec = 0.375;
    private const double BendLengthMaxSec = 0.6875;
    private const double BendValueMax = 3.0;

    public static PitchModel? PitchFromDvTrack(
        List<DvSegmentPitchRawData> segments,
        List<DvNoteWithPitch> notes,
        List<Tempo> tempos)
    {
        var merged = MergePointsFromSegments(segments);
        var mergedSameTick = MergeSameTickPoints(merged);
        if (mergedSameTick is null) return null;
        var mergedSameValue = MergeSameValuePoints(mergedSameTick);
        var withDefault = ApplyDefaultPitch(mergedSameValue, notes, tempos);
        return new PitchModel(withDefault, IsAbsolute: true);
    }

    private static List<(long Tick, double? Value)> MergePointsFromSegments(
        List<DvSegmentPitchRawData> segments)
    {
        var result = new List<(long Tick, double? Value)>();
        foreach (var segment in segments)
        {
            foreach (var (rawTick, centValue) in segment.Data)
            {
                if (rawTick < 0) continue;
                var tick = (long)rawTick + segment.TickOffset;
                double? value = centValue >= 0
                    ? Dv.ConvertNoteKey(centValue / 100.0)
                    : null;
                result.Add((tick, value));
            }
        }
        return result;
    }

    private static List<(long Tick, double? Value)>? MergeSameTickPoints(
        List<(long Tick, double? Value)> points)
    {
        var grouped = points
            .GroupBy(p => p.Tick)
            .Select(g =>
            {
                var list = g.ToList();
                if (list.Count > 1)
                {
                    if (list.Any(p => p.Value is null))
                        return (g.Key, (double?)null);
                    var values = list.Where(p => p.Value is not null).Select(p => p.Value!.Value);
                    return (g.Key, (double?)values.Average());
                }
                return (g.Key, list[0].Value);
            })
            .OrderBy(p => p.Item1)
            .ToList();

        return grouped.Any(p => p.Item2 is not null) ? grouped : null;
    }

    private static List<(long Tick, double? Value)> MergeSameValuePoints(
        List<(long Tick, double? Value)> points)
    {
        var result = new List<(long Tick, double? Value)>();
        foreach (var point in points.OrderBy(p => p.Tick))
        {
            var lastValue = result.Count > 0 ? result[^1].Value : (double?)null;
            if (point.Value != lastValue)
                result.Add(point);
        }
        return result;
    }

    private static List<(long Tick, double? Value)> ApplyDefaultPitch(
        List<(long Tick, double? Value)> points,
        List<DvNoteWithPitch> notes,
        List<Tempo> tempos)
    {
        if (points.Count == 0 || notes.Count == 0) return points;

        var transformer = new TickTimeTransformer(tempos);
        var basePitch = GetBasePitch(notes, transformer);
        var bendDiff = GetBendPitch(notes, transformer);
        var vibratoDiff = GetVibratoPitch(notes, transformer);

        var allPoints = points.ToList();
        if (points[^1].Tick < notes[^1].Note.TickOff)
            allPoints.Add((notes[^1].Note.TickOff, (double?)null));

        allPoints = allPoints.Where(p => p.Value is not null).ToList();

        var result = new List<(long Tick, double? Value)>();
        foreach (var point in allPoints)
        {
            var lastPoint = result.Count > 0 ? result[^1] : (Tick: -1L, Value: (double?)null);
            var startTick = lastPoint.Tick >= 0 ? lastPoint.Tick : 0;
            var endTick = point.Tick;

            if (lastPoint.Value is null && result.Count > 0)
            {
                for (var tick = startTick; tick < endTick; tick += SamplingIntervalTick)
                {
                    var baseVal = basePitch.TryGetValue(tick, out var bv) ? bv : 0.0;
                    var bendVal = bendDiff.TryGetValue(tick, out var bdv) ? bdv : 0.0;
                    var vibVal = vibratoDiff.TryGetValue(tick, out var vv) ? vv : 0.0;
                    result.Add((tick, baseVal + bendVal + vibVal));
                }
            }
            result.Add(point);
        }
        return result;
    }

    private static Dictionary<long, double> GetBasePitch(
        List<DvNoteWithPitch> notes, TickTimeTransformer transformer)
    {
        var result = new List<(long Tick, double Value)>();

        for (var i = 0; i <= notes.Count; i++)
        {
            var lastNote = i > 0 ? notes[i - 1] : null;
            var thisNote = i < notes.Count ? notes[i] : null;

            var portamento = new List<(long Tick, double Value)>();
            if (lastNote is not null && thisNote is not null)
                portamento = GetPortamento(lastNote, transformer, thisNote);

            if (lastNote is not null)
            {
                var portFirstTick = portamento.Count > 0 ? portamento[0].Tick : lastNote.Note.TickOff;
                var halfStart = lastNote.Note.TickOn + (lastNote.Note.Length + 1) / 2;
                for (var tick = halfStart; tick < portFirstTick; tick++)
                    result.Add((tick, (double)lastNote.Note.Key));
            }

            result.AddRange(portamento);

            if (thisNote is not null)
            {
                var start = lastNote is null ? 0 : (portamento.Count > 0 ? portamento[^1].Tick : thisNote.Note.TickOn);
                var halfStart = thisNote.Note.TickOn + (thisNote.Note.Length + 1) / 2;
                for (var tick = start; tick < halfStart; tick++)
                    result.Add((tick, (double)thisNote.Note.Key));
            }
        }

        return MergeSameTickPoints(result.Select(p => (p.Tick, (double?)p.Value)).ToList())!
            .Where(p => p.Value is not null)
            .ToDictionary(p => p.Tick, p => p.Value!.Value);
    }

    private static Dictionary<long, double> GetBendPitch(
        List<DvNoteWithPitch> notes, TickTimeTransformer transformer)
    {
        var result = new List<(long Tick, double Value)>();

        foreach (var note in notes)
        {
            var startTick = note.Note.TickOn;
            var startSec = transformer.TickToSec(startTick);
            var valleySec = startSec + BendDownLengthFixedSec;
            var valleyTick = Math.Min(transformer.SecToTick(valleySec), note.Note.TickOn + note.Note.Length / 2 - 1);

            var lengthSec = note.BenLen <= 50
                ? BendLengthMinSec
                : (BendLengthMaxSec - BendLengthMinSec) * (note.BenLen - 50) / 50.0 + BendLengthMinSec;
            var endSec = startSec + lengthSec;
            var endTick = Math.Min(transformer.SecToTick(endSec), note.Note.TickOff - 1);

            var valleyValue = -BendValueMax * note.BenDep / 100.0;

            var bendDown = new List<(long Tick, double Value)> { (startTick, 0.0), (valleyTick, valleyValue) }
                .InterpolateLinear(1L);
            var bendUp = new List<(long Tick, double Value)> { (valleyTick, valleyValue), (endTick, 0.0) }
                .InterpolateCosineEaseInOut(1L)
                .Skip(1)
                .ToList();

            result.AddRange(bendDown);
            result.AddRange(bendUp);
        }

        var merged = MergeSameTickPoints(result.Select(p => (p.Tick, (double?)p.Value)).ToList());
        if (merged is null) return new Dictionary<long, double>();
        return merged.Where(p => p.Value is not null).ToDictionary(p => p.Tick, p => p.Value!.Value);
    }

    private static List<(long Tick, double Value)> GetPortamento(
        DvNoteWithPitch lastNote, TickTimeTransformer transformer, DvNoteWithPitch thisNote)
    {
        var tailLengthSec = PortamentoLengthMaxSec * lastNote.PorTail / 100.0;
        var startSec = transformer.TickToSec(lastNote.Note.TickOff) - tailLengthSec;
        var halfStartLast = lastNote.Note.TickOn + (lastNote.Note.Length + 1) / 2;
        var startTick = Math.Max(transformer.SecToTick(startSec), halfStartLast);

        var headLengthSec = PortamentoLengthMaxSec * thisNote.PorHead / 100.0;
        var endSec = transformer.TickToSec(thisNote.Note.TickOn) + headLengthSec;
        var halfStartThis = thisNote.Note.TickOn + (thisNote.Note.Length + 1) / 2;
        var endTick = Math.Min(transformer.SecToTick(endSec), halfStartThis - 1);

        return new List<(long Tick, double Value)>
        {
            (startTick, (double)lastNote.Note.Key),
            (endTick, (double)thisNote.Note.Key)
        }.InterpolateCosineEaseInOut(1L);
    }

    private static Dictionary<long, double> GetVibratoPitch(
        List<DvNoteWithPitch> notes, TickTimeTransformer transformer)
    {
        var result = new List<(long Tick, double Value)>();

        foreach (var note in notes)
        {
            var startTick = note.Note.TickOn;
            var startSec = transformer.TickToSec(startTick);

            var points = note.Vibrato
                .Select(v =>
                {
                    var tick = transformer.SecToTick(startSec + v.Ms / 1000.0);
                    var key = -v.MinusCent / 100.0;
                    return (Tick: tick, Value: key);
                })
                .Where(p => p.Tick >= startTick && p.Tick < note.Note.TickOff)
                .OrderBy(p => p.Tick)
                .ToList();

            result.AddRange(points.InterpolateLinear(1L));
        }

        var merged = MergeSameTickPoints(result.Select(p => (p.Tick, (double?)p.Value)).ToList());
        if (merged is null) return new Dictionary<long, double>();
        return merged.Where(p => p.Value is not null).ToDictionary(p => p.Tick, p => p.Value!.Value);
    }

    public static DvSegmentPitchRawData? GenerateForDv(this PitchModel pitch, List<Note> notes)
    {
        if (notes.Count == 0) return null;
        var absoluteData = pitch.GetAbsoluteData(notes);
        if (absoluteData is null || absoluteData.Count == 0) return null;

        var data = new List<(int Tick, int Value)> { (-1, -1) };
        data.AddRange(AppendPoints(absoluteData).Select(p =>
        {
            var rawValue = p.Value is not null
                ? (int)Math.Round(Dv.ConvertNoteKey(p.Value.Value) * 100, MidpointRounding.ToPositiveInfinity)
                : -1;
            return ((int)p.Tick, rawValue);
        }));

        return new DvSegmentPitchRawData(0L, data);
    }

    private static List<(long Tick, double? Value)> AppendPoints(List<(long Tick, double? Value)> points)
    {
        var results = new List<(long Tick, double? Value)>();
        double? lastValue = null;
        foreach (var point in points)
        {
            if (lastValue is null && point.Value is not null)
                results.Add((point.Tick, null));
            if (lastValue is not null && point.Value is null)
                results.Add((point.Tick, lastValue));
            results.Add(point);
            lastValue = point.Value;
        }
        return results;
    }
}
