using UtaFormatix.Core.Models;
using System.Text;

namespace UtaFormatix.Core.IO;

internal sealed class UstxInput(string filePath, List<ImportWarning> warnings)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HashSet<string> missing = new(StringComparer.Ordinal);
    private UstxInputSchema? schema;

    public string ReadText()
    {
        var bytes = File.ReadAllBytes(filePath);
        Encoding encoding = StrictUtf8;
        var start = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); start = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, false, true); start = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); start = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); start = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) start = 3;
        try { return encoding.GetString(bytes.AsSpan(start)); }
        catch (DecoderFallbackException error)
        { throw Error("USTX_ENCODING", $"$bytes[{start + error.Index}]", $"Invalid {encoding.WebName} input; characters cannot be silently replaced."); }
    }

    public YamlDotNet.Core.IParser Attach(YamlDotNet.Core.IParser parser) => schema = new UstxInputSchema(parser, this);
    public void Complete() => schema!.Complete();
    public string LastPath => schema?.LastPath ?? "$";
    private string At(string path) => $"{filePath} ({path})";
    public ProjectInputException Error(string code, string path, string reason) => new(code, At(path), reason);
    private void Warn(string code, string path, string reason) => warnings.Add(new ImportWarning.InputDiagnostic(code, At(path), reason));

    public void RecordMissing(string path, bool defaults)
    {
        if (defaults || path.Contains(".vibrato.", StringComparison.Ordinal)) missing.Add(path);
        if (defaults)
        {
            var message = path == "$.name" ? "Recovered the missing project name from the input filename."
                : path.EndsWith(".pitch", StringComparison.Ordinal) ? "Missing pitch data uses a neutral curve."
                : path.EndsWith(".vibrato", StringComparison.Ordinal) ? "Missing vibrato data disables vibrato."
                : "Missing interpolation shape uses io (ease in-out).";
            Warn("USTX_DEFAULT", path, message);
        }
    }

    private void Finite(double value, string path)
    {
        if (!double.IsFinite(value)) throw Error("USTX_NUMBER", path, "Expected a finite number.");
    }
    private void Text(string? value, string path)
    {
        if (value is null) return;
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw Error("USTX_TEXT", path, "Text contains an unpaired Unicode surrogate."); }
    }
    private void Positive(double value, string path)
    {
        Finite(value, path);
        if (value <= 0) throw Error("USTX_RANGE", path, "Expected a positive value.");
    }
    private long Add(long left, long right, string path)
    {
        try { return checked(left + right); }
        catch (OverflowException) { throw Error("USTX_RANGE", path, "Coordinate addition exceeds the Int64 tick axis."); }
    }

    public void Normalize(Ustx.UstxProject project)
    {
        if (missing.Contains("$.name")) project.Name = Path.GetFileNameWithoutExtension(filePath);
        Text(project.Name, "$.name");
        if (project.Resolution is { } resolution) Positive(resolution, "$.resolution");
        if (project.Bpm is { } bpm) Positive(bpm, "$.bpm");
        if (project.BeatPerBar is { } numerator) Positive(numerator, "$.beat_per_bar");
        if (project.BeatUnit is { } denominator) Positive(denominator, "$.beat_unit");
        NormalizeTiming(project);

        var byTrack = new List<(long On, long Off, string Path)>[project.Tracks.Count];
        for (var i = 0; i < byTrack.Length; i++)
        {
            byTrack[i] = [];
            Text(project.Tracks[i].TrackName, $"$.tracks[{i}].track_name");
        }
        for (var pi = 0; pi < project.VoiceParts.Count; pi++)
        {
            var part = project.VoiceParts[pi]; var path = $"$.voice_parts[{pi}]";
            if (part.TrackNo < 0 || part.TrackNo >= project.Tracks.Count)
                throw Error("USTX_TRACK", path + ".track_no", "Voice part refers to a nonexistent track; its destination cannot be inferred.");
            var trackStart = byTrack[part.TrackNo].Count;
            for (var ni = 0; ni < part.Notes.Count; ni++)
            {
                var note = part.Notes[ni]; var at = path + $".notes[{ni}]";
                Text(note.Lyric, at + ".lyric");
                if (note.Duration <= 0) throw Error("USTX_DURATION", at + ".duration", "Note duration must be positive.");
                var on = Add(part.Position, note.Position, at + ".position");
                var off = Add(on, note.Duration, at + ".duration");
                if (on < 0) throw Error("USTX_RANGE", at + ".position", "An absolute note onset cannot be negative; the note cannot be silently skipped.");
                byTrack[part.TrackNo].Add((on, off, at));
                note.Pitch ??= new Ustx.UstxPitch();
                note.Vibrato ??= new Ustx.UstxVibrato();
                var points = note.Pitch.Data;
                for (var di = 0; di < points.Count; di++)
                {
                    var point = points[di]; var where = at + $".pitch.data[{di}]";
                    Finite(point.X, where + ".x"); Finite(point.Y, where + ".y");
                    point.Shape ??= "io";
                    if (point.Shape is not ("i" or "o" or "io" or "l"))
                        throw Error("USTX_PITCH", where + ".shape", "Unknown pitch interpolation shape; an intended curve cannot be inferred.");
                }
                if (points.Zip(points.Skip(1), (a, b) => a.X > b.X).Any(v => v))
                {
                    note.Pitch.Data = points.OrderBy(p => p.X).ToList();
                    Warn("USTX_REORDERED", at + ".pitch.data", "Sorted pitch controls by time while retaining each value and outgoing shape.");
                }
                NormalizeVibrato(note.Vibrato, at + ".vibrato");
            }
            if (part.Notes.Zip(part.Notes.Skip(1), (a, b) => a.Position > b.Position).Any(v => v))
            {
                part.Notes = part.Notes.OrderBy(n => n.Position).ToList();
                byTrack[part.TrackNo].Sort(trackStart, part.Notes.Count,
                    Comparer<(long On, long Off, string Path)>.Create((a, b) => a.On.CompareTo(b.On)));
                Warn("USTX_REORDERED", path + ".notes", "Sorted notes by onset without discarding notes or their pitch data.");
            }
            NormalizeCurves(part, path);
        }
        for (var ti = 0; ti < byTrack.Length; ti++)
        {
            var original = byTrack[ti]; var sorted = original.OrderBy(n => n.On).ToList();
            for (var i = 1; i < sorted.Count; i++)
                if (sorted[i].On < sorted[i - 1].Off)
                    throw Error("USTX_OVERLAP", sorted[i].Path, "Notes overlap on one track; separate them into tracks instead of losing or truncating notes.");
            if (!original.SequenceEqual(sorted))
                Warn("USTX_REORDERED", $"$.tracks[{ti}].notes", "Imported notes in chronological order across voice parts.");
        }
    }

    private void NormalizeVibrato(Ustx.UstxVibrato value, string path)
    {
        var fields = new (string Name, double Value)[]
        {
            ("length", value.Length), ("period", value.Period), ("depth", value.Depth),
            ("in", value.In), ("out", value.Out), ("shift", value.Shift), ("drift", value.Drift),
        };
        foreach (var (name, number) in fields) Finite(number, path + "." + name);
        if (value.Length < 0 || value.Depth < 0 || value.In < 0 || value.Out < 0)
            throw Error("USTX_VIBRATO", path, "Vibrato length, depth and fade lengths cannot be negative.");
        var absent = fields.Where(f => missing.Contains(path + "." + f.Name)).Select(f => f.Name).ToList();
        if (value.Length > 0 && missing.Contains(path + ".depth"))
            throw Error("USTX_REQUIRED", path + ".depth", "Enabled vibrato requires an explicit depth.");
        if (value.Length > 0 && value.Depth > 0)
        {
            if (absent.Count > 0) throw Error("USTX_REQUIRED", path + "." + absent[0], "Enabled vibrato requires all of its parameters.");
            if (value.Period == 0)
            {
                value.Length = 0;
                Warn("USTX_VIBRATO_IGNORED", path + ".period", "Ignored vibrato because its period is zero.");
            }
            else if (value.Period < 0 || !double.IsFinite(1.0 / value.Period))
                throw Error("USTX_RANGE", path + ".period", "Vibrato period must produce a finite positive frequency.");
        }
        else if (absent.Count > 0)
            Warn("USTX_DEFAULT", path, "Inactive vibrato uses neutral defaults for omitted parameters: " + string.Join(", ", absent) + ".");
    }

    private void NormalizeCurves(Ustx.UstxVoicePart part, string path)
    {
        Ustx.UstxCurve? firstPitch = null;
        for (var ci = 0; ci < (part.Curves?.Count ?? 0); ci++)
        {
            var curve = part.Curves![ci]; var at = path + $".curves[{ci}]";
            if (curve.Xs.Count != curve.Ys.Count)
                throw Error("USTX_DIMENSION", at, "Curve xs and ys must have equal lengths; unmatched points cannot be truncated.");
            for (var i = 0; i < curve.Ys.Count; i++)
            {
                var value = curve.Ys[i]; Finite(value, at + $".ys[{i}]");
                if (value < int.MinValue || value > int.MaxValue || value != Math.Truncate(value))
                    throw Error("USTX_RANGE", at + $".ys[{i}]", "Curve values must be representable Int32 cents; fractional values cannot be silently truncated.");
                Add(curve.Xs[i], part.Position, at + $".xs[{i}]");
            }
            if (curve.Xs.Zip(curve.Xs.Skip(1), (a, b) => a > b).Any(v => v))
            {
                var pairs = curve.Xs.Zip(curve.Ys).OrderBy(p => p.First).ToList();
                curve.Xs = pairs.Select(p => p.First).ToList(); curve.Ys = pairs.Select(p => p.Second).ToList();
                Warn("USTX_REORDERED", at, "Sorted curve coordinates together with their values.");
            }
            for (var i = 1; i < curve.Xs.Count; i++)
                if (curve.Xs[i] == curve.Xs[i - 1])
                {
                    if (curve.Ys[i] != curve.Ys[i - 1]) throw Error("USTX_DUPLICATE", at + $".xs[{i}]", "Conflicting values at the same curve coordinate are ambiguous.");
                    Warn("USTX_DUPLICATE", at + $".xs[{i}]", "Retained an identical repeated control point.");
                }
            if (curve.Abbr != "pitd") continue;
            if (firstPitch is null) firstPitch = curve;
            else if (!firstPitch.Xs.SequenceEqual(curve.Xs) || !firstPitch.Ys.SequenceEqual(curve.Ys))
                throw Error("USTX_DUPLICATE", at, "Multiple conflicting pitd curves cannot be silently ignored.");
            else Warn("USTX_DUPLICATE", at, "Repeated identical pitd curve uses the same pitch data.");
        }
    }

    private void NormalizeTiming(Ustx.UstxProject project)
    {
        var tempos = project.Tempos;
        if (tempos is { Count: > 0 })
        {
            for (var i = 0; i < tempos.Count; i++)
            {
                if (tempos[i].Position < 0) throw Error("USTX_RANGE", $"$.tempos[{i}].position", "Tempo position cannot be negative.");
                Positive(tempos[i].Bpm, $"$.tempos[{i}].bpm");
            }
            if (tempos.Zip(tempos.Skip(1), (a, b) => a.Position > b.Position).Any(v => v))
            {
                project.Tempos = tempos = tempos.OrderBy(t => t.Position).ToList();
                Warn("USTX_REORDERED", "$.tempos", "Sorted tempo markers by position, preserving source order at equal positions.");
            }
            if (tempos[0].Position > 0)
            {
                tempos.Insert(0, new Ustx.UstxTempo { Position = 0, Bpm = project.Bpm ?? 120 });
                Warn("USTX_DEFAULT", "$.tempos", "Inserted the legacy/default tempo at tick zero before later markers.");
            }
        }
        else if (project.Bpm is null) Warn("USTX_DEFAULT", "$.tempos", "Used default tempo 120 BPM at tick zero.");
        var meters = project.TimeSignatures;
        if (meters is { Count: > 0 })
        {
            for (var i = 0; i < meters.Count; i++)
            {
                if (meters[i].BarPosition < 0) throw Error("USTX_RANGE", $"$.time_signatures[{i}].bar_position", "Meter position cannot be negative.");
                Positive(meters[i].BeatPerBar, $"$.time_signatures[{i}].beat_per_bar"); Positive(meters[i].BeatUnit, $"$.time_signatures[{i}].beat_unit");
            }
            if (meters.Zip(meters.Skip(1), (a, b) => a.BarPosition > b.BarPosition).Any(v => v))
            {
                project.TimeSignatures = meters = meters.OrderBy(t => t.BarPosition).ToList();
                Warn("USTX_REORDERED", "$.time_signatures", "Sorted time signatures by measure, preserving source order at equal positions.");
            }
            if (meters[0].BarPosition > 0)
            {
                meters.Insert(0, new Ustx.UstxTimeSignature { BarPosition = 0, BeatPerBar = project.BeatPerBar ?? 4, BeatUnit = project.BeatUnit ?? 4 });
                Warn("USTX_DEFAULT", "$.time_signatures", "Inserted the legacy/default meter at measure zero before later markers.");
            }
        }
        else if (project.BeatPerBar is null || project.BeatUnit is null)
            Warn("USTX_DEFAULT", "$.time_signatures", "Used default 4 for missing legacy meter components.");
    }
}
