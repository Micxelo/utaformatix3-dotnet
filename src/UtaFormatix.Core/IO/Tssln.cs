using System.Reflection;
using System.Text.RegularExpressions;
using UtaFormatix.Core.Exceptions;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;

namespace UtaFormatix.Core.IO;

public static partial class Tssln
{
    private const double TickRate = 2.0;

    [GeneratedRegex(@"\[.+?\]")]
    private static partial Regex PhonemePartPattern();

    public static Project Parse(string filePath, ImportParams? importParams = null)
    {
        var projectName = Path.GetFileNameWithoutExtension(filePath);
        var data = File.ReadAllBytes(filePath);
        var valueTree = ValueTreeBinary.Parse(data);

        if (valueTree.Type != "TSSolution")
            throw new IllegalFileException.IllegalTsslnFile();

        var trackTrees = valueTree.Children
            .First(c => c.Type == "Tracks")
            .Children
            .Where(c => c.Attributes["Type"].AsInt() == 0)
            .ToList();

        var (tempos, timeSignatures) = ParseMasterTrack(trackTrees.First());
        var tracks = ParseTracks(trackTrees, importParams);

        return new Project(
            Format: Format.Tssln,
            Name: projectName,
            Tracks: tracks,
            TimeSignatures: timeSignatures,
            Tempos: tempos,
            MeasurePrefix: 1,
            ImportWarnings: []);
    }

    public static void GenerateFile(Project project, string filePath, IEnumerable<FeatureConfig>? features = null)
    {
        var templateData = LoadTemplate();
        var baseTree = ValueTreeBinary.Parse(templateData);

        var tracksTree = baseTree.Children.First(c => c.Type == "Tracks");
        var baseTrack = tracksTree.Children.First(c => c.Attributes["Type"].AsInt() == 0);

        var tracks = GenerateTracks(baseTrack, project);
        tracksTree.Children = tracks;

        var result = ValueTreeBinary.Dump(baseTree);
        File.WriteAllBytes(filePath, result);
    }

    private static byte[] LoadTemplate()
    {
        var assembly = typeof(Tssln).Assembly;
        using var stream = assembly.GetManifestResourceStream("UtaFormatix.Core.Resources.template.tssln")
                           ?? throw new InvalidOperationException("Tssln template not found in embedded resources");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static ValueTree ParsePluginData(byte[] pluginData)
    {
        var tree = ValueTreeBinary.Parse(pluginData);
        if (tree.Type.StartsWith("VST"))
        {
            var actualData = new byte[pluginData.Length - 48];
            Array.Copy(pluginData, 48, actualData, 0, actualData.Length);
            return ValueTreeBinary.Parse(actualData);
        }
        return tree;
    }

    private static (List<Tempo> Tempos, List<TimeSignature> TimeSignatures) ParseMasterTrack(ValueTree trackTree)
    {
        var pluginData = trackTree.Attributes["PluginData"].AsBinary();
        var pluginDataTree = ParsePluginData(pluginData);

        if (pluginDataTree.Type != "StateInformation")
            throw new IllegalFileException.IllegalTsslnFile();

        var songTree = pluginDataTree.Children.First(c => c.Type == "Song");

        var tempoTree = songTree.Children.First(c => c.Type == "Tempo");
        var tempos = tempoTree.Children.Select(t => new Tempo(
            (long)(t.Attributes["Clock"].AsInt() / TickRate),
            t.Attributes["Tempo"].AsDouble())).ToList();

        var beatTree = songTree.Children.First(c => c.Type == "Beat");
        var timeSignatures = new List<TimeSignature>();

        var currentBeatIndex = 0;
        var currentMeasureIndex = 0;
        var beatLength = 4.0;

        foreach (var ts in beatTree.Children.OrderBy(t => t.Attributes["Clock"].AsInt()))
        {
            var numerator = ts.Attributes["Beats"].AsInt();
            var denominator = ts.Attributes["BeatType"].AsInt();
            var clock = ts.Attributes["Clock"].AsInt();
            var beatIndex = (int)Math.Floor(clock / TickRate / Constants.TicksInBeat);

            var beatNum = beatIndex - currentBeatIndex;
            if (beatNum < 0) throw new IllegalFileException.IllegalTsslnFile();

            var measureIndex = (int)(currentMeasureIndex + beatNum / beatLength);
            beatLength = (double)numerator / denominator * 4;

            currentBeatIndex = beatIndex;
            currentMeasureIndex = measureIndex;

            timeSignatures.Add(new TimeSignature(measureIndex, numerator, denominator));
        }

        return (tempos, timeSignatures);
    }

    private static List<Track> ParseTracks(List<ValueTree> trackTrees, ImportParams? importParams)
    {
        var defaultLyric = importParams?.DefaultLyric ?? Constants.DefaultLyric;

        return trackTrees.Select((trackTree, trackIndex) =>
        {
            var trackName = trackTree.Attributes["Name"].AsString();
            var pluginData = trackTree.Attributes["PluginData"].AsBinary();
            var pluginDataTree = ParsePluginData(pluginData);

            if (pluginDataTree.Type != "StateInformation")
                throw new IllegalFileException.IllegalTsslnFile();

            var songTree = pluginDataTree.Children.First(c => c.Type == "Song");
            var scoreTree = songTree.Children.First(c => c.Type == "Score");

            var notes = new List<Note>();
            var noteIndex = 0;

            foreach (var noteTree in scoreTree.Children)
            {
                if (noteTree.Type != "Note") continue;

                var pitchStep = noteTree.Attributes["PitchStep"].AsInt();
                var pitchOctave = noteTree.Attributes["PitchOctave"].AsInt();
                var rawLyric = noteTree.Attributes["Lyric"].AsString();
                var lyric = PhonemePartPattern().Replace(rawLyric, "");
                if (string.IsNullOrWhiteSpace(lyric))
                    lyric = defaultLyric;

                var phoneme = noteTree.Attributes["Phoneme"].AsString().Replace(",", "");

                var tickOn = noteTree.Attributes["Clock"].AsInt();
                var duration = noteTree.Attributes["Duration"].AsInt();
                var tickOff = tickOn + duration;

                notes.Add(new Note(
                    noteIndex,
                    pitchOctave * 12 + pitchStep + 12,
                    lyric,
                    (long)(tickOn / TickRate),
                    (long)(tickOff / TickRate),
                    Phoneme: string.IsNullOrEmpty(phoneme) ? null : phoneme));
                noteIndex++;
            }

            return new Track(trackIndex, trackName, notes);
        }).ToList();
    }

    private static List<ValueTree> GenerateTracks(ValueTree baseTrack, Project project)
    {
        return project.Tracks.Select(track =>
        {
            var trackTree = DeepClone(baseTrack);
            trackTree.Attributes["Name"] = new ValueTreeValue
                { ValueType = ValueTreeValueType.String, Value = track.Name };

            var pluginDataBytes = trackTree.Attributes["PluginData"].AsBinary();
            var pluginDataTree = ValueTreeBinary.Parse(pluginDataBytes);

            var songTree = pluginDataTree.Children.First(c => c.Type == "Song");

            var tempoTree = songTree.Children.First(c => c.Type == "Tempo");
            tempoTree.Children = GenerateTempos(project.Tempos);

            var beatTree = songTree.Children.First(c => c.Type == "Beat");
            beatTree.Children = GenerateTimeSignatures(project.TimeSignatures);

            var scoreTree = songTree.Children.First(c => c.Type == "Score");
            var baseChildren = scoreTree.Children.Select(DeepClone).ToList();
            baseChildren.AddRange(GenerateNotes(track));
            scoreTree.Children = baseChildren;

            trackTree.Attributes["PluginData"] = new ValueTreeValue
                { ValueType = ValueTreeValueType.Binary, Value = ValueTreeBinary.Dump(pluginDataTree) };

            return trackTree;
        }).ToList();
    }

    private static List<ValueTree> GenerateTempos(List<Tempo> tempos)
    {
        return tempos.Select(tempo =>
        {
            var tree = new ValueTree { Type = "Sound" };
            tree.Attributes["Clock"] = new ValueTreeValue
                { ValueType = ValueTreeValueType.Int, Value = (int)(tempo.TickPosition * TickRate) };
            tree.Attributes["Tempo"] = new ValueTreeValue
                { ValueType = ValueTreeValueType.Double, Value = tempo.Bpm };
            return tree;
        }).ToList();
    }

    private static List<ValueTree> GenerateTimeSignatures(List<TimeSignature> timeSignatures)
    {
        var result = new List<ValueTree>();
        var currentBeat = 0;
        var currentMeasure = 0;
        var beatLength = 4.0;

        foreach (var ts in timeSignatures)
        {
            var numMeasure = ts.MeasurePosition - currentMeasure;
            var numBeat = (int)(numMeasure * beatLength);
            currentBeat += numBeat;

            beatLength = (double)ts.Numerator / ts.Denominator * 4;
            currentMeasure = ts.MeasurePosition;

            var tree = new ValueTree { Type = "Time" };
            tree.Attributes["Clock"] = new ValueTreeValue
                { ValueType = ValueTreeValueType.Int, Value = (int)(currentBeat * Constants.TicksInBeat * TickRate) };
            tree.Attributes["Beats"] = new ValueTreeValue
                { ValueType = ValueTreeValueType.Int, Value = ts.Numerator };
            tree.Attributes["BeatType"] = new ValueTreeValue
                { ValueType = ValueTreeValueType.Int, Value = ts.Denominator };

            result.Add(tree);
        }

        return result;
    }

    private static List<ValueTree> GenerateNotes(Track track)
    {
        return track.Notes.Select(note =>
        {
            var tree = new ValueTree { Type = "Note" };
            tree.Attributes["PitchStep"] = new ValueTreeValue
                { ValueType = ValueTreeValueType.Int, Value = note.Key % 12 };
            tree.Attributes["PitchOctave"] = new ValueTreeValue
                { ValueType = ValueTreeValueType.Int, Value = note.Key / 12 - 1 };
            tree.Attributes["Lyric"] = new ValueTreeValue
                { ValueType = ValueTreeValueType.String, Value = note.Lyric };
            tree.Attributes["Phoneme"] = new ValueTreeValue
                { ValueType = ValueTreeValueType.String, Value = note.Phoneme ?? "" };
            tree.Attributes["Clock"] = new ValueTreeValue
                { ValueType = ValueTreeValueType.Int, Value = (int)(note.TickOn * TickRate) };
            tree.Attributes["Syllabic"] = new ValueTreeValue
                { ValueType = ValueTreeValueType.Int, Value = 0 };
            tree.Attributes["Duration"] = new ValueTreeValue
                { ValueType = ValueTreeValueType.Int, Value = (int)((note.TickOff - note.TickOn) * TickRate) };
            return tree;
        }).ToList();
    }

    private static ValueTree DeepClone(ValueTree source)
    {
        var clone = new ValueTree
        {
            Type = source.Type,
            Attributes = new Dictionary<string, ValueTreeValue>(
                source.Attributes.Select(kv => new KeyValuePair<string, ValueTreeValue>(
                    kv.Key, CloneValue(kv.Value)))),
            Children = source.Children.Select(DeepClone).ToList(),
        };
        return clone;
    }

    private static ValueTreeValue CloneValue(ValueTreeValue source)
    {
        var clone = new ValueTreeValue { ValueType = source.ValueType };
        clone.Value = source.Value switch
        {
            byte[] bytes => bytes.AsSpan().ToArray(),
            List<ValueTreeValue> list => list.Select(CloneValue).ToList(),
            _ => source.Value,
        };
        return clone;
    }
}
