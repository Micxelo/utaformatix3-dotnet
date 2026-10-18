using System.Text.Json;
using System.Text.Json.Nodes;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;
using UtaFormatix.Core.Process.Pitch;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.IO;

public static class S5p
{
    private const long TickRate = 1470000L;
    private const long DefaultInterval = 5512500L;

    private static readonly string TemplateJson = LoadTemplate();

    private static string LoadTemplate()
    {
        var assembly = typeof(S5p).Assembly;
        using var stream = assembly.GetManifestResourceStream("UtaFormatix.Core.Resources.template.s5p");
        if (stream == null)
            throw new InvalidOperationException("S5P template not found in embedded resources");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static Project Parse(string filePath, ImportParams? importParams = null)
    {
        var parameters = importParams ?? new ImportParams();
        var warnings = new List<ImportWarning>();
        var input = new S5pInput(parameters, warnings);
        var project = input.Read(filePath);

        var timeSignatures = ParseTimeSignatures(project, warnings);
        var tempos = ParseTempos(project, warnings);
        var tracks = ParseTracks(project, parameters, input);

        return new Project(
            Format: Format.S5p,
            Name: Path.GetFileNameWithoutExtension(filePath),
            Tracks: tracks,
            TimeSignatures: timeSignatures,
            Tempos: tempos,
            MeasurePrefix: 0,
            ImportWarnings: warnings);
    }

    private static List<TimeSignature> ParseTimeSignatures(JsonObject project, List<ImportWarning> warnings)
    {
        var meterArray = project["meter"]?.AsArray();
        if (meterArray is null || meterArray.Count == 0)
        {
            warnings.Add(new ImportWarning.TimeSignatureNotFound());
            return [new TimeSignature(0, Constants.DefaultMeterHigh, Constants.DefaultMeterLow)];
        }

        return meterArray
            .Select(m => new TimeSignature(
                MeasurePosition: m!["measure"]!.GetValue<int>(),
                Numerator: m["beatPerMeasure"]!.GetValue<int>(),
                Denominator: m["beatGranularity"]!.GetValue<int>()))
            .ToList();
    }

    private static List<Tempo> ParseTempos(JsonObject project, List<ImportWarning> warnings)
    {
        var tempoArray = project["tempo"]?.AsArray();
        if (tempoArray is null || tempoArray.Count == 0)
        {
            warnings.Add(new ImportWarning.TempoNotFound());
            return [new Tempo(0, Constants.DefaultBpm)];
        }

        return tempoArray
            .Select(t => new Tempo(
                TickPosition: t!["position"]!.GetValue<long>() / TickRate,
                Bpm: t["beatPerMinute"]!.GetValue<double>()))
            .ToList();
    }

    private static List<Track> ParseTracks(JsonObject project, ImportParams parameters, S5pInput input)
    {
        var trackArray = project["tracks"]?.AsArray() ?? [];
        return trackArray
            .Select((t, i) => ParseTrack(t!.AsObject(), i, parameters, input))
            .ToList();
    }

    private static Track ParseTrack(JsonObject trackNode, int index, ImportParams parameters, S5pInput input)
    {
        var trackName = trackNode["name"]?.GetValue<string>() ?? $"Track {index + 1}";
        var notes = ParseNotes(trackNode, parameters.DefaultLyric);

        PitchModel? pitch = null;
        if (!parameters.SimpleImport && !input.SkipPitch(trackNode))
            pitch = ParsePitch(trackNode);

        return new Track(index, trackName, notes, pitch);
    }

    private static List<Note> ParseNotes(JsonObject trackNode, string defaultLyric)
    {
        var notesArray = trackNode["notes"]?.AsArray() ?? [];
        var noteId = 0;

        return notesArray
            .Select(n =>
            {
                var onset = n!["onset"]!.GetValue<long>();
                var duration = n["duration"]!.GetValue<long>();
                var key = n["pitch"]!.GetValue<int>();
                var lyric = n["lyric"]?.GetValue<string>();

                var tickOn = onset / TickRate;

                return new Note(
                    Id: noteId++,
                    Key: key,
                    Lyric: LyricWhitespace.IsNullOrBlank(lyric) ? defaultLyric : lyric!,
                    TickOn: tickOn,
                    TickOff: tickOn + duration / TickRate);
            })
            .ToList();
    }

    private static PitchModel? ParsePitch(JsonObject trackNode)
    {
        var parameters = trackNode["parameters"]?.AsObject();
        var pitchDelta = parameters?["pitchDelta"]?.AsArray();
        if (pitchDelta is null)
            return new PitchModel([], IsAbsolute: false);
        if (pitchDelta.Count == 0)
            return null;

        var interval = parameters!["interval"]!.GetValue<long>();
        var tickMultiplier = interval / (double)TickRate;

        var points = new List<(long Tick, double? Value)>();
        for (var i = 0; i < pitchDelta.Count; i += 2)
        {
            var rawTick = pitchDelta[i]!.GetValue<double>();
            var centValue = pitchDelta[i + 1]!.GetValue<double>();

            var tick = PitchTickRounding.Round(rawTick * tickMultiplier);
            var value = centValue / 100.0;
            points.Add((tick, value));
        }

        return points.Count > 0 ? new PitchModel(points, IsAbsolute: false) : null;
    }

    public static (byte[] Data, string FileName, List<ExportNotification> Notifications) Generate(
        Project project, IEnumerable<FeatureConfig>? features = null)
    {
        var featureList = features?.ToList() ?? [];
        var jsonText = GenerateContent(project, featureList);

        var notifications = new List<ExportNotification>();
        if (featureList.Contains(Feature.ConvertPitch))
            notifications.Add(new ExportNotification.PitchDataExported());

        var data = System.Text.Encoding.UTF8.GetBytes(jsonText);
        var name = Format.S5p.GetFileName(project.Name);
        return (data, name, notifications);
    }

    public static void GenerateFile(Project project, string filePath, IEnumerable<FeatureConfig>? features = null)
    {
        var (data, _, _) = Generate(project, features);
        AtomicFile.Write(filePath, temporary => File.WriteAllBytes(temporary, data));
    }

    private static string GenerateContent(Project project, List<FeatureConfig> features)
    {
        ValidateForExport(project);
        var s5p = JsonNode.Parse(TemplateJson)!.AsObject();

        var meterArray = new JsonArray();
        foreach (var ts in project.TimeSignatures)
        {
            meterArray.Add((JsonNode?)new JsonObject
            {
                ["measure"] = ts.MeasurePosition,
                ["beatPerMeasure"] = ts.Numerator,
                ["beatGranularity"] = ts.Denominator,
            });
        }
        s5p["meter"] = meterArray;

        var tempoArray = new JsonArray();
        foreach (var tempo in project.Tempos)
        {
            tempoArray.Add((JsonNode?)new JsonObject
            {
                ["position"] = checked(tempo.TickPosition * TickRate),
                ["beatPerMinute"] = tempo.Bpm,
            });
        }
        s5p["tempo"] = tempoArray;

        var emptyTrack = s5p["tracks"]!.AsArray()[0]!.AsObject();
        var newTracks = new JsonArray();
        foreach (var track in project.Tracks)
        {
            newTracks.Add((JsonNode?)GenerateTrack(track, emptyTrack, features));
        }
        s5p["tracks"] = newTracks;

        return s5p.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static JsonObject GenerateTrack(Track track, JsonObject emptyTrack, List<FeatureConfig> features)
    {
        var newTrack = emptyTrack.DeepClone().AsObject();
        newTrack["name"] = track.Name;
        newTrack["displayOrder"] = track.Id;

        var notesArray = new JsonArray();
        foreach (var note in track.Notes)
        {
            notesArray.Add((JsonNode?)new JsonObject
            {
                ["onset"] = checked(note.TickOn * TickRate),
                ["duration"] = checked(note.Length * TickRate),
                ["lyric"] = note.Lyric,
                ["pitch"] = note.Key,
            });
        }
        newTrack["notes"] = notesArray;

        var trackParameters = newTrack["parameters"]!.AsObject();
        trackParameters["interval"] = DefaultInterval;
        trackParameters["pitchDelta"] = GeneratePitchData(track, features);

        return newTrack;
    }

    private static JsonArray GeneratePitchData(Track track, List<FeatureConfig> features)
    {
        var array = new JsonArray();
        if (!features.Contains(Feature.ConvertPitch)) return array;

        var data = track.Pitch?.GetRelativeData(track.Notes);
        if (data is null) return array;

        var divisor = DefaultInterval / (double)TickRate;
        foreach (var (tick, value) in data)
        {
            array.Add((JsonNode?)JsonValue.Create(tick / divisor));
            array.Add((JsonNode?)JsonValue.Create(value * 100.0));
        }

        return array;
    }

    private static void ValidateForExport(Project project)
    {
        void Require(bool valid, string path, string message)
        { if (!valid) throw S5pInput.Error("S5P_EXPORT", path, message); }
        void Text(string? value, string path)
        {
            if (value is null) return;
            try { _ = new System.Text.UTF8Encoding(false, true).GetByteCount(value); }
            catch (System.Text.EncoderFallbackException) { throw S5pInput.Error("S5P_UNICODE", path, "String contains an isolated surrogate."); }
        }
        void Position(long tick, string path)
        {
            try { _ = checked(tick * TickRate); }
            catch (OverflowException) { throw S5pInput.Error("S5P_OVERFLOW", path, "Tick cannot be represented as an Int64 S5P position."); }
        }
        Require(project.Tempos.Count > 0, "$.tempos", "At least one tempo is required.");
        Require(project.TimeSignatures.Count > 0, "$.timeSignatures", "At least one time signature is required.");
        Text(project.Name, "$.name");
        for (var i = 0; i < project.Tempos.Count; i++)
        {
            var tempo = project.Tempos[i]; var path = $"$.tempos[{i}]";
            Position(tempo.TickPosition, path + ".tickPosition");
            Require(double.IsFinite(tempo.Bpm) && tempo.Bpm > 0, path + ".bpm", "BPM must be finite and positive.");
            Require(i == 0 || tempo.TickPosition >= project.Tempos[i - 1].TickPosition, "$.tempos", "Tempos must be in chronological order.");
        }
        for (var i = 0; i < project.TimeSignatures.Count; i++)
        {
            var meter = project.TimeSignatures[i]; var path = $"$.timeSignatures[{i}]";
            Require(meter.Numerator > 0 && meter.Denominator > 0, path, "Time signature components must be positive.");
            Require(i == 0 || meter.MeasurePosition >= project.TimeSignatures[i - 1].MeasurePosition, "$.timeSignatures", "Time signatures must be in chronological order.");
        }
        for (var ti = 0; ti < project.Tracks.Count; ti++)
        {
            var track = project.Tracks[ti]; var path = $"$.tracks[{ti}]";
            Text(track.Name, path + ".name");
            for (var ni = 0; ni < track.Notes.Count; ni++)
            {
                var note = track.Notes[ni]; var at = $"{path}.notes[{ni}]";
                Require(note.TickOn >= 0 && note.TickOff > note.TickOn, at, "Note must have a non-negative start and positive duration.");
                Require(ni == 0 || note.TickOn >= track.Notes[ni - 1].TickOff, at, "Notes must be ordered and must not overlap.");
                Position(note.TickOn, at + ".tickOn"); Position(note.TickOff, at + ".tickOff"); Position(note.Length, at + ".duration");
                Text(note.Lyric, at + ".lyric");
            }
            if (track.Pitch is null) continue;
            var multiplier = DefaultInterval / (double)TickRate;
            for (var pi = 0; pi < track.Pitch.Data.Count; pi++)
            {
                var (tick, value) = track.Pitch.Data[pi]; var at = $"{path}.pitch[{pi}]";
                var convertedTick = tick / multiplier * multiplier;
                Require(double.IsFinite(convertedTick) && convertedTick > long.MinValue && convertedTick < long.MaxValue,
                    at, "Pitch position cannot be represented on the S5P tick axis.");
                Require(value is null || double.IsFinite(value.Value * 100), at, "Pitch must be finite in cents.");
            }
        }
    }
}
