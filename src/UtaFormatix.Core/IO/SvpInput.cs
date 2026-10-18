using System.Text.Json;
using System.Text.Json.Nodes;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process.Pitch;

namespace UtaFormatix.Core.IO;

internal sealed class SvpInput : SynthVInput
{
    public SvpInput(ImportParams parameters, List<ImportWarning> warnings)
        : base(parameters, warnings, SynthVInputOptions.From(parameters.Svp ?? new()), "SVP", Error) { }

    private const long Rate = 1470000;
    // The currently tested SVP schemas, including the Synthesizer V 2 samples.
    private const int MaxSupportedVersion = 153;
    private readonly Dictionary<JsonObject, JsonObject> groups = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Note, string> notePaths = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<JsonObject> droppedPitch = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, List<(JsonObject Group, string Path)>> library = new(StringComparer.Ordinal);
    private long pitchSamples;
    private double maximumSecondsPerTick;
    private double maximumTempoTick;
    private List<Tempo> tempos = [];
    private TickTimeTransformer timeTransformer = null!;

    public JsonObject GroupFor(JsonObject reference) => groups[reference];
    public bool SkipPitch(JsonObject track) => droppedPitch.Contains(track);
    public void RecordNote(Note note, JsonNode source) => notePaths[note] = PathOf(source);
    public string PathOf(Note note) => notePaths[note];
    public static SvpInputException Error(string code, string path, string message) => new(code, path, message);
    public JsonObject Read(string filePath)
    {
        var text = ReadText(filePath);

        var candidates = new List<(JsonObject Json, int Version, string Path)>();
        SvpInputException? lastError = null;
        long documentIndex = 0;
        foreach (var range in text.AsSpan().Split('\0'))
        {
            var index = documentIndex++;
            var segment = text.AsSpan()[range];
            if (segment.Trim(" \t\r\n").IsEmpty) continue;
            var document = segment.ToString();
            var path = $"$documents[{index}]";
            try
            {
                CheckJson(document, path);
                var json = Object(JsonNode.Parse(document, documentOptions: new JsonDocumentOptions { MaxDepth = options.MaxDepth }), path);
                var version = Integer(json, "version", path, required: false, defaultValue: 0, nullable: true);
                if (version is < 0 or > int.MaxValue) throw Error("SVP_VERSION", path + ".version", "Version must be a non-negative Int32.");
                candidates.Add((json, (int)version, path));
            }
            catch (SvpInputException ex) when (Recover && ex.Code != "SVP_LIMIT")
            { lastError = ex; Warn("SVP_SKIPPED_DOCUMENT", path, ex.Message); }
        }
        if (candidates.Count == 0) throw lastError ?? Error("SVP_EMPTY", "$", "No readable SVP document remains.");
        foreach (var candidate in candidates.OrderByDescending(c => c.Version))
        {
            var warningCount = warnings.Count;
            try
            {
                if (candidate.Version > MaxSupportedVersion)
                    throw Error("SVP_VERSION", candidate.Path + ".version", $"Version {candidate.Version} is not supported (maximum {MaxSupportedVersion}); no automatic fallback.");
                groups.Clear(); paths.Clear(); droppedPitch.Clear(); library.Clear(); pitchSamples = 0;
                maximumSecondsPerTick = 0; maximumTempoTick = 0;
                Normalize(candidate.Json, candidate.Path);
                return candidate.Json;
            }
            catch (SvpInputException ex) when (Recover && ex.Code != "SVP_LIMIT")
            {
                lastError = ex;
                warnings.RemoveRange(warningCount, warnings.Count - warningCount);
                Warn("SVP_SKIPPED_DOCUMENT", candidate.Path, $"Explicit recovery skipped this candidate: {ex.Message}");
            }
        }
        throw lastError ?? Error("SVP_EMPTY", "$", "No valid supported SVP document remains after recovery.");
    }

    private void Normalize(JsonObject project, string path)
    {
        var time = Object(project["time"], path + ".time");
        NormalizeTiming(time, path + ".time", "tempo");
        NormalizeTiming(time, path + ".time", "meter");
        tempos = time["tempo"]!.AsArray()
            .Select(t => new Tempo(t!["position"]!.GetValue<long>() / Rate, t["bpm"]!.GetValue<double>())).ToList();
        if (tempos.Count == 0) tempos.Add(new Tempo(0, Constants.DefaultBpm));
        timeTransformer = new TickTimeTransformer(tempos);
        var lib = Array(project, "library", path);
        Objects(lib, path + ".library", (group, at) =>
        {
            if (group["uuid"]?.GetValueKind() != JsonValueKind.String) return;
            var id = group["uuid"]!.GetValue<string>();
            if (!library.TryGetValue(id, out var list)) library[id] = list = [];
            list.Add((group, at));
        });
        var tracks = Array(project, "tracks", path);
        Objects(tracks, path + ".tracks", NormalizeTrack);
    }

    private void NormalizeTiming(JsonObject time, string path, string key)
    {
        var events = Array(time, key, path, nullable: true);
        Objects(events, path + "." + key, (item, at) =>
        {
            if (key == "tempo")
            {
                Integer(item, "position", at);
                var bpm = Number(item, "bpm", at);
                var rate = 60.0 / Constants.TicksInBeat / bpm;
                if (bpm <= 0 || !double.IsFinite(rate) || rate <= 0)
                    throw Error("SVP_RANGE", at + ".bpm", "BPM must be positive and representable in the time mapping.");
                maximumSecondsPerTick = Math.Max(maximumSecondsPerTick, rate);
                maximumTempoTick = Math.Max(maximumTempoTick, Math.Abs((double)item["position"]!.GetValue<long>() / Rate));
            }
            else
            {
                Integer(item, "index", at, int32: true);
                if (Integer(item, "numerator", at, int32: true) <= 0) throw Error("SVP_RANGE", at + ".numerator", "Numerator must be positive.");
                if (Integer(item, "denominator", at, int32: true) <= 0) throw Error("SVP_RANGE", at + ".denominator", "Denominator must be positive.");
            }
        });
        var positionKey = key == "tempo" ? "position" : "index";
        long Position(JsonNode? node) => key == "tempo" ? node![positionKey]!.GetValue<long>() / Rate : node![positionKey]!.GetValue<int>();
        for (var i = 1; i < events.Count; i++)
            if (Position(events[i]) < Position(events[i - 1]))
            {
                var error = Error("SVP_ORDER", path + "." + key, "Timing markers must be in chronological order.");
                if (!Recover) throw error;
                Warn("SVP_SORTED_TIMING", error.FieldPath, "Stably sorted timing markers during explicit recovery.");
                var sorted = events.OrderBy(Position).ToList(); events.Clear(); foreach (var item in sorted) events.Add(item);
                break;
            }
        if (key == "tempo")
        {
            if (events.Count == 0) maximumSecondsPerTick = 60.0 / Constants.TicksInBeat / Constants.DefaultBpm;
            var offset = 0.0;
            for (var i = 1; i < events.Count; i++)
            {
                offset += (Position(events[i]) - Position(events[i - 1])) * (60.0 / Constants.TicksInBeat / events[i - 1]!["bpm"]!.GetValue<double>());
                if (!double.IsFinite(offset)) throw Error("SVP_RANGE", path + ".tempo", "Tempo mapping exceeds finite seconds.");
            }
        }
    }

    private void NormalizeTrack(JsonObject track, string path)
    {
        String(track, "name", path);
        if (track["dispOrder"] is not null) Integer(track, "dispOrder", path, int32: true);
        var main = track["mainGroup"];
        var reference = track["mainRef"];
        if (main is not null || reference is not null)
        {
            try { Bind(Object(reference, path + ".mainRef"), Object(main, path + ".mainGroup"), path + ".mainRef", path + ".mainGroup", track); }
            catch (SvpInputException ex) when (ex.Code != "SVP_LIMIT")
            { Drop(ex, "Discarded the main group and its pitch."); track["mainGroup"] = null; track["mainRef"] = null; droppedPitch.Add(track); }
        }
        var warningCount = warnings.Count;
        var references = Array(track, "groups", path, nullable: true);
        if (warnings.Count != warningCount) droppedPitch.Add(track);
        Objects(references, path + ".groups", (item, at) =>
        {
            var id = String(item, "groupID", at, required: true)!;
            if (!library.TryGetValue(id, out var matches) || matches.Count != 1)
                throw Error("SVP_REFERENCE", at + ".groupID", "Reference must resolve to exactly one library group.");
            Bind(item, matches[0].Group, at, matches[0].Path, track);
        }, () => droppedPitch.Add(track));
        if (droppedPitch.Contains(track)) Warn("SVP_DROPPED_PITCH", path, "Omitted this track's pitch because recovery discarded or could not validate contributing data.");
    }

    private void Bind(JsonObject reference, JsonObject sourceGroup, string refPath, string groupPath, JsonObject track)
    {
        var group = Recover ? sourceGroup.DeepClone().AsObject() : sourceGroup;
        paths.TryAdd(group, groupPath);
        var uuid = String(group, "uuid", groupPath, required: true);
        var id = String(reference, "groupID", refPath, required: true);
        if (uuid != id) throw Error("SVP_REFERENCE", refPath + ".groupID", "Reference does not match the group's uuid.");
        var offset = Integer(reference, "blickOffset", refPath, required: false);
        var keyOffset = Integer(reference, "pitchOffset", refPath, required: false, int32: true);
        var warningCount = warnings.Count;
        var notes = Array(group, "notes", groupPath);
        if (warnings.Count != warningCount) droppedPitch.Add(track);
        Objects(notes, groupPath + ".notes", (note, at) =>
        {
            var onset = Integer(note, "onset", at);
            var duration = Integer(note, "duration", at);
            var key = Integer(note, "pitch", at, int32: true);
            String(note, "lyrics", at); String(note, "phonemes", at);
            if (duration < Rate) throw Error("SVP_RANGE", at + ".duration", "Duration must be positive and at least one output tick.");
            try
            {
                var on = checked(onset + offset) / Rate;
                _ = checked(checked(onset + offset) + duration);
                _ = checked(on + duration / Rate);
                _ = checked((int)key + (int)keyOffset);
                if (on < 0) throw Error("SVP_RANGE", at + ".onset", $"Note is before tick zero after applying {refPath}.blickOffset.");
                FiniteTime(on + duration / (double)Rate, at);
            }
            catch (OverflowException) { throw Error("SVP_OVERFLOW", at, $"Note position or key overflows after applying reference {refPath}."); }
        }, () => droppedPitch.Add(track));
        // Pitch vibrato ranges must use the same chronological note order as
        // the imported track. Keep source paths recorded before reordering.
        var orderedNotes = notes.OrderBy(n => n!["onset"]!.GetValue<long>()).ToList();
        notes.Clear(); foreach (var item in orderedNotes) notes.Add(item);
        try
        {
            ValidateVibrato(reference, "voice", refPath);
            foreach (var n in notes) ValidateVibrato(n!.AsObject(), "attributes", PathOf(n));
            if (group["parameters"] is not null)
            {
                var p = Object(group["parameters"], groupPath + ".parameters");
                var baseMagnitude = Curve(p, "pitchDelta", groupPath + ".parameters", offset, envelope: false) / 100;
                var envelope = Math.Max(1, Curve(p, "vibratoEnv", groupPath + ".parameters", offset, envelope: true));
                foreach (var n in p["pitchDelta"]?["points"]?.AsArray() is { Count: > 0 } ? notes : new JsonArray())
                {
                    var attrs = n!["attributes"];
                    var voice = reference["voice"];
                    var depth = Math.Abs(attrs?["dF0Vbr"]?.GetValue<double>() ?? voice?["dF0Vbr"]?.GetValue<double>() ?? 1);
                    if (depth == 0) continue;
                    var start = attrs?["tF0VbrStart"]?.GetValue<double>() ?? voice?["tF0VbrStart"]?.GetValue<double>() ?? 0.25;
                    var on = checked(n["onset"]!.GetValue<long>() + offset) / Rate;
                    var off = checked(on + n["duration"]!.GetValue<long>() / Rate);
                    var startSec = timeTransformer.TickToSec(on) + start;
                    if (startSec >= timeTransformer.TickToSec(off)) continue;
                    long startTick;
                    long distance;
                    try
                    {
                        startTick = timeTransformer.SecToTickChecked(startSec);
                        distance = checked(off - startTick);
                    }
                    catch (OverflowException) { throw Error("SVP_RANGE", PathOf(n) + ".attributes.tF0VbrStart", "Vibrato start cannot be represented safely on the tick axis."); }
                    var frequency = Math.Abs(attrs?["fF0Vbr"]?.GetValue<double>() ?? voice?["fF0Vbr"]?.GetValue<double>() ?? 5.5);
                    var phase = Math.Abs(attrs?["pF0Vbr"]?.GetValue<double>() ?? 0);
                    var rate = tempos.LastOrDefault(t => t.TickPosition <= on)?.Bpm.BpmToSecPerTick() ?? Constants.DefaultBpm.BpmToSecPerTick();
                    // Match the multiplication order used by pitch processing,
                    // including its intermediate values, in both import modes.
                    var rad = 2 * Math.PI * frequency * rate * Math.Abs((double)distance) + phase;
                    if (!double.IsFinite(rad) || !double.IsFinite(baseMagnitude + depth * 0.5 * envelope))
                        throw Error("SVP_RANGE", PathOf(n) + ".attributes", "Vibrato exceeds the supported finite processing range.");
                }
            }
            if (!parameters.SimpleImport && !droppedPitch.Contains(track))
            {
                var points = group["parameters"]?["pitchDelta"]?["points"]?.AsArray();
                if (points is { Count: > 0 })
                {
                    foreach (var n in notes)
                    {
                        var attrs = n!["attributes"];
                        var depth = attrs?["dF0Vbr"]?.GetValue<double>() ?? reference["voice"]?["dF0Vbr"]?.GetValue<double>() ?? 1;
                        if (depth != 0) Budget(n["duration"]!.GetValue<long>() / Rate / 4.0 + 2, PathOf(n));
                    }
                }
            }
        }
        catch (SvpInputException ex) when (ex.Code != "SVP_LIMIT")
        { Drop(ex, "Discarded this track's pitch."); droppedPitch.Add(track); }
        groups[reference] = group;
    }

    private void ValidateVibrato(JsonObject owner, string key, string path)
    {
        if (owner[key] is null) return;
        var obj = Object(owner[key], path + "." + key);
        foreach (var field in new[] { "tF0VbrStart", "tF0VbrLeft", "tF0VbrRight", "dF0Vbr", "fF0Vbr", "pF0Vbr" })
        {
            if (obj[field] is null) continue;
            var value = Number(obj, field, path + "." + key);
            if ((field == "tF0VbrLeft" || field == "tF0VbrRight") && value < 0)
                throw Error("SVP_RANGE", path + "." + key + "." + field, "Vibrato easing length must not be negative.");
        }
    }

    private double Curve(JsonObject owner, string key, string path, long offset, bool envelope)
    {
        if (owner[key] is null) return 0;
        path += "." + key;
        var curve = Object(owner[key], path);
        _ = String(curve, "mode", path);
        var points = Array(curve, "points", path, nullable: true);
        if (points.Count % 2 != 0) throw Error("SVP_POINTS", path + ".points", "Expected complete coordinate/value pairs.");
        var min = double.PositiveInfinity; var max = double.NegativeInfinity;
        var magnitude = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var value = Number(points[i], $"{path}.points[{i}]");
            points[i] = value;
            if (i % 2 == 0)
            {
                var tick = (value + offset) / Rate;
                if (!double.IsFinite(tick) || tick <= long.MinValue || tick >= long.MaxValue)
                    throw Error("SVP_RANGE", $"{path}.points[{i}]", "Pitch position cannot be represented as an Int64 tick.");
                FiniteTime(tick, $"{path}.points[{i}]");
                min = Math.Min(min, tick); max = Math.Max(max, tick);
            }
            else
            {
                magnitude = Math.Max(magnitude, Math.Abs(value));
                if (envelope && magnitude > double.MaxValue / 2)
                    throw Error("SVP_RANGE", $"{path}.points[{i}]", "Envelope exceeds the supported finite interpolation range.");
            }
        }
        if (points.Count > 0)
        {
            // The current interpolation and duplicate averaging accumulate
            // finite doubles. Reject magnitudes that could overflow those
            // operations, including in simple import, rather than emit NaN.
            var processingMagnitude = envelope ? magnitude : magnitude / 100;
            var divisor = Math.Max(2, 2 * (Math.Ceiling(max - min) + 2) + points.Count / 2.0);
            if (processingMagnitude > double.MaxValue / divisor)
                throw Error("SVP_RANGE", path + ".points", "Curve magnitude exceeds the supported finite interpolation range.");
        }
        if (!parameters.SimpleImport && points.Count > 0)
            Budget((max - min) / (envelope ? 1 : 4) + points.Count / 2.0 + 2, path);
        return magnitude;
    }

    private void Budget(double count, string path)
    {
        if (!double.IsFinite(count) || count > options.MaxPitchSamples - pitchSamples)
            throw Error("SVP_LIMIT", path, $"Pitch processing exceeds the {options.MaxPitchSamples} sample budget; use simple import or raise the explicit limit.");
        pitchSamples += (long)Math.Ceiling(count);
    }

    private void FiniteTime(double tick, string path)
    {
        if (!double.IsFinite((Math.Abs(tick) + maximumTempoTick) * maximumSecondsPerTick * 2))
            throw Error("SVP_RANGE", path, "Coordinate/tempo combination exceeds finite seconds.");
    }
}
