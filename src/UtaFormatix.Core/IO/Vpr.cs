using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;
using UtaFormatix.Core.Process.Pitch;

namespace UtaFormatix.Core.IO;

public static class Vpr
{
    private const string JsonEntryPath1 = "Project/sequence.json";
    private const string JsonEntryPath2 = "Project\\sequence.json";
    private const double BpmRate = 100.0;
    private const int PitchMaxValue = 8191;
    private const int DefaultPitchBendSensitivity = 2;

    private static readonly string TemplateJson = LoadTemplate();

    private static string LoadTemplate()
    {
        var assembly = typeof(Vpr).Assembly;
        using var stream = assembly.GetManifestResourceStream("UtaFormatix.Core.Resources.template.vprjson");
        if (stream == null)
            throw new InvalidOperationException("VPR template not found in embedded resources");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static Project Parse(string filePath, ImportParams importParams)
    {
        using var fileStream = File.OpenRead(filePath);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);

        var jsonEntry = archive.GetEntry(JsonEntryPath1)
            ?? archive.GetEntry(JsonEntryPath2)
            ?? throw new InvalidDataException("VPR file does not contain Project/sequence.json");

        using var entryStream = jsonEntry.Open();
        using var reader = new StreamReader(entryStream);
        var jsonText = reader.ReadToEnd();

        var root = JsonNode.Parse(jsonText) ?? throw new InvalidDataException("Invalid VPR JSON");

        var projectName = root["title"]?.GetValue<string>() ?? "Untitled";

        var masterTrack = root["masterTrack"];
        var tempos = ParseTempos(masterTrack);
        var timeSignatures = ParseTimeSignatures(masterTrack);

        var tracksNode = root["tracks"]?.AsArray();
        if (tracksNode == null)
            throw new InvalidDataException("VPR file does not contain tracks");

        var tracks = new List<Track>();
        var trackIndex = 0;
        foreach (var trackNode in tracksNode)
        {
            if (trackNode == null) continue;
            var track = ParseTrack(trackNode, trackIndex++, importParams);
            tracks.Add(track);
        }

        var warnings = new List<ImportWarning>();
        if (tempos.Count == 0)
            warnings.Add(new ImportWarning.TempoNotFound());
        if (timeSignatures.Count == 0)
            warnings.Add(new ImportWarning.TimeSignatureNotFound());

        return new Project(
            Format: Format.Vpr,
            Name: projectName,
            Tracks: tracks,
            TimeSignatures: timeSignatures.Count > 0 ? timeSignatures : [new TimeSignature(0, 4, 4)],
            Tempos: tempos.Count > 0 ? tempos : [new Tempo(0, 120)],
            MeasurePrefix: 1,
            ImportWarnings: warnings);
    }

    private static List<Tempo> ParseTempos(JsonNode? masterTrack)
    {
        var tempos = new List<Tempo>();
        var tempoNode = masterTrack?["tempo"];
        var eventsNode = tempoNode?["events"]?.AsArray();

        if (eventsNode == null) return tempos;

        foreach (var eventNode in eventsNode)
        {
            if (eventNode == null) continue;
            var pos = eventNode["pos"]?.GetValue<long>() ?? 0;
            var value = eventNode["value"]?.GetValue<int>() ?? 12000;
            var bpm = value / BpmRate;
            tempos.Add(new Tempo(pos, bpm));
        }

        return tempos;
    }

    private static List<TimeSignature> ParseTimeSignatures(JsonNode? masterTrack)
    {
        var timeSignatures = new List<TimeSignature>();
        var timeSigNode = masterTrack?["timeSig"];
        var eventsNode = timeSigNode?["events"]?.AsArray();

        if (eventsNode == null) return timeSignatures;

        foreach (var eventNode in eventsNode)
        {
            if (eventNode == null) continue;
            var bar = eventNode["bar"]?.GetValue<int>() ?? 0;
            var numer = eventNode["numer"]?.GetValue<int>() ?? 4;
            var denom = eventNode["denom"]?.GetValue<int>() ?? 4;
            timeSignatures.Add(new TimeSignature(bar, numer, denom));
        }

        return timeSignatures;
    }

    private static Track ParseTrack(JsonNode trackNode, int trackId, ImportParams importParams)
    {
        var name = trackNode["name"]?.GetValue<string>() ?? "Untitled";
        var partsNode = trackNode["parts"]?.AsArray();

        var notes = new List<Note>();
        var pitchDataList = new List<VocaloidPartPitchData>();

        if (partsNode != null)
        {
            foreach (var partNode in partsNode)
            {
                if (partNode == null) continue;
                var partPos = partNode["pos"]?.GetValue<long>() ?? 0;

                var notesNode = partNode["notes"]?.AsArray();
                if (notesNode != null)
                {
                    foreach (var noteNode in notesNode)
                    {
                        if (noteNode == null) continue;
                        var note = ParseNote(noteNode, partPos, notes.Count, importParams);
                        notes.Add(note);
                    }
                }

                var pitchData = ParsePitchData(partNode, partPos);
                if (pitchData != null)
                {
                    pitchDataList.Add(pitchData);
                }
            }
        }

        var pitch = pitchDataList.Count > 0
            ? PitchFromVocaloidParts(pitchDataList)
            : new Pitch();

        return new Track(trackId, name, notes, pitch);
    }

    private static Note ParseNote(JsonNode noteNode, long partPos, int id, ImportParams importParams)
    {
        var pos = noteNode["pos"]?.GetValue<long>() ?? 0;
        var duration = noteNode["duration"]?.GetValue<long>() ?? 480;
        var number = noteNode["number"]?.GetValue<int>() ?? 60;
        var lyric = noteNode["lyric"]?.GetValue<string>();
        var phoneme = noteNode["phoneme"]?.GetValue<string>();

        lyric = string.IsNullOrWhiteSpace(lyric) ? importParams.DefaultLyric : lyric;
        phoneme = string.IsNullOrWhiteSpace(phoneme) ? "a" : phoneme;

        return new Note(
            Id: id,
            Key: number,
            Lyric: lyric,
            TickOn: partPos + pos,
            TickOff: partPos + pos + duration,
            Phoneme: phoneme);
    }

    private static VocaloidPartPitchData? ParsePitchData(JsonNode partNode, long partPos)
    {
        var controllersNode = partNode["controllers"]?.AsArray();
        if (controllersNode == null) return null;

        List<VocaloidPartPitchData.Event>? pitEvents = null;
        List<VocaloidPartPitchData.Event>? pbsEvents = null;

        foreach (var controllerNode in controllersNode)
        {
            if (controllerNode == null) continue;
            var controllerName = controllerNode["name"]?.GetValue<string>();

            if (controllerName == "pitchBend")
            {
                pitEvents = ParseControllerEvents(controllerNode);
            }
            else if (controllerName == "pitchBendSens")
            {
                pbsEvents = ParseControllerEvents(controllerNode);
            }
        }

        if (pitEvents == null || pitEvents.Count == 0) return null;

        if (pbsEvents == null || pbsEvents.Count == 0)
        {
            pbsEvents = [new VocaloidPartPitchData.Event(0, DefaultPitchBendSensitivity)];
        }

        return new VocaloidPartPitchData(partPos, pitEvents, pbsEvents);
    }

    private static List<VocaloidPartPitchData.Event> ParseControllerEvents(JsonNode controllerNode)
    {
        var events = new List<VocaloidPartPitchData.Event>();
        var eventsNode = controllerNode["events"]?.AsArray();

        if (eventsNode == null) return events;

        foreach (var eventNode in eventsNode)
        {
            if (eventNode == null) continue;
            var pos = eventNode["pos"]?.GetValue<long>() ?? 0;
            var value = (int)(eventNode["value"]?.GetValue<long>() ?? 0);
            events.Add(new VocaloidPartPitchData.Event(pos, value));
        }

        return events;
    }

    private static Pitch PitchFromVocaloidParts(List<VocaloidPartPitchData> parts)
    {
        var allData = new SortedDictionary<long, double>();

        foreach (var part in parts)
        {
            var pbs = part.Pbs.OrderBy(e => e.Pos).ToList();
            var pit = part.Pit.OrderBy(e => e.Pos).ToList();

            var currentPbsValue = DefaultPitchBendSensitivity;
            var pbsIndex = 0;

            foreach (var pitEvent in pit)
            {
                while (pbsIndex < pbs.Count && pbs[pbsIndex].Pos <= pitEvent.Pos)
                {
                    currentPbsValue = (int)pbs[pbsIndex].Value;
                    pbsIndex++;
                }

                var absolutePos = part.StartPos + pitEvent.Pos;
                var pitchValue = pitEvent.Value * currentPbsValue / (double)PitchMaxValue;

                allData[absolutePos] = pitchValue;
            }
        }

        if (allData.Count == 0) return new Pitch();

        var data = allData.Select(kv => (Tick: kv.Key, Value: (double?)kv.Value)).ToList();
        return new Pitch(data, false);
    }

    public static void GenerateFile(Project project, string filePath, IEnumerable<FeatureConfig>? features = null)
    {
        var featureList = features?.ToList() ?? [];
        var jsonText = GenerateJson(project, featureList);

        using var fileStream = File.Create(filePath);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(JsonEntryPath1);

        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream);
        writer.Write(jsonText);
    }

    private static string GenerateJson(Project project, List<FeatureConfig> features)
    {
        var root = JsonNode.Parse(TemplateJson) ?? throw new InvalidOperationException("Invalid VPR template");

        root["title"] = project.Name;

        var masterTrack = root["masterTrack"];
        UpdateTempos(masterTrack, project.Tempos);
        UpdateTimeSignatures(masterTrack, project.TimeSignatures);

        var tracksNode = root["tracks"]?.AsArray();
        long endTick = 0;
        if (tracksNode != null && tracksNode.Count > 0)
        {
            var templateTrack = tracksNode[0];
            tracksNode.Clear();

            foreach (var track in project.Tracks)
            {
                var trackNode = GenerateTrack(track, templateTrack, features);
                tracksNode.Add(trackNode);

                var partDuration = trackNode["parts"]?.AsArray()?[0]?["duration"]?.GetValue<long>() ?? 0;
                endTick = Math.Max(endTick, partDuration);
            }
        }

        endTick = Math.Max(endTick, project.Tempos.Count > 0 ? project.Tempos.Max(t => t.TickPosition) : 0);
        endTick = Math.Max(endTick, CalculateTimeSignatureEndTick(project.TimeSignatures));

        var loopNode = masterTrack?["loop"];
        if (loopNode != null)
        {
            loopNode["end"] = endTick;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        return root.ToJsonString(options);
    }

    private static long CalculateTimeSignatureEndTick(List<TimeSignature> timeSignatures)
    {
        if (timeSignatures.Count == 0) return 0;

        long tick = 0;
        var lastTimeSig = timeSignatures[0];
        var lastMeasure = lastTimeSig.MeasurePosition;

        for (int i = 1; i <= timeSignatures.Count; i++)
        {
            var currentMeasure = i < timeSignatures.Count
                ? timeSignatures[i].MeasurePosition
                : lastMeasure + 4;

            var measureCount = currentMeasure - lastMeasure;
            var ticksPerMeasure = Constants.TicksInBeat * 4 * lastTimeSig.Numerator / lastTimeSig.Denominator;
            tick += measureCount * ticksPerMeasure;

            if (i < timeSignatures.Count)
            {
                lastTimeSig = timeSignatures[i];
                lastMeasure = currentMeasure;
            }
        }

        return tick;
    }

    private static void UpdateTempos(JsonNode? masterTrack, List<Tempo> tempos)
    {
        var tempoNode = masterTrack?["tempo"];
        var eventsNode = tempoNode?["events"]?.AsArray();

        if (eventsNode == null) return;

        eventsNode.Clear();
        foreach (var tempo in tempos)
        {
            var eventNode = new JsonObject
            {
                ["pos"] = tempo.TickPosition,
                ["value"] = (int)(tempo.Bpm * BpmRate)
            };
            eventsNode.Add(eventNode);
        }
    }

    private static void UpdateTimeSignatures(JsonNode? masterTrack, List<TimeSignature> timeSignatures)
    {
        var timeSigNode = masterTrack?["timeSig"];
        var eventsNode = timeSigNode?["events"]?.AsArray();

        if (eventsNode == null) return;

        eventsNode.Clear();
        foreach (var timeSig in timeSignatures)
        {
            var eventNode = new JsonObject
            {
                ["bar"] = timeSig.MeasurePosition,
                ["numer"] = timeSig.Numerator,
                ["denom"] = timeSig.Denominator
            };
            eventsNode.Add(eventNode);
        }
    }

    private static JsonNode GenerateTrack(Track track, JsonNode? templateTrack, List<FeatureConfig> features)
    {
        var trackNode = templateTrack?.DeepClone() ?? new JsonObject();
        trackNode["name"] = track.Name;

        var partsNode = trackNode["parts"]?.AsArray();
        if (partsNode != null && partsNode.Count > 0)
        {
            var templatePart = partsNode[0];
            partsNode.Clear();

            var partNode = GeneratePart(track, templatePart, features);
            partsNode.Add(partNode);
        }

        return trackNode;
    }

    private static JsonNode GeneratePart(Track track, JsonNode? templatePart, List<FeatureConfig> features)
    {
        var partNode = templatePart?.DeepClone() ?? new JsonObject();

        var minTick = track.Notes.Count > 0 ? track.Notes.Min(n => n.TickOn) : 0;
        var maxTick = track.Notes.Count > 0 ? track.Notes.Max(n => n.TickOff) : 1920;
        partNode["pos"] = minTick;
        partNode["duration"] = maxTick;

        var notesNode = partNode["notes"]?.AsArray();
        if (notesNode != null)
        {
            notesNode.Clear();
            foreach (var note in track.Notes)
            {
                var noteNode = GenerateNote(note, minTick);
                notesNode.Add(noteNode);
            }
        }

        var convertPitch = features.Any(f => f is FeatureConfig.ConvertPitchConfig) && track.Pitch?.Data.Count > 0;
        if (convertPitch && track.Pitch != null)
        {
            var pitchData = track.Pitch.GenerateForVocaloid([.. track.Notes]);
            if (pitchData != null)
            {
                GeneratePitchControllers(partNode, pitchData);
            }
        }

        return partNode;
    }

    private static JsonNode GenerateNote(Note note, long partPos)
    {
        return new JsonObject
        {
            ["lyric"] = note.Lyric,
            ["phoneme"] = note.Phoneme,
            ["isProtected"] = !string.IsNullOrEmpty(note.Phoneme),
            ["pos"] = note.TickOn - partPos,
            ["duration"] = note.TickOff - note.TickOn,
            ["number"] = note.Key,
            ["velocity"] = 64,
            ["exp"] = new JsonObject { ["opening"] = 127 },
            ["singingSkill"] = new JsonObject
            {
                ["duration"] = 158,
                ["weight"] = new JsonObject { ["pre"] = 64, ["post"] = 64 }
            },
            ["vibrato"] = new JsonObject { ["type"] = 0, ["duration"] = 0 }
        };
    }

    private static void GeneratePitchControllers(JsonNode partNode, VocaloidPartPitchData pitchData)
    {
        var controllersNode = partNode["controllers"]?.AsArray();
        if (controllersNode == null)
        {
            controllersNode = new JsonArray();
            partNode["controllers"] = controllersNode;
        }

        controllersNode.Clear();

        var pitController = new JsonObject
        {
            ["name"] = "pitchBend",
            ["events"] = GenerateControllerEventsArray(pitchData.Pit)
        };
        controllersNode.Add(pitController);

        var pbsController = new JsonObject
        {
            ["name"] = "pitchBendSens",
            ["events"] = GenerateControllerEventsArray(pitchData.Pbs)
        };
        controllersNode.Add(pbsController);
    }

    private static JsonArray GenerateControllerEventsArray(List<VocaloidPartPitchData.Event> events)
    {
        var array = new JsonArray();
        foreach (var evt in events)
        {
            var eventNode = new JsonObject
            {
                ["pos"] = evt.Pos,
                ["value"] = evt.Value
            };
            array.Add(eventNode);
        }
        return array;
    }
}
