using UtaFormatix.Core.IO;
using UtaFormatix.Core.Models;

namespace UtaFormatix.Core.Process.Pitch;

internal sealed class UstPitchContext(PitchSampleBudget budget, bool validateOnly, Func<int, string, string> path)
    : IVibratoPitchContext
{
    private int note;
    public bool ValidateOnly => validateOnly;
    public void AtNote(int index) => note = index;
    public string Path(string field) => path(note, field);
    public ProjectInputException Range(string field) => new("UST_RANGE", Path(field),
        "Pitch calculation is outside the supported numeric, tick, or output-field range.");
    public double Finite(double value, string field)
    {
        if (!double.IsFinite(value)) throw Range(field);
        return value;
    }
    public long Add(long left, long right, string field)
    {
        try { return checked(left + right); }
        catch (OverflowException) { throw Range(field); }
    }
    public long Distance(long left, long right, string field)
    {
        try { return checked(right - left); }
        catch (OverflowException) { throw Range(field); }
    }
    public long Tick(TickTimeTransformer time, double milliseconds, string field)
    {
        Finite(milliseconds, field);
        try { return time.SecToTickChecked(milliseconds / 1000.0); }
        catch (OverflowException) { throw Range(field); }
    }
    private void Spend(double count, string field)
    {
        if (!budget.TrySpend(count)) budget.Check(count, Path(field));
    }
    public void Source(long count, string field) => Spend(count, field);
    public void Samples(double count, string field)
    {
        if (!validateOnly) Spend(count, field);
    }
    public void Preview(double count, string field)
    {
        if (!validateOnly && !budget.Fits(count)) budget.Check(count, Path(field));
    }
    public void Values(IEnumerable<(long Tick, double Value)> points, string field)
    {
        foreach (var p in points) Finite(p.Value, field);
    }
    public void Curve(long from, long to, double y0, double y1, string curve)
    {
        var span = Distance(from, to, "PBW");
        Finite(y0 + y1, "PBY");
        var delta = Finite(y1 - y0, "PBY");
        if (curve == "s") Finite(span * delta, "PBY");
        Samples(span > 0 ? (span - 1) / UstConstants.SamplingIntervalTick + 1 : 1, "PBW");
    }

    public List<(long Tick, double? Value)>? Absolute(Models.Pitch pitch, List<Note> notes)
    {
        Source(pitch.Data.Count, "pitch");
        foreach (var p in pitch.Data) if (p.Value is { } value) Finite(value, "pitch");
        if (pitch.IsAbsolute) return pitch.Data;
        if (notes.Count == 0) return null;
        var borders = new List<long>();
        for (var i = 1; i < notes.Count; i++)
        {
            if (notes[i].TickOn < notes[i - 1].TickOff) throw Range("pitch");
            borders.Add(notes[i - 1].TickOff + (notes[i].TickOn - notes[i - 1].TickOff) / 2);
        }
        var result = new List<(long Tick, double? Value)>(pitch.Data.Count);
        var index = 0;
        foreach (var p in pitch.Data)
        {
            while (index < borders.Count && p.Tick >= borders[index]) index++;
            double? value = p.Value switch { null => 0.0, 0.0 => null, _ => Finite(p.Value.Value + notes[index].Key, "pitch") };
            result.Add((p.Tick, value));
        }
        return result;
    }

    public List<(long Tick, double? Value)> Resample(List<(long Tick, double? Value)> points, long interval)
    {
        if (points.Count == 0) return [];
        var left = points.Min(p => p.Tick);
        var right = points.Max(p => p.Tick);
        var span = Distance(left, right, "PitchBend");
        Samples(span / interval + 1, "PitchBend");
        var sorted = true;
        for (var i = 1; i < points.Count; i++) if (points[i].Tick < points[i - 1].Tick) { sorted = false; break; }
        var result = new List<(long Tick, double? Value)>();
        var previous = -1;
        var next = 0;
        for (var tick = left; ;)
        {
            double? value;
            if (sorted)
            {
                while (previous + 1 < points.Count && points[previous + 1].Tick <= tick) previous++;
                while (next < points.Count && points[next].Tick < tick) next++;
                value = (previous >= 0 ? points[previous].Value : null) ?? (next < points.Count ? points[next].Value : null);
            }
            else
            {
                // Preserve the existing input-order semantics for unsorted data.
                Samples(points.Count, "PitchBend");
                value = points.LastOrDefault(p => p.Tick <= tick).Value ?? points.FirstOrDefault(p => p.Tick >= tick).Value;
            }
            result.Add((tick, value));
            if (right - tick < interval) break;
            tick += interval;
        }
        return result;
    }

    public List<(long Tick, double Value)> Simplify(List<(long Tick, double Value)> points, long maxCount)
    {
        Samples(Math.Max(1, points.Count), "PBS");
        if (points.Count < 2) return points;
        var ranges = new Dictionary<(int From, int To), (double Distance, int Split)>();
        var keep = new bool[points.Count];
        for (var epsilon = 0.05; ; epsilon += 0.05)
        {
            Array.Clear(keep);
            keep[0] = keep[^1] = true;
            var pending = new Stack<(int From, int To)>();
            pending.Push((0, points.Count - 1));
            while (pending.TryPop(out var range))
            {
                if (!ranges.TryGetValue(range, out var shape))
                {
                    var first = points[range.From];
                    var last = points[range.To];
                    var dx = (double)Distance(first.Tick, last.Tick, "PBW");
                    var dy = Finite(last.Value - first.Value, "PBY");
                    var mag = Math.Sqrt(Finite(dx * dx + dy * dy, "PBY"));
                    if (mag > 0) { dx /= mag; dy /= mag; }
                    shape = (0.0, range.From);
                    Samples(Math.Max(0, range.To - range.From - 1), "PBY");
                    for (var i = range.From + 1; i < range.To; i++)
                    {
                        var x = (double)Distance(first.Tick, points[i].Tick, "PBW");
                        var y = Finite(points[i].Value - first.Value, "PBY");
                        var dot = Finite(dx * x + dy * y, "PBY");
                        var ax = x - dot * dx;
                        var ay = y - dot * dy;
                        var distance = Math.Sqrt(Finite(ax * ax + ay * ay, "PBY"));
                        if (distance > shape.Distance) shape = (distance, i);
                    }
                    ranges.Add(range, shape);
                }
                else Samples(1, "PBY");
                if (shape.Distance > epsilon)
                {
                    keep[shape.Split] = true;
                    pending.Push((shape.Split, range.To));
                    pending.Push((range.From, shape.Split));
                }
            }
            var result = points.Where((_, index) => keep[index]).ToList();
            if (result.Count < maxCount) return result;
        }
    }
}
