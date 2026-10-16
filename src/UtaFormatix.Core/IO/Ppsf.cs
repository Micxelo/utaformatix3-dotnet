using System.IO.Compression;
using System.Text.Json;
using UtaFormatix.Core.Exceptions;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;

namespace UtaFormatix.Core.IO;

public static class Ppsf
{
    private const double BpmRate = 10000.0;
    private const string JsonPath = "ppsf.json";

    public static Project Parse(string filePath, ImportParams? importParams = null)
    {
        var projectName = Path.GetFileNameWithoutExtension(filePath);
        var warnings = new List<ImportWarning>();

        var jsonText = ReadJsonFromZip(filePath);
        using var doc = JsonDocument.Parse(jsonText, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;

        var ppsfObj = root.GetProperty("ppsf");
        var projectObj = ppsfObj.GetProperty("project");

        var name = projectObj.TryGetProperty("name", out var nameProp)
                   && nameProp.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(nameProp.GetString())
            ? nameProp.GetString()!
            : projectName;

        var timeSignatures = ParseTimeSignatures(projectObj.GetProperty("meter"), warnings);
        var tempos = ParseTempos(projectObj.GetProperty("tempo"), warnings);

        var tracks = new List<Track>();
        if (projectObj.TryGetProperty("dvl_track", out var tracksProp) && tracksProp.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var trackElem in tracksProp.EnumerateArray())
            {
                tracks.Add(ParseTrack(index, trackElem, importParams?.DefaultLyric ?? Constants.DefaultLyric));
                index++;
            }
        }

        return new Project(
            Format: Format.Ppsf,
            Name: name,
            Tracks: tracks,
            TimeSignatures: timeSignatures,
            Tempos: tempos,
            MeasurePrefix: 0,
            ImportWarnings: warnings);
    }

    private static string ReadJsonFromZip(string filePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            var entry = archive.GetEntry(JsonPath)
                        ?? throw new CannotReadFileException();
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (InvalidDataException)
        {
            throw new UnsupportedLegacyPpsfException();
        }
    }

    private static List<TimeSignature> ParseTimeSignatures(JsonElement meterObj, List<ImportWarning> warnings)
    {
        var constObj = meterObj.GetProperty("const");
        var constNume = constObj.GetProperty("nume").GetInt32();
        var constDenomi = constObj.GetProperty("denomi").GetInt32();

        var useSequence = meterObj.GetProperty("use_sequence").GetBoolean();

        if (!useSequence)
        {
            return [new TimeSignature(0, constNume, constDenomi)];
        }

        var sequence = new List<TimeSignature>();
        if (meterObj.TryGetProperty("sequence", out var seqProp) && seqProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var ev in seqProp.EnumerateArray())
            {
                sequence.Add(new TimeSignature(
                    ev.GetProperty("measure").GetInt32(),
                    ev.GetProperty("nume").GetInt32(),
                    ev.GetProperty("denomi").GetInt32()));
            }
        }

        if (sequence.Count == 0)
        {
            return [new TimeSignature(0, constNume, constDenomi)];
        }

        if (!sequence.Any(ts => ts.MeasurePosition == 0))
        {
            sequence.Insert(0, new TimeSignature(0, constNume, constDenomi));
        }

        return sequence;
    }

    private static List<Tempo> ParseTempos(JsonElement tempoObj, List<ImportWarning> warnings)
    {
        var constValue = tempoObj.GetProperty("const").GetInt32();
        var firstBpm = constValue / BpmRate;

        var useSequence = tempoObj.GetProperty("use_sequence").GetBoolean();

        if (!useSequence)
        {
            return [new Tempo(0, firstBpm)];
        }

        var sequence = new List<Tempo>();
        if (tempoObj.TryGetProperty("sequence", out var seqProp) && seqProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var ev in seqProp.EnumerateArray())
            {
                sequence.Add(new Tempo(
                    ev.GetProperty("tick").GetInt64(),
                    ev.GetProperty("value").GetInt32() / BpmRate));
            }
        }

        if (sequence.Count == 0)
        {
            return [new Tempo(0, firstBpm)];
        }

        if (!sequence.Any(t => t.TickPosition == 0))
        {
            sequence.Insert(0, new Tempo(0, firstBpm));
        }

        return sequence;
    }

    private static Track ParseTrack(int index, JsonElement trackElem, string defaultLyric)
    {
        var name = trackElem.TryGetProperty("name", out var nameProp)
                   && nameProp.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(nameProp.GetString())
            ? nameProp.GetString()!
            : $"Track {index + 1}";

        var notes = new List<Note>();
        if (trackElem.TryGetProperty("events", out var eventsProp) && eventsProp.ValueKind == JsonValueKind.Array)
        {
            var noteId = 0;
            foreach (var ev in eventsProp.EnumerateArray())
            {
                if (ev.TryGetProperty("enabled", out var enabledProp)
                    && enabledProp.ValueKind == JsonValueKind.False)
                    continue;

                var pos = ev.GetProperty("pos").GetInt64();
                var length = ev.GetProperty("length").GetInt64();
                var key = ev.GetProperty("note_number").GetInt32();

                string lyric;
                if (ev.TryGetProperty("lyric", out var lyricProp)
                    && lyricProp.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(lyricProp.GetString()))
                {
                    lyric = lyricProp.GetString()!;
                }
                else
                {
                    lyric = defaultLyric;
                }

                notes.Add(new Note(noteId, key, lyric, pos, pos + length));
                noteId++;
            }
        }

        return new Track(index, name, notes).ValidateNotes();
    }
}
