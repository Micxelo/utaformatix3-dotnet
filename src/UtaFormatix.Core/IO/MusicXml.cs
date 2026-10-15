using System.IO.Compression;
using System.Xml.Linq;
using UtaFormatix.Core.Exceptions;
using UtaFormatix.Core.Models;

namespace UtaFormatix.Core.IO;

public static class MusicXml
{
    private const double DefaultTickRateCevio = 2.0;

    private static string LoadTemplate()
    {
        var assembly = typeof(MusicXml).Assembly;
        using var stream = assembly.GetManifestResourceStream("UtaFormatix.Core.Resources.template.musicxml");
        if (stream == null)
            throw new InvalidOperationException("MusicXML template not found in embedded resources");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static Project Parse(string filePath, ImportParams? importParams = null)
    {
        var projectName = Path.GetFileNameWithoutExtension(filePath);
        var text = File.ReadAllText(filePath);
        var doc = XDocument.Parse(text);

        var root = doc.Root ?? throw new IllegalFileException.XmlRootNotFound();
        var partNodes = root.Elements("part").ToList();

        var warnings = new List<ImportWarning>();
        var masterTrack = partNodes.FirstOrDefault(p => p.Elements("measure").Any())
            ?? throw new IllegalFileException.XmlElementNotFound("measure");

        var masterTrackResult = ParseMasterTrack(masterTrack, warnings);
        var timeSignatures = masterTrackResult.TimeSignatures.Count > 0
            ? masterTrackResult.TimeSignatures
            : [new TimeSignature(0, Constants.DefaultMeterHigh, Constants.DefaultMeterLow)];
        var tempos = masterTrackResult.TempoWithMeasureIndexes.Select(t => t.Tempo).ToList();
        if (tempos.Count == 0)
            tempos = [new Tempo(0, Constants.DefaultBpm)];

        var parameters = importParams ?? new ImportParams();
        var tracks = partNodes.Select((element, index) =>
            ParseTrack(index, element, masterTrackResult, parameters.DefaultLyric)).ToList();

        return new Project(
            Format: Format.MusicXml,
            Name: projectName,
            Tracks: tracks,
            TimeSignatures: timeSignatures,
            Tempos: tempos,
            MeasurePrefix: 0,
            ImportWarnings: warnings);
    }

    private static MasterTrackParseResult ParseMasterTrack(XElement partNode, List<ImportWarning> warnings)
    {
        var measureNodes = partNode.Elements("measure").ToList();
        var divisions = measureNodes.First()
            .Elements("attributes")
            .SelectMany(a => a.Elements("divisions"))
            .First()
            .Value;
        var divisionsLong = long.Parse(divisions);
        var importTickRate = (double)Constants.TicksInBeat / divisionsLong;

        var tempos = new List<(int MeasureIndex, Tempo Tempo)>();
        var timeSignatures = new List<TimeSignature>();
        var measureBorders = new List<long> { 0 };
        var tickPosition = 0L;
        var currentTimeSignature = new TimeSignature(0, Constants.DefaultMeterHigh, Constants.DefaultMeterLow);

        for (var index = 0; index < measureNodes.Count; index++)
        {
            var measureNode = measureNodes[index];
            var timeNode = measureNode
                .Elements("attributes")
                .SelectMany(a => a.Elements("time"))
                .FirstOrDefault();

            if (timeNode is not null)
            {
                var ts = new TimeSignature(
                    index,
                    int.Parse(timeNode.Element("beats")!.Value),
                    int.Parse(timeNode.Element("beat-type")!.Value));
                timeSignatures.Add(ts);
                currentTimeSignature = ts;
            }

            var soundNode = measureNode.Elements("sound")
                .FirstOrDefault(s => s.Attribute("tempo") is not null);
            if (soundNode is not null)
            {
                var tempo = new Tempo(tickPosition, double.Parse(soundNode.Attribute("tempo")!.Value));
                tempos.Add((index, tempo));
            }

            tickPosition += (long)Constants.TicksInFullNote * currentTimeSignature.Numerator / currentTimeSignature.Denominator;
            measureBorders.Add(tickPosition);
        }

        if (timeSignatures.Count == 0)
            warnings.Add(new ImportWarning.TimeSignatureNotFound());
        if (tempos.Count == 0)
            warnings.Add(new ImportWarning.TempoNotFound());

        return new MasterTrackParseResult(tempos, timeSignatures, importTickRate, measureBorders);
    }

    private static Track ParseTrack(
        int trackIndex, XElement partNode, MasterTrackParseResult masterTrackResult, string defaultLyric)
    {
        var trackName = $"Track {trackIndex + 1}";
        var notes = new List<Note>();
        var isInsideNote = false;
        var importTickRate = masterTrackResult.ImportTickRate;
        var measureNodes = partNode.Elements("measure").ToList();

        for (var index = 0; index < measureNodes.Count; index++)
        {
            var measureNode = measureNodes[index];
            var tickPosition = masterTrackResult.MeasureBorders[index];

            foreach (var noteNode in measureNode.Elements("note"))
            {
                var durationEl = noteNode.Element("duration");
                long duration;
                if (durationEl is not null && long.TryParse(durationEl.Value, out var parsed))
                {
                    duration = (long)(parsed * importTickRate);
                }
                else if (noteNode.Element("grace") is not null)
                {
                    continue;
                }
                else
                {
                    throw new IllegalFileException.XmlElementNotFound("duration");
                }

                if (noteNode.Elements("rest").Any())
                {
                    tickPosition += duration;
                    continue;
                }

                var pitchNode = noteNode.Element("pitch");
                var key = pitchNode is not null ? ParsePitch(pitchNode) : Constants.DefaultKey;

                var lyric = noteNode.Element("lyric")?.Element("text")?.Value ?? defaultLyric;

                Note note;
                if (!isInsideNote)
                {
                    note = new Note(0, key, lyric, tickPosition, tickPosition + duration);
                }
                else
                {
                    var lastNote = notes[^1];
                    note = lastNote with { TickOff = lastNote.TickOff + duration };
                    notes.RemoveAt(notes.Count - 1);
                }

                tickPosition += duration;
                notes.Add(note);

                var tieType = noteNode.Element("tie")?.Attribute("type")?.Value;
                switch (tieType)
                {
                    case "start":
                        isInsideNote = true;
                        break;
                    case "stop":
                        isInsideNote = false;
                        break;
                }
            }
        }

        return new Track(trackIndex, trackName, notes, null);
    }

    private static int ParsePitch(XElement pitchNode)
    {
        var step = pitchNode.Element("step")!.Value;
        var alterEl = pitchNode.Element("alter");
        var alter = alterEl is not null ? int.Parse(alterEl.Value) : 0;
        var relativeKey = step switch
        {
            "C" => 0,
            "D" => 2,
            "E" => 4,
            "F" => 5,
            "G" => 7,
            "A" => 9,
            "B" => 11,
            _ => throw new InvalidOperationException(),
        } + alter;
        var octave = int.Parse(pitchNode.Element("octave")!.Value) + 1;
        return octave * Constants.KeyInOctave + relativeKey;
    }

    public static void GenerateFile(Project project, string filePath, IEnumerable<FeatureConfig>? features = null)
    {
        var projectWithTickRateApplied = ApplyTickRate(project);
        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        foreach (var track in projectWithTickRateApplied.Tracks)
        {
            var content = GenerateTrackContent(projectWithTickRateApplied, track);
            var trackNameSafe = GetSafeFileName(track.Name);
            var entryName = $"{project.Name}_{track.Id + 1}_{trackNameSafe}.{Format.MusicXml.Extension}";
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    private static string GenerateTrackContent(Project project, Track track)
    {
        var keyTicks = GetKeyTicks(project, track);
        var measures = GetMeasures(keyTicks, project.TimeSignatures);

        var templateText = LoadTemplate();
        var doc = XDocument.Parse(templateText);
        var root = doc.Root!;
        var partNode = root.Element("part")!;
        var firstMeasureNode = partNode.Element("measure")!;
        firstMeasureNode.Remove();

        for (var index = 0; index < measures.Count; index++)
        {
            var measureNode = GenerateMeasureNode(doc, measures[index], index, index == 0 ? firstMeasureNode : null);
            partNode.Add(measureNode);
        }

        using var writer = new StringWriter();
        doc.Save(writer);
        return writer.ToString();
    }

    private static XElement GenerateMeasureNode(
        XDocument doc, MXmlMeasure measure, int index, XElement? baseMeasureNode)
    {
        var node = baseMeasureNode is not null
            ? new XElement(baseMeasureNode)
            : new XElement("measure");
        node.SetAttributeValue("number", (index + 1).ToString());

        if (measure.TimeSignature is not null)
        {
            node.Add(GenerateTimeSignatureNode(measure.TimeSignature));
        }

        foreach (var content in measure.Contents)
        {
            switch (content)
            {
                case MXmlMeasureContent.Tempo tempo:
                    foreach (var el in GenerateNodesForTempo(tempo))
                        node.Add(el);
                    break;
                case MXmlMeasureContent.Rest rest:
                    node.Add(GenerateRestNode(rest));
                    break;
                case MXmlMeasureContent.Note note:
                    node.Add(GenerateNoteNode(note));
                    break;
            }
        }

        return node;
    }

    private static XElement GenerateTimeSignatureNode(TimeSignature timeSignature) =>
        new("attributes",
            new XElement("time",
                new XElement("beats", timeSignature.Numerator),
                new XElement("beat-type", timeSignature.Denominator)));

    private static List<XElement> GenerateNodesForTempo(MXmlMeasureContent.Tempo tempo)
    {
        var soundNode = new XElement("sound", new XAttribute("tempo", tempo.Bpm.ToString()));
        var directionNode = new XElement("direction",
            new XElement("direction-type",
                new XElement("metronome",
                    new XElement("beat-unit", "quarter"),
                    new XElement("per-minute", tempo.Bpm.ToString()))),
            new XElement(soundNode));
        return [soundNode, directionNode];
    }

    private static XElement GenerateRestNode(MXmlMeasureContent.Rest rest) =>
        new("note",
            new XElement("rest"),
            new XElement("duration", rest.Duration));

    private static XElement GenerateNoteNode(MXmlMeasureContent.Note note)
    {
        var octave = (note.NoteModel.Key / Constants.KeyInOctave) - 1;
        var (step, alter) = (note.NoteModel.Key % Constants.KeyInOctave) switch
        {
            0 => ("C", 0),
            1 => ("C", 1),
            2 => ("D", 0),
            3 => ("D", 1),
            4 => ("E", 0),
            5 => ("F", 0),
            6 => ("F", 1),
            7 => ("G", 0),
            8 => ("G", 1),
            9 => ("A", 0),
            10 => ("A", 1),
            11 => ("B", 0),
            _ => throw new InvalidOperationException(),
        };

        var pitchEl = new XElement("pitch",
            new XElement("step", step));
        if (alter == 1)
            pitchEl.Add(new XElement("alter", alter));
        pitchEl.Add(new XElement("octave", octave));

        var noteEl = new XElement("note",
            pitchEl,
            new XElement("duration", note.Duration));

        var tieType = note.Type switch
        {
            MXmlMeasureContent.NoteType.Begin => "start",
            MXmlMeasureContent.NoteType.End => "stop",
            _ => (string?)null,
        };

        if (tieType is not null)
        {
            noteEl.Add(new XElement("tie", new XAttribute("type", tieType)));
            noteEl.Add(new XElement("notations",
                new XElement("tied", new XAttribute("type", tieType))));
        }

        var syllabic = note.Type switch
        {
            MXmlMeasureContent.NoteType.Begin => "begin",
            MXmlMeasureContent.NoteType.Middle => "middle",
            MXmlMeasureContent.NoteType.End => "end",
            MXmlMeasureContent.NoteType.Single => "single",
            _ => "single",
        };

        var lyricEl = new XElement("lyric",
            new XElement("syllabic", syllabic));
        if (note.Type is MXmlMeasureContent.NoteType.Begin or MXmlMeasureContent.NoteType.Single)
            lyricEl.Add(new XElement("text", note.NoteModel.Lyric));
        else
            lyricEl.Add(new XElement("text"));

        noteEl.Add(lyricEl);
        return noteEl;
    }

    private static Project ApplyTickRate(Project project) =>
        project with
        {
            Tempos = project.Tempos.Select(t => t with { TickPosition = (long)(t.TickPosition * DefaultTickRateCevio) }).ToList(),
            Tracks = project.Tracks.Select(track => track with
            {
                Notes = track.Notes.Select(n => n with
                {
                    TickOn = (long)(n.TickOn * DefaultTickRateCevio),
                    TickOff = (long)(n.TickOff * DefaultTickRateCevio),
                }).ToList(),
            }).ToList(),
        };

    private static List<KeyTick> GetKeyTicks(Project project, Track track)
    {
        var tempos = project.Tempos.Select(t => (KeyTick)new KeyTick.WithTempo(t.TickPosition, t));
        var noteStarts = track.Notes.Select(n => (KeyTick)new KeyTick.WithNoteStart(n.TickOn, n));
        var noteEnds = track.Notes.Select(n => (KeyTick)new KeyTick.WithNoteEnd(n.TickOff, n));
        return noteEnds.Concat(tempos).Concat(noteStarts).OrderBy(k => k.Tick).ToList();
    }

    private static List<MXmlMeasure> GetMeasures(List<KeyTick> keyTicks, List<TimeSignature> timeSignatures)
    {
        var tickCounter = new TickCounter(ticksInFullNote: (long)(Constants.TicksInFullNote * DefaultTickRateCevio));
        var measureBorderTicks = new List<long> { 0 };

        foreach (var timeSignature in timeSignatures)
        {
            var previousMeasure = tickCounter.Measure;
            var ticksInMeasure = tickCounter.TicksInMeasure;
            tickCounter.GoToMeasure(timeSignature);
            var currentMeasure = tickCounter.Measure;
            for (var i = 0; i < currentMeasure - previousMeasure; i++)
                measureBorderTicks.Add(measureBorderTicks[^1] + ticksInMeasure);
        }

        var lastTick = keyTicks[^1].Tick;
        if (lastTick >= tickCounter.Tick + tickCounter.TicksInMeasure)
        {
            var previousMeasure = tickCounter.Measure;
            var ticksInMeasure = tickCounter.TicksInMeasure;
            tickCounter.GoToTick(lastTick);
            var currentMeasure = tickCounter.Measure;
            for (var i = 0; i < currentMeasure - previousMeasure; i++)
                measureBorderTicks.Add(measureBorderTicks[^1] + ticksInMeasure);
        }

        measureBorderTicks.Add(measureBorderTicks[^1] + tickCounter.TicksInMeasure);

        var keyTicksWithMeasureBorders = measureBorderTicks
            .Zip(measureBorderTicks.Skip(1))
            .Select(pair =>
            {
                var borderPair = (pair.First, pair.Second);
                var filtered = keyTicks.Where(kt =>
                {
                    if (kt is KeyTick.WithNoteEnd)
                        return kt.Tick > borderPair.First && kt.Tick <= borderPair.Second;
                    return kt.Tick >= borderPair.First && kt.Tick < borderPair.Second;
                }).ToList();
                return (BorderPair: borderPair, KeyTicks: filtered);
            }).ToList();

        var contentGroupMap = new Dictionary<(long, long), List<MXmlMeasureContent>>();
        (Note NoteModel, long Head)? ongoingNoteWithCurrentHead = null;

        foreach (var (borderPair, keyTickGroup) in keyTicksWithMeasureBorders)
        {
            var currentTickInMeasure = 0L;
            var currentContentGroup = new List<MXmlMeasureContent>();

            foreach (var keyTick in keyTickGroup)
            {
                var keyTickRelative = keyTick.Tick - borderPair.First;
                if (keyTickRelative > currentTickInMeasure)
                {
                    if (ongoingNoteWithCurrentHead is null)
                    {
                        currentContentGroup.Add(
                            new MXmlMeasureContent.Rest(keyTickRelative - currentTickInMeasure));
                    }
                    currentTickInMeasure = keyTickRelative;
                }

                switch (keyTick)
                {
                    case KeyTick.WithTempo tempoTick:
                        if (ongoingNoteWithCurrentHead is null)
                        {
                            currentContentGroup.Add(new MXmlMeasureContent.Tempo(tempoTick.TempoModel.Bpm));
                        }
                        else
                        {
                            var (noteModel, head) = ongoingNoteWithCurrentHead.Value;
                            currentContentGroup.Add(new MXmlMeasureContent.Note(
                                keyTick.Tick - head, noteModel,
                                noteModel.TickOn == head ? MXmlMeasureContent.NoteType.Begin : MXmlMeasureContent.NoteType.Middle));
                            ongoingNoteWithCurrentHead = (noteModel, keyTick.Tick);
                            currentContentGroup.Add(new MXmlMeasureContent.Tempo(tempoTick.TempoModel.Bpm));
                        }
                        break;
                    case KeyTick.WithNoteStart noteStart:
                        ongoingNoteWithCurrentHead = (noteStart.NoteModel, keyTick.Tick);
                        break;
                    case KeyTick.WithNoteEnd noteEnd:
                    {
                        var (noteModel, head) = ongoingNoteWithCurrentHead!.Value;
                        currentContentGroup.Add(new MXmlMeasureContent.Note(
                            noteEnd.NoteModel.TickOff - head, noteEnd.NoteModel,
                            noteModel.TickOn == head ? MXmlMeasureContent.NoteType.Single : MXmlMeasureContent.NoteType.End));
                        ongoingNoteWithCurrentHead = null;
                        break;
                    }
                }
            }

            var restLength = borderPair.Second - borderPair.First - currentTickInMeasure;
            if (restLength > 0)
            {
                if (ongoingNoteWithCurrentHead is null)
                {
                    currentContentGroup.Add(new MXmlMeasureContent.Rest(restLength));
                }
                else
                {
                    var (noteModel, head) = ongoingNoteWithCurrentHead.Value;
                    currentContentGroup.Add(new MXmlMeasureContent.Note(
                        borderPair.Second - head, noteModel,
                        noteModel.TickOn == head ? MXmlMeasureContent.NoteType.Begin : MXmlMeasureContent.NoteType.Middle));
                    ongoingNoteWithCurrentHead = (noteModel, borderPair.Second);
                }
            }

            contentGroupMap[borderPair] = currentContentGroup;
        }

        return contentGroupMap
            .OrderBy(kv => kv.Key.Item1)
            .Select((kv, index) => new MXmlMeasure(
                kv.Key.Item1,
                kv.Key.Item2 - kv.Key.Item1,
                timeSignatures.Find(ts => ts.MeasurePosition == index),
                kv.Value))
            .ToList();
    }

    private static string GetSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? "_" : c.ToString()));
    }

    #region Data Models

    private sealed record MasterTrackParseResult(
        List<(int MeasureIndex, Tempo Tempo)> TempoWithMeasureIndexes,
        List<TimeSignature> TimeSignatures,
        double ImportTickRate,
        List<long> MeasureBorders);

    private abstract class KeyTick(long tick)
    {
        public long Tick { get; } = tick;

        public sealed class WithTempo(long tick, Tempo tempo) : KeyTick(tick)
        {
            public Tempo TempoModel { get; } = tempo;
        }

        public sealed class WithNoteStart(long tick, Note note) : KeyTick(tick)
        {
            public Note NoteModel { get; } = note;
        }

        public sealed class WithNoteEnd(long tick, Note note) : KeyTick(tick)
        {
            public Note NoteModel { get; } = note;
        }
    }

    private sealed record MXmlMeasure(
        long TickStart,
        long Length,
        TimeSignature? TimeSignature,
        List<MXmlMeasureContent> Contents);

    private abstract class MXmlMeasureContent
    {
        public sealed class Tempo(double bpm) : MXmlMeasureContent
        {
            public double Bpm { get; } = bpm;
        }

        public sealed class Rest(long duration) : MXmlMeasureContent
        {
            public long Duration { get; } = duration;
        }

        public sealed class Note(long duration, Models.Note noteModel, NoteType type) : MXmlMeasureContent
        {
            public long Duration { get; } = duration;
            public Models.Note NoteModel { get; } = noteModel;
            public NoteType Type { get; } = type;
        }

        public enum NoteType
        {
            Begin,
            Middle,
            End,
            Single,
        }
    }

    #endregion
}
