using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;
using UtaFormatix.Core.Process.Pitch;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.IO;

public static class Svp
{
    private const long TickRate = 1470000L;

    private static readonly string TemplateJson = LoadTemplate();

    private static string LoadTemplate()
    {
        var assembly = typeof(Svp).Assembly;
        using var stream = assembly.GetManifestResourceStream("UtaFormatix.Core.Resources.template.svp");
        if (stream == null)
            throw new InvalidOperationException("SVP template not found in embedded resources");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static Project Parse(string filePath, ImportParams? importParams = null)
    {
        var parameters = importParams ?? new ImportParams();
        var warnings = new List<ImportWarning>();
        var input = new SvpInput(parameters, warnings);
        var project = input.Read(filePath);

        var timeSignatures = ParseTimeSignatures(project, warnings);
        var tempos = ParseTempos(project, warnings);
        var tracks = ParseTracks(project, tempos, parameters, input);

        return new Project(
            Format: Format.Svp,
            Name: Path.GetFileNameWithoutExtension(filePath),
            Tracks: tracks,
            TimeSignatures: timeSignatures,
            Tempos: tempos,
            MeasurePrefix: 0,
            ImportWarnings: warnings);
    }

    private static List<TimeSignature> ParseTimeSignatures(JsonObject project, List<ImportWarning> warnings)
    {
        var meterArray = project["time"]?["meter"]?.AsArray();
        if (meterArray is null || meterArray.Count == 0)
        {
            warnings.Add(new ImportWarning.TimeSignatureNotFound());
            return [new TimeSignature(0, Constants.DefaultMeterHigh, Constants.DefaultMeterLow)];
        }

        return meterArray
            .Select(m => new TimeSignature(
                MeasurePosition: m!["index"]!.GetValue<int>(),
                Numerator: m["numerator"]!.GetValue<int>(),
                Denominator: m["denominator"]!.GetValue<int>()))
            .ToList();
    }

    private static List<Tempo> ParseTempos(JsonObject project, List<ImportWarning> warnings)
    {
        var tempoArray = project["time"]?["tempo"]?.AsArray();
        if (tempoArray is null || tempoArray.Count == 0)
        {
            warnings.Add(new ImportWarning.TempoNotFound());
            return [new Tempo(0, Constants.DefaultBpm)];
        }

        return tempoArray
            .Select(t => new Tempo(
                TickPosition: t!["position"]!.GetValue<long>() / TickRate,
                Bpm: t["bpm"]!.GetValue<double>()))
            .ToList();
    }

    private static List<Track> ParseTracks(JsonObject project, List<Tempo> tempos, ImportParams parameters, SvpInput input)
    {
        var trackArray = project["tracks"]?.AsArray() ?? [];

        var sortedTracks = trackArray
            .Select(t => t!.AsObject())
            .OrderBy(t => t["dispOrder"]?.GetValue<int>())
            .ToList();

        return sortedTracks.Select((trackNode, index) =>
            ParseTrack(trackNode, tempos, index, parameters, input)).ToList();
    }

    private static Track ParseTrack(JsonObject trackNode, List<Tempo> tempos,
        int index, ImportParams parameters, SvpInput input)
    {
        var trackName = trackNode["name"]?.GetValue<string>() ?? $"Track {index + 1}";
        var notes = ParseNotes(trackNode, parameters.DefaultLyric, input).OrderBy(n => n.TickOn).ToList();
        var validated = new List<Note>();
        var recoveredOverlap = false;
        foreach (var note in notes)
        {
            if (validated.Count > 0 && note.TickOn < validated[^1].TickOff)
            {
                var error = SvpInput.Error("SVP_OVERLAP", input.PathOf(note), "Note overlaps a previous note; lossless monophonic conversion is not possible.");
                if (!input.Recover) throw error;
                input.Warn("SVP_DROPPED_NOTE", error.FieldPath, error.Reason);
                recoveredOverlap = true;
                continue;
            }
            validated.Add(note with { Id = validated.Count });
        }

        PitchModel? pitch = null;
        if (recoveredOverlap) input.Warn("SVP_DROPPED_PITCH", input.PathOf(trackNode), "Omitted pitch after discarding overlapping notes during recovery.");
        try
        {
            if (!parameters.SimpleImport && !input.SkipPitch(trackNode) && !recoveredOverlap)
                pitch = ParsePitch(trackNode, tempos, input);
            if (pitch?.Data.Any(p => p.Value is { } value && !double.IsFinite(value)) == true)
                throw SvpInput.Error("SVP_RANGE", input.PathOf(trackNode), "Pitch processing produced a non-finite value.");
        }
        catch (Exception ex) when (ex is SvpInputException or ArithmeticException or ArgumentException or InvalidOperationException)
        {
            var error = ex as SvpInputException ?? SvpInput.Error("SVP_PITCH", input.PathOf(trackNode), ex.Message);
            if (!input.Recover) throw error;
            input.Warn("SVP_DROPPED_PITCH", error.FieldPath, error.Reason);
            pitch = null;
        }

        return new Track(index, trackName, validated, pitch);
    }

    private static List<Note> ParseNotes(JsonObject trackNode, string defaultLyric, SvpInput input)
    {
        var notes = new List<Note>();
        var noteId = 0;

        var mainGroup = trackNode["mainGroup"]?.AsObject();
        var mainRef = trackNode["mainRef"]?.AsObject();
        if (mainGroup is not null && mainRef is not null)
            notes.AddRange(ParseNotesFromGroup(mainRef, input.GroupFor(mainRef), defaultLyric, noteId, input));
        noteId = notes.Count;

        var groups = trackNode["groups"]?.AsArray();
        if (groups is not null)
        {
            foreach (var refNode in groups)
            {
                var refObj = refNode!.AsObject();
                var group = input.GroupFor(refObj);

                var extraNotes = ParseNotesFromGroup(refObj, group, defaultLyric, noteId, input);
                notes.AddRange(extraNotes);
                noteId = notes.Count;
            }
        }

        return notes;
    }

    private static List<Note> ParseNotesFromGroup(JsonObject refNode, JsonObject group, string defaultLyric, int startId, SvpInput input)
    {
        var blickOffset = refNode["blickOffset"]!.GetValue<long>();
        var pitchOffset = refNode["pitchOffset"]!.GetValue<int>();
        var noteArray = group["notes"]?.AsArray() ?? [];

        return noteArray
            .Select((n, i) =>
            {
                var onset = n!["onset"]!.GetValue<long>();
                var duration = n["duration"]!.GetValue<long>();
                var key = n["pitch"]!.GetValue<int>();
                var lyric = n["lyrics"]?.GetValue<string>();
                var phoneme = n["phonemes"]?.GetValue<string>();

                var tickOn = checked(onset + blickOffset) / TickRate;

                var note = new Note(
                    Id: startId + i,
                    Key: checked(key + pitchOffset),
                    Lyric: LyricWhitespace.IsNullOrBlank(lyric) ? defaultLyric : lyric!,
                    TickOn: tickOn,
                    TickOff: checked(tickOn + duration / TickRate),
                    Phoneme: phoneme);
                input.RecordNote(note, n);
                return note;
            })
            .ToList();
    }

    private static PitchModel? ParsePitch(JsonObject trackNode, List<Tempo> tempos, SvpInput input)
    {
        var mainPitch = new List<(long Tick, double Value)>();
        var extraPitch = new List<(long Tick, double Value)>();

        var mainGroup = trackNode["mainGroup"]?.AsObject();
        var mainRef = trackNode["mainRef"]?.AsObject();
        if (mainGroup is not null && mainRef is not null)
            mainPitch = ParsePitchFromGroup(mainRef, input.GroupFor(mainRef), tempos);

        var groups = trackNode["groups"]?.AsArray();
        if (groups is not null)
        {
            foreach (var refNode in groups)
            {
                var refObj = refNode!.AsObject();
                var group = input.GroupFor(refObj);

                extraPitch.AddRange(ParsePitchFromGroup(refObj, group, tempos));
            }
        }

        var all = mainPitch.Concat(extraPitch).OrderBy(p => p.Tick).ToList();
        return all.Count > 0 ? new PitchModel(all.Select(p => (p.Tick, (double?)p.Value)).ToList(), IsAbsolute: false) : null;
    }

    private static List<(long Tick, double Value)> ParsePitchFromGroup(JsonObject refNode, JsonObject group, List<Tempo> tempos)
    {
        var blickOffset = refNode["blickOffset"]!.GetValue<long>();

        var vibratoDefaultParameters = ParseVibratoDefaults(refNode["voice"]?.AsObject());

        var parameters = group["parameters"]?.AsObject();
        var pitchDelta = parameters?["pitchDelta"]?.AsObject();
        var pitchMode = pitchDelta?["mode"]?.GetValue<string>();
        var pitchPoints = ParsePitchPoints(pitchDelta?["points"]?.AsArray(), blickOffset);

        var vibratoEnv = parameters?["vibratoEnv"]?.AsObject();
        var vibratoEnvMode = vibratoEnv?["mode"]?.GetValue<string>();
        var vibratoEnvPoints = ParseVibratoEnvPoints(vibratoEnv?["points"]?.AsArray(), blickOffset);

        var notesWithVibrato = ParseNotesWithVibrato(group["notes"]?.AsArray(), refNode);

        return SynthVPitchConversion.ProcessSvpInputPitchData(
            pitchPoints, pitchMode, notesWithVibrato, tempos,
            vibratoEnvPoints, vibratoEnvMode, vibratoDefaultParameters);
    }

    private static SvpDefaultVibratoParameters? ParseVibratoDefaults(JsonObject? voice)
    {
        if (voice is null) return null;

        var hasAny = voice["tF0VbrStart"] is not null || voice["tF0VbrLeft"] is not null ||
                     voice["tF0VbrRight"] is not null || voice["dF0Vbr"] is not null ||
                     voice["fF0Vbr"] is not null;
        if (!hasAny) return null;

        return new SvpDefaultVibratoParameters(
            VibratoStart: voice["tF0VbrStart"]?.GetValue<double>(),
            EaseInLength: voice["tF0VbrLeft"]?.GetValue<double>(),
            EaseOutLength: voice["tF0VbrRight"]?.GetValue<double>(),
            Depth: voice["dF0Vbr"]?.GetValue<double>(),
            Frequency: voice["fF0Vbr"]?.GetValue<double>());
    }

    private static List<(long Tick, double Value)> ParsePitchPoints(JsonArray? pointsArray, long blickOffset)
    {
        if (pointsArray is null) return [];

        var result = new List<(long Tick, double Value)>();
        for (var i = 0; i < pointsArray.Count; i += 2)
        {
            var rawTick = pointsArray[i]!.GetValue<double>();
            var centValue = pointsArray[i + 1]!.GetValue<double>();
            var tick = PitchTickRounding.Round((rawTick + blickOffset) / TickRate);
            var value = centValue / 100.0;
            result.Add((tick, value));
        }
        return result;
    }

    private static List<(long Tick, double Value)> ParseVibratoEnvPoints(JsonArray? pointsArray, long blickOffset)
    {
        if (pointsArray is null) return [];

        var result = new List<(long Tick, double Value)>();
        for (var i = 0; i < pointsArray.Count; i += 2)
        {
            var rawTick = pointsArray[i]!.GetValue<double>();
            var value = pointsArray[i + 1]!.GetValue<double>();
            var tick = PitchTickRounding.Round((rawTick + blickOffset) / TickRate);
            result.Add((tick, value));
        }
        return result;
    }

    private static List<SvpNoteWithVibrato> ParseNotesWithVibrato(JsonArray? notesArray, JsonObject refNode)
    {
        if (notesArray is null) return [];

        var blickOffset = refNode["blickOffset"]!.GetValue<long>();

        return notesArray
            .Select(n =>
            {
                var onset = n!["onset"]!.GetValue<long>();
                var duration = n["duration"]!.GetValue<long>();
                var attrs = n["attributes"]?.AsObject();

                return new SvpNoteWithVibrato(
                    NoteStartTick: checked(onset + blickOffset) / TickRate,
                    NoteLengthTick: duration / TickRate,
                    VibratoStart: attrs?["tF0VbrStart"]?.GetValue<double>(),
                    EaseInLength: attrs?["tF0VbrLeft"]?.GetValue<double>(),
                    EaseOutLength: attrs?["tF0VbrRight"]?.GetValue<double>(),
                    Depth: attrs?["dF0Vbr"]?.GetValue<double>(),
                    Frequency: attrs?["fF0Vbr"]?.GetValue<double>(),
                    Phase: attrs?["pF0Vbr"]?.GetValue<double>());
            })
            .ToList();
    }

    public static (byte[] Data, string FileName, List<ExportNotification> Notifications) Generate(
        Project project, IEnumerable<FeatureConfig>? features = null)
    {
        ValidateForExport(project);
        var featureList = features?.ToList() ?? [];
        var jsonTexts = GenerateContents(project, featureList);

        var notifications = new List<ExportNotification>();
        if (featureList.Contains(Feature.ConvertPitch))
            notifications.Add(new ExportNotification.PitchDataExported());

        if (jsonTexts.Count == 1)
        {
            var data = System.Text.Encoding.UTF8.GetBytes(jsonTexts[0]);
            var name = Format.Svp.GetFileName(project.Name);
            return (data, name, notifications);
        }

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            for (var i = 0; i < jsonTexts.Count; i++)
            {
                var entryName = Format.Svp.GetFileName($"{project.Name}_{i + 1}");
                var entry = archive.CreateEntry(entryName);
                using var entryStream = entry.Open();
                var bytes = System.Text.Encoding.UTF8.GetBytes(jsonTexts[i]);
                entryStream.Write(bytes, 0, bytes.Length);
            }
        }

        var zipData = memoryStream.ToArray();
        var zipName = Format.Svp.GetFileName(project.Name) + ".zip";
        return (zipData, zipName, notifications);
    }

    public static void GenerateFile(Project project, string filePath, IEnumerable<FeatureConfig>? features = null)
    {
        var (data, _, _) = Generate(project, features);
        AtomicFile.Write(filePath, temporary => File.WriteAllBytes(temporary, data));
    }

    private static List<string> GenerateContents(Project project, List<FeatureConfig> features)
    {
        var maxTrackCount = features
            .OfType<FeatureConfig.SplitProjectConfig>()
            .FirstOrDefault()?.MaxTrackCount ?? int.MaxValue;
        if (maxTrackCount <= 0) throw SvpInput.Error("SVP_EXPORT", "$.splitProject", "Track limit must be positive.");

        var trackChunks = project.Tracks.Count == 0
            ? new[] { Array.Empty<Track>() }
            : project.Tracks.Chunk(maxTrackCount);
        var results = new List<string>();

        foreach (var tracks in trackChunks)
        {
            var svp = JsonNode.Parse(TemplateJson)!.AsObject();

            var time = svp["time"]!.AsObject();
            time["meter"] = BuildMeterArray(project.TimeSignatures);
            time["tempo"] = BuildTempoArray(project.Tempos);

            var emptyTrack = svp["tracks"]!.AsArray()[0]!.AsObject();
            var newTracks = new JsonArray();

            foreach (var track in tracks)
            {
                newTracks.Add((JsonNode?)GenerateTrack(track, emptyTrack, features));
            }

            svp["tracks"] = newTracks;

            var jsonText = svp.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            results.Add(jsonText);
        }

        return results;
    }

    private static JsonArray BuildMeterArray(List<TimeSignature> timeSignatures)
    {
        var array = new JsonArray();
        foreach (var ts in timeSignatures)
        {
            var meter = new JsonObject
            {
                ["index"] = ts.MeasurePosition,
                ["numerator"] = ts.Numerator,
                ["denominator"] = ts.Denominator,
            };
            array.Add((JsonNode?)meter);
        }
        return array;
    }

    private static JsonArray BuildTempoArray(List<Tempo> tempos)
    {
        var array = new JsonArray();
        foreach (var tempo in tempos)
        {
            var t = new JsonObject
            {
                ["position"] = checked(tempo.TickPosition * TickRate),
                ["bpm"] = tempo.Bpm,
            };
            array.Add((JsonNode?)t);
        }
        return array;
    }

    private static JsonObject GenerateTrack(Track track, JsonObject emptyTrack, List<FeatureConfig> features)
    {
        var uuid = Guid.NewGuid().ToString();

        var newTrack = emptyTrack.DeepClone().AsObject();
        newTrack["name"] = track.Name;
        newTrack["dispOrder"] = track.Id;

        var mainGroup = newTrack["mainGroup"]!.AsObject();
        mainGroup["uuid"] = uuid;

        var notesArray = new JsonArray();
        foreach (var note in track.Notes)
        {
            var noteObj = new JsonObject
            {
                ["onset"] = checked(note.TickOn * TickRate),
                ["duration"] = checked(checked(note.TickOff - note.TickOn) * TickRate),
                ["lyrics"] = note.Lyric,
                ["phonemes"] = note.Phoneme ?? "",
                ["pitch"] = note.Key,
                ["attributes"] = new JsonObject(),
            };
            notesArray.Add((JsonNode?)noteObj);
        }
        mainGroup["notes"] = notesArray;

        var parameters = mainGroup["parameters"]!.AsObject();
        var pitchData = GeneratePitchData(track, features);
        if (pitchData is not null)
        {
            parameters["pitchDelta"] = pitchData;
        }

        var mainRef = newTrack["mainRef"]!.AsObject();
        mainRef["groupID"] = uuid;

        return newTrack;
    }

    private static JsonObject? GeneratePitchData(Track track, List<FeatureConfig> features)
    {
        if (!features.Contains(Feature.ConvertPitch)) return null;

        var data = track.Pitch?.GetRelativeData(track.Notes);
        if (data is null) return null;

        var svpData = data
            .AppendPitchPointsForSvpOutput()
            .Select(p => ((double)checked(p.Tick * TickRate), p.Value * 100.0))
            .ToList();

        var pointsArray = new JsonArray();
        foreach (var (tick, value) in svpData)
        {
            pointsArray.Add((JsonNode?)JsonValue.Create(tick));
            pointsArray.Add((JsonNode?)JsonValue.Create(value));
        }

        return new JsonObject
        {
            ["mode"] = "cosine",
            ["points"] = pointsArray,
        };
    }

    private static void ValidateForExport(Project project)
    {
        void Require(bool valid, string path, string message)
        { if (!valid) throw SvpInput.Error("SVP_EXPORT", path, message); }
        void Text(string? value, string path)
        {
            if (value is null) return;
            try { _ = new System.Text.UTF8Encoding(false, true).GetByteCount(value); }
            catch (System.Text.EncoderFallbackException) { throw SvpInput.Error("SVP_UNICODE", path, "String contains an isolated surrogate."); }
        }
        void Position(long tick, string path)
        {
            try { _ = checked(tick * TickRate); }
            catch (OverflowException) { throw SvpInput.Error("SVP_OVERFLOW", path, "Tick cannot be represented as an Int64 SVP position."); }
        }
        Require(project.Tempos.Count > 0, "$.tempos", "At least one tempo is required.");
        Text(project.Name, "$.name");
        Require(project.TimeSignatures.Count > 0, "$.timeSignatures", "At least one time signature is required.");
        for (var i = 0; i < project.Tempos.Count; i++)
        {
            var tempo = project.Tempos[i];
            Position(tempo.TickPosition, $"$.tempos[{i}].tickPosition");
            Require(double.IsFinite(tempo.Bpm) && tempo.Bpm > 0, $"$.tempos[{i}].bpm", "BPM must be finite and positive.");
            Require(i == 0 || tempo.TickPosition >= project.Tempos[i - 1].TickPosition, "$.tempos", "Tempos must be in chronological order.");
        }
        for (var i = 0; i < project.TimeSignatures.Count; i++)
        {
            var meter = project.TimeSignatures[i];
            Require(meter.Numerator > 0 && meter.Denominator > 0, $"$.timeSignatures[{i}]", "Time signature components must be positive.");
            Require(i == 0 || meter.MeasurePosition >= project.TimeSignatures[i - 1].MeasurePosition, "$.timeSignatures", "Time signatures must be in chronological order.");
        }
        for (var ti = 0; ti < project.Tracks.Count; ti++)
        {
            var track = project.Tracks[ti];
            var path = $"$.tracks[{ti}]";
            Text(track.Name, path + ".name");
            for (var ni = 0; ni < track.Notes.Count; ni++)
            {
                var note = track.Notes[ni];
                var at = $"{path}.notes[{ni}]";
                Require(note.TickOn >= 0 && note.TickOff > note.TickOn, at, "Note must have a non-negative start and positive duration.");
                Require(ni == 0 || note.TickOn >= track.Notes[ni - 1].TickOff, at, "Notes must be ordered and must not overlap.");
                Position(note.TickOn, at + ".tickOn");
                Position(note.TickOff, at + ".tickOff");
                Position(note.Length, at + ".duration");
                Text(note.Lyric, at + ".lyric"); Text(note.Phoneme, at + ".phoneme");
            }
            if (track.Pitch is not null)
                for (var pi = 0; pi < track.Pitch.Data.Count; pi++)
                {
                    var (tick, value) = track.Pitch.Data[pi];
                    var at = $"{path}.pitch[{pi}]";
                    Position(tick, at);
                    Require(pi == 0 || tick >= track.Pitch.Data[pi - 1].Tick, at, "Pitch points must be ordered.");
                    Require(value is null || double.IsFinite(value.Value * 100), at, "Pitch must be finite in cents.");
                }
        }
    }
}
