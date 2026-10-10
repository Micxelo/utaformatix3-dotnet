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
        var text = File.ReadAllText(filePath);

        var lastIndex = text.LastIndexOf('}');
        if (lastIndex >= 0)
            text = text[..(lastIndex + 1)];

        var project = JsonNode.Parse(text)!.AsObject();
        var warnings = new List<ImportWarning>();

        var timeSignatures = ParseTimeSignatures(project, warnings);
        var tempos = ParseTempos(project, warnings);
        var tracks = ParseTracks(project, parameters);

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
            .Where(m => m is not null)
            .Select(m => new TimeSignature(
                MeasurePosition: m!["measure"]?.GetValue<int>() ?? 0,
                Numerator: m["beatPerMeasure"]?.GetValue<int>() ?? 4,
                Denominator: m["beatGranularity"]?.GetValue<int>() ?? 4))
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
            .Where(t => t is not null)
            .Select(t => new Tempo(
                TickPosition: (long)((t!["position"]?.GetValue<double>() ?? 0) / TickRate),
                Bpm: t["beatPerMinute"]?.GetValue<double>() ?? Constants.DefaultBpm))
            .ToList();
    }

    private static List<Track> ParseTracks(JsonObject project, ImportParams parameters)
    {
        var trackArray = project["tracks"]?.AsArray() ?? [];
        return trackArray
            .Where(t => t is not null)
            .Select((t, i) => ParseTrack(t!.AsObject(), i, parameters))
            .ToList();
    }

    private static Track ParseTrack(JsonObject trackNode, int index, ImportParams parameters)
    {
        var trackName = trackNode["name"]?.GetValue<string>() ?? $"Track {index + 1}";
        var notes = ParseNotes(trackNode, parameters.DefaultLyric);

        PitchModel? pitch = null;
        if (!parameters.SimpleImport)
            pitch = ParsePitch(trackNode);

        return new Track(index, trackName, notes, pitch).ValidateNotes();
    }

    private static List<Note> ParseNotes(JsonObject trackNode, string defaultLyric)
    {
        var notesArray = trackNode["notes"]?.AsArray() ?? [];
        var noteId = 0;

        return notesArray
            .Where(n => n is not null)
            .Select(n =>
            {
                var onset = n!["onset"]?.GetValue<long>() ?? 0L;
                var duration = n["duration"]?.GetValue<long>() ?? 0L;
                var key = n["pitch"]?.GetValue<int>() ?? Constants.DefaultKey;
                var lyric = n["lyric"]?.GetValue<string>();

                var tickOn = onset / TickRate;

                return new Note(
                    Id: noteId++,
                    Key: key,
                    Lyric: string.IsNullOrWhiteSpace(lyric) ? defaultLyric : lyric,
                    TickOn: tickOn,
                    TickOff: tickOn + duration / TickRate);
            })
            .ToList();
    }

    private static PitchModel? ParsePitch(JsonObject trackNode)
    {
        var parameters = trackNode["parameters"]?.AsObject();
        var pitchDelta = parameters?["pitchDelta"]?.AsArray();
        if (pitchDelta is null || pitchDelta.Count == 0)
            return null;

        var interval = parameters?["interval"]?.GetValue<long>() ?? DefaultInterval;
        var tickMultiplier = interval / (double)TickRate;

        var points = new List<(long Tick, double? Value)>();
        for (var i = 0; i + 1 < pitchDelta.Count; i += 2)
        {
            var rawTick = pitchDelta[i]?.GetValue<double>();
            var centValue = pitchDelta[i + 1]?.GetValue<double>();
            if (rawTick is null || centValue is null) continue;

            var tick = (long)Math.Round(rawTick.Value * tickMultiplier);
            var value = centValue.Value / 100.0;
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
        File.WriteAllBytes(filePath, data);
    }

    private static string GenerateContent(Project project, List<FeatureConfig> features)
    {
        var s5p = JsonNode.Parse(TemplateJson)!.AsObject();

        var meterArray = new JsonArray();
        foreach (var ts in project.TimeSignatures)
        {
            meterArray.Add(new JsonObject
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
            tempoArray.Add(new JsonObject
            {
                ["position"] = tempo.TickPosition * TickRate,
                ["beatPerMinute"] = tempo.Bpm,
            });
        }
        s5p["tempo"] = tempoArray;

        var emptyTrack = s5p["tracks"]!.AsArray()[0]!.AsObject();
        var newTracks = new JsonArray();
        foreach (var track in project.Tracks)
        {
            newTracks.Add(GenerateTrack(track, emptyTrack, features));
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
            notesArray.Add(new JsonObject
            {
                ["onset"] = note.TickOn * TickRate,
                ["duration"] = note.Length * TickRate,
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
            array.Add(JsonNode.Parse((tick / divisor).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            array.Add(JsonNode.Parse((value * 100.0).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return array;
    }
}
