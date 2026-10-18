using System.Text.Json;
using System.Text.Json.Nodes;
using UtaFormatix.Core.Models;

namespace UtaFormatix.Core.IO;

internal sealed class S5pInput : SynthVInput
{
    private const long Rate = 1470000;
    private readonly HashSet<JsonObject> droppedPitch = new(ReferenceEqualityComparer.Instance);
    private long pitchSamples;
    private double maximumSecondsPerTick;
    private double maximumTempoTick;

    public S5pInput(ImportParams parameters, List<ImportWarning> warnings)
        : base(parameters, warnings, SynthVInputOptions.From(parameters.S5p ?? new()), "S5P", Error) { }

    public static S5pInputException Error(string code, string path, string message) => new(code, path, message);
    public bool SkipPitch(JsonObject track) => droppedPitch.Contains(track);

    public JsonObject Read(string filePath)
    {
        var text = ReadText(filePath);
        CheckJson(text, "$");
        var project = Object(JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = options.MaxDepth }), "$");
        var version = Integer(project, "version", "$", required: false, nullable: true);
        // Version 7 is the schema used by the original S5P template and fixtures.
        if (version is < 0 or > 7)
            throw Error("S5P_VERSION", "$.version", "Unsupported S5P version; expected a version between 0 and 7.");
        NormalizeTiming(project, "tempo");
        NormalizeTiming(project, "meter");
        var tracks = Array(project, "tracks", "$");
        Objects(tracks, "$.tracks", NormalizeTrack);
        return project;
    }

    private void NormalizeTiming(JsonObject project, string key)
    {
        var events = Array(project, key, "$", nullable: true);
        Objects(events, "$." + key, (item, at) =>
        {
            if (key == "tempo")
            {
                Integer(item, "position", at);
                var bpm = Number(item, "beatPerMinute", at);
                var rate = 60.0 / Constants.TicksInBeat / bpm;
                if (bpm <= 0 || !double.IsFinite(rate) || rate <= 0)
                    throw Error("S5P_RANGE", at + ".beatPerMinute", "BPM must be positive and representable in the time mapping.");
            }
            else
            {
                Integer(item, "measure", at, int32: true);
                if (Integer(item, "beatPerMeasure", at, int32: true) <= 0)
                    throw Error("S5P_RANGE", at + ".beatPerMeasure", "Numerator must be positive.");
                if (Integer(item, "beatGranularity", at, int32: true) <= 0)
                    throw Error("S5P_RANGE", at + ".beatGranularity", "Denominator must be positive.");
            }
        });
        long Position(JsonNode? item) => key == "tempo" ? item!["position"]!.GetValue<long>() / Rate : item!["measure"]!.GetValue<int>();
        for (var i = 1; i < events.Count; i++)
            if (Position(events[i]) < Position(events[i - 1]))
            {
                if (!Recover) throw Error("S5P_ORDER", "$." + key, "Timing markers must be in chronological order.");
                Warn("S5P_SORTED_TIMING", "$." + key, "Stably sorted timing markers during explicit recovery.");
                var sorted = events.OrderBy(Position).ToList();
                events.Clear(); foreach (var item in sorted) events.Add(item);
                break;
            }
        if (key != "tempo") return;
        maximumSecondsPerTick = events.Count == 0 ? 60.0 / Constants.TicksInBeat / Constants.DefaultBpm : 0;
        var offset = 0.0;
        for (var i = 0; i < events.Count; i++)
        {
            maximumSecondsPerTick = Math.Max(maximumSecondsPerTick, 60.0 / Constants.TicksInBeat / events[i]!["beatPerMinute"]!.GetValue<double>());
            maximumTempoTick = Math.Max(maximumTempoTick, Math.Abs((double)Position(events[i])));
            if (i > 0)
                offset += (Position(events[i]) - Position(events[i - 1])) * (60.0 / Constants.TicksInBeat / events[i - 1]!["beatPerMinute"]!.GetValue<double>());
            if (!double.IsFinite(offset)) throw Error("S5P_RANGE", "$.tempo", "Tempo mapping exceeds finite seconds.");
        }
    }

    private void NormalizeTrack(JsonObject track, string path)
    {
        String(track, "name", path);
        if (track["displayOrder"] is not null) Integer(track, "displayOrder", path, int32: true);
        var warningCount = warnings.Count;
        var notes = Array(track, "notes", path);
        if (warnings.Count != warningCount) droppedPitch.Add(track);
        Objects(notes, path + ".notes", (note, at) =>
        {
            var onset = Integer(note, "onset", at);
            var duration = Integer(note, "duration", at);
            Integer(note, "pitch", at, int32: true);
            String(note, "lyric", at);
            if (duration < Rate) throw Error("S5P_RANGE", at + ".duration", "Duration must be positive and at least one output tick.");
            var on = onset / Rate;
            if (on < 0) throw Error("S5P_RANGE", at + ".onset", "Note is before tick zero.");
            try { _ = checked(onset + duration); }
            catch (OverflowException) { throw Error("S5P_OVERFLOW", at, "Note end overflows the Int64 blick axis."); }
            FiniteTime(on + duration / (double)Rate, at);
        }, () => droppedPitch.Add(track));
        var sorted = notes.OrderBy(n => n!["onset"]!.GetValue<long>()).ToList();
        notes.Clear();
        long end = 0;
        foreach (var n in sorted)
        {
            var on = n!["onset"]!.GetValue<long>() / Rate;
            if (notes.Count > 0 && on < end)
            {
                var error = Error("S5P_OVERLAP", PathOf(n), "Note overlaps a previous note; lossless monophonic conversion is not possible.");
                if (!Recover) throw error;
                Warn("S5P_DROPPED_NOTE", error.FieldPath, error.Reason);
                droppedPitch.Add(track);
                continue;
            }
            notes.Add(n);
            end = on + n["duration"]!.GetValue<long>() / Rate;
        }
        try { NormalizePitch(track, path); }
        catch (S5pInputException ex) when (ex.Code != "S5P_LIMIT")
        {
            Drop(ex, "Discarded this track's pitch.");
            droppedPitch.Add(track);
        }
        if (droppedPitch.Contains(track))
            Warn("S5P_DROPPED_PITCH", path, "Omitted this track's pitch because recovery discarded or could not validate contributing data.");
    }

    private void NormalizePitch(JsonObject track, string path)
    {
        if (track["parameters"] is null) return;
        var p = Object(track["parameters"], path + ".parameters");
        path += ".parameters";
        long? interval = p["interval"] is null ? null : Integer(p, "interval", path);
        if (interval <= 0) throw Error("S5P_RANGE", path + ".interval", "Sampling interval must be positive.");
        if (p["pitchDelta"] is null) return;
        if (p["pitchDelta"] is not JsonArray points) throw Error("S5P_TYPE", path + ".pitchDelta", "Expected an array of coordinate/value pairs.");
        if (points.Count % 2 != 0) throw Error("S5P_POINTS", path + ".pitchDelta", "Expected complete coordinate/value pairs.");
        if (points.Count == 0) return;
        if (interval is null) throw Error("S5P_REQUIRED", path + ".interval", "A sampling interval is required for nonempty pitch data; no unit is inferred.");
        var multiplier = interval.Value / (double)Rate;
        for (var i = 0; i < points.Count; i++)
        {
            var at = $"{path}.pitchDelta[{i}]";
            var value = Number(points[i], at); points[i] = value;
            if (i % 2 != 0) continue;
            var tick = value * multiplier;
            if (!double.IsFinite(tick) || tick <= long.MinValue || tick >= long.MaxValue)
                throw Error("S5P_RANGE", at, "Pitch position cannot be represented as an Int64 tick.");
            FiniteTime(tick, at);
        }
        if (!parameters.SimpleImport && !droppedPitch.Contains(track))
        {
            var count = points.Count / 2;
            if (count > options.MaxPitchSamples - pitchSamples)
                throw Error("S5P_LIMIT", path + ".pitchDelta", $"Pitch processing exceeds the {options.MaxPitchSamples} sample budget; use simple import or raise the explicit limit.");
            pitchSamples += count;
        }
    }

    private void FiniteTime(double tick, string path)
    {
        if (!double.IsFinite((Math.Abs(tick) + maximumTempoTick) * maximumSecondsPerTick * 2))
            throw Error("S5P_RANGE", path, "Coordinate/tempo combination exceeds finite seconds.");
    }
}
