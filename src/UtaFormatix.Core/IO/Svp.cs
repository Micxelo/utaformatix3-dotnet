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
        var text = File.ReadAllText(filePath);

        var rawProjects = text.Split('\0')
            .Select(s => s.Trim('\0'))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var projects = rawProjects.Select(json => JsonNode.Parse(json)!.AsObject()).ToList();
        var project = projects.MaxBy(p => p["version"]?.GetValue<int>() ?? 0)
            ?? throw new InvalidDataException("No valid SVP project found");

        var warnings = new List<ImportWarning>();

        var timeSignatures = ParseTimeSignatures(project, warnings);
        var tempos = ParseTempos(project, warnings);
        var tracks = ParseTracks(project, tempos, parameters);

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
            .Where(m => m is not null)
            .Select(m => new TimeSignature(
                MeasurePosition: m!["index"]?.GetValue<int>() ?? 0,
                Numerator: m["numerator"]?.GetValue<int>() ?? 4,
                Denominator: m["denominator"]?.GetValue<int>() ?? 4))
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
            .Where(t => t is not null)
            .Select(t => new Tempo(
                TickPosition: (long)((t!["position"]?.GetValue<double>() ?? 0) / TickRate),
                Bpm: t["bpm"]?.GetValue<double>() ?? Constants.DefaultBpm))
            .ToList();
    }

    private static List<Track> ParseTracks(JsonObject project, List<Tempo> tempos, ImportParams parameters)
    {
        var trackArray = project["tracks"]?.AsArray() ?? [];
        var library = project["library"]?.AsArray() ?? [];

        var sortedTracks = trackArray
            .Where(t => t is not null)
            .Select(t => t!.AsObject())
            .OrderBy(t => t["dispOrder"]?.GetValue<int>() ?? 0)
            .ToList();

        return sortedTracks.Select((trackNode, index) =>
            ParseTrack(trackNode, library, tempos, index, parameters)).ToList();
    }

    private static Track ParseTrack(JsonObject trackNode, JsonArray library, List<Tempo> tempos,
        int index, ImportParams parameters)
    {
        var trackName = trackNode["name"]?.GetValue<string>() ?? $"Track {index + 1}";
        var notes = ParseNotes(trackNode, library, parameters.DefaultLyric)
            .Where(n => n.TickOn >= 0)
            .ToList();

        PitchModel? pitch = null;
        if (!parameters.SimpleImport)
            pitch = ParsePitch(trackNode, library, tempos);

        return new Track(index, trackName, notes, pitch).ValidateNotes();
    }

    private static List<Note> ParseNotes(JsonObject trackNode, JsonArray library, string defaultLyric)
    {
        var notes = new List<Note>();
        var noteId = 0;

        var mainGroup = trackNode["mainGroup"]?.AsObject();
        var mainRef = trackNode["mainRef"]?.AsObject();
        if (mainGroup is not null && mainRef is not null)
            notes.AddRange(ParseNotesFromGroup(mainRef, mainGroup, defaultLyric, noteId));
        noteId = notes.Count;

        var groups = trackNode["groups"]?.AsArray();
        if (groups is not null)
        {
            foreach (var refNode in groups)
            {
                if (refNode is null) continue;
                var refObj = refNode.AsObject();
                var groupId = refObj["groupID"]?.GetValue<string>();
                if (groupId is null) continue;

                var group = library
                    .Where(g => g is not null)
                    .Select(g => g!.AsObject())
                    .FirstOrDefault(g => g["uuid"]?.GetValue<string>() == groupId);

                if (group is not null)
                {
                    var extraNotes = ParseNotesFromGroup(refObj, group, defaultLyric, noteId);
                    notes.AddRange(extraNotes);
                    noteId = notes.Count;
                }
            }
        }

        return notes;
    }

    private static List<Note> ParseNotesFromGroup(JsonObject refNode, JsonObject group, string defaultLyric, int startId)
    {
        var blickOffset = refNode["blickOffset"]?.GetValue<long>() ?? 0L;
        var pitchOffset = refNode["pitchOffset"]?.GetValue<int>() ?? 0;
        var noteArray = group["notes"]?.AsArray() ?? [];

        return noteArray
            .Where(n => n is not null)
            .Select((n, i) =>
            {
                var onset = n!["onset"]?.GetValue<long>() ?? 0L;
                var duration = n["duration"]?.GetValue<long>() ?? 0L;
                var key = n["pitch"]?.GetValue<int>() ?? 60;
                var lyric = n["lyrics"]?.GetValue<string>();
                var phoneme = n["phonemes"]?.GetValue<string>();

                var tickOn = (onset + blickOffset) / TickRate;

                return new Note(
                    Id: startId + i,
                    Key: key + pitchOffset,
                    Lyric: string.IsNullOrWhiteSpace(lyric) ? defaultLyric : lyric,
                    TickOn: tickOn,
                    TickOff: tickOn + duration / TickRate,
                    Phoneme: phoneme);
            })
            .ToList();
    }

    private static PitchModel? ParsePitch(JsonObject trackNode, JsonArray library, List<Tempo> tempos)
    {
        var mainPitch = new List<(long Tick, double Value)>();
        var extraPitch = new List<(long Tick, double Value)>();

        var mainGroup = trackNode["mainGroup"]?.AsObject();
        var mainRef = trackNode["mainRef"]?.AsObject();
        if (mainGroup is not null && mainRef is not null)
            mainPitch = ParsePitchFromGroup(mainRef, mainGroup, tempos);

        var groups = trackNode["groups"]?.AsArray();
        if (groups is not null)
        {
            foreach (var refNode in groups)
            {
                if (refNode is null) continue;
                var refObj = refNode.AsObject();
                var groupId = refObj["groupID"]?.GetValue<string>();
                if (groupId is null) continue;

                var group = library
                    .Where(g => g is not null)
                    .Select(g => g!.AsObject())
                    .FirstOrDefault(g => g["uuid"]?.GetValue<string>() == groupId);

                if (group is not null)
                    extraPitch.AddRange(ParsePitchFromGroup(refObj, group, tempos));
            }
        }

        var all = mainPitch.Concat(extraPitch).OrderBy(p => p.Tick).ToList();
        return all.Count > 0 ? new PitchModel(all.Select(p => (p.Tick, (double?)p.Value)).ToList(), IsAbsolute: false) : null;
    }

    private static List<(long Tick, double Value)> ParsePitchFromGroup(JsonObject refNode, JsonObject group, List<Tempo> tempos)
    {
        var blickOffset = refNode["blickOffset"]?.GetValue<long>() ?? 0L;

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
        for (var i = 0; i + 1 < pointsArray.Count; i += 2)
        {
            var rawTick = pointsArray[i]?.GetValue<double>();
            var centValue = pointsArray[i + 1]?.GetValue<double>();
            if (rawTick is null || centValue is null) continue;
            var tick = (long)Math.Round((rawTick.Value + blickOffset) / TickRate);
            var value = centValue.Value / 100.0;
            result.Add((tick, value));
        }
        return result;
    }

    private static List<(long Tick, double Value)> ParseVibratoEnvPoints(JsonArray? pointsArray, long blickOffset)
    {
        if (pointsArray is null) return [];

        var result = new List<(long Tick, double Value)>();
        for (var i = 0; i + 1 < pointsArray.Count; i += 2)
        {
            var rawTick = pointsArray[i]?.GetValue<double>();
            var value = pointsArray[i + 1]?.GetValue<double>();
            if (rawTick is null || value is null) continue;
            var tick = (long)Math.Round((rawTick.Value + blickOffset) / TickRate);
            result.Add((tick, value.Value));
        }
        return result;
    }

    private static List<SvpNoteWithVibrato> ParseNotesWithVibrato(JsonArray? notesArray, JsonObject refNode)
    {
        if (notesArray is null) return [];

        var blickOffset = refNode["blickOffset"]?.GetValue<long>() ?? 0L;

        return notesArray
            .Where(n => n is not null)
            .Select(n =>
            {
                var onset = n!["onset"]?.GetValue<long>() ?? 0L;
                var duration = n["duration"]?.GetValue<long>() ?? 0L;
                var attrs = n["attributes"]?.AsObject();

                return new SvpNoteWithVibrato(
                    NoteStartTick: (onset + blickOffset) / TickRate,
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
        File.WriteAllBytes(filePath, data);
    }

    private static List<string> GenerateContents(Project project, List<FeatureConfig> features)
    {
        var maxTrackCount = features
            .OfType<FeatureConfig.SplitProjectConfig>()
            .FirstOrDefault()?.MaxTrackCount ?? int.MaxValue;

        var trackChunks = project.Tracks.Chunk(maxTrackCount);
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
                newTracks.Add(GenerateTrack(track, emptyTrack, features));
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
            array.Add(meter);
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
                ["position"] = tempo.TickPosition * TickRate,
                ["bpm"] = tempo.Bpm,
            };
            array.Add(t);
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
                ["onset"] = note.TickOn * TickRate,
                ["duration"] = note.Length * TickRate,
                ["lyrics"] = note.Lyric,
                ["phonemes"] = note.Phoneme ?? "",
                ["pitch"] = note.Key,
                ["attributes"] = new JsonObject(),
            };
            notesArray.Add(noteObj);
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
            .Select(p => ((double)(p.Tick * TickRate), p.Value * 100.0))
            .ToList();

        var pointsArray = new JsonArray();
        foreach (var (tick, value) in svpData)
        {
            pointsArray.Add(JsonNode.Parse(tick.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            pointsArray.Add(JsonNode.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return new JsonObject
        {
            ["mode"] = "cosine",
            ["points"] = pointsArray,
        };
    }
}
