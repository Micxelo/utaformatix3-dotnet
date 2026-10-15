using System.Xml.Linq;
using UtaFormatix.Core.Exceptions;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;
using UtaFormatix.Core.Process.Pitch;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.IO;

public static class Ccs
{
    private const double TickRate = 2.0;
    private const int OctaveOffset = -1;
    private const int FixedMeasurePrefix = 1;

    private static string LoadTemplate()
    {
        var assembly = typeof(Ccs).Assembly;
        using var stream = assembly.GetManifestResourceStream("UtaFormatix.Core.Resources.template.ccs");
        if (stream == null)
            throw new InvalidOperationException("CCS template not found in embedded resources");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static Project Parse(string filePath, ImportParams? importParams = null)
    {
        var projectName = Path.GetFileNameWithoutExtension(filePath);
        var text = File.ReadAllText(filePath);
        var doc = XDocument.Parse(text);

        var scenarioNode = doc.Root ?? throw new IllegalFileException.XmlRootNotFound();
        var sceneNode = scenarioNode.Element("Sequence")?.Element("Scene")
            ?? throw new IllegalFileException.XmlElementNotFound("Scene");
        var unitNodes = sceneNode.Element("Units")?.Elements("Unit")
            .Where(u => u.Attribute("Category")?.Value == "SingerSong").ToList() ?? [];
        var groupNodes = sceneNode.Element("Groups")?.Elements("Group")
            .Where(g => g.Attribute("Category")?.Value == "SingerSong").ToList() ?? [];

        var parameters = importParams ?? new ImportParams();
        var results = unitNodes.Select((unitNode, index) =>
        {
            var groupId = unitNode.Attribute("Group")?.Value;
            var group = groupId is not null
                ? groupNodes.FirstOrDefault(g => g.Attribute("Id")?.Value == groupId)
                : null;
            var trackName = group?.Attribute("Name")?.Value;
            return ParseTrack(index, unitNode, trackName, parameters);
        }).ToList();

        var tracks = results.Select(r => r.Track).ToList();
        var warnings = new List<ImportWarning>();
        var tempos = MergeTempos(results, warnings);
        var timeSignatures = MergeTimeSignatures(results, warnings);

        return new Project(
            Format: Format.Ccs,
            Name: projectName,
            Tracks: tracks,
            TimeSignatures: timeSignatures,
            Tempos: tempos,
            MeasurePrefix: FixedMeasurePrefix,
            ImportWarnings: warnings);
    }

    private static List<Tempo> MergeTempos(List<TrackParseResult> results, List<ImportWarning> warnings)
    {
        var tempos = new List<Tempo>();
        var firstWithTempos = results.FirstOrDefault(r => r.Tempos.Count > 0);
        if (firstWithTempos is not null)
        {
            tempos = firstWithTempos.Tempos.ToList();
        }
        else
        {
            tempos.Add(new Tempo(0, Constants.DefaultBpm));
            warnings.Add(new ImportWarning.TempoNotFound());
        }

        foreach (var result in results)
        {
            var ignoredTempos = result.Tempos.Except(tempos).ToList();
            warnings.AddRange(ignoredTempos.Select(t =>
                (ImportWarning)new ImportWarning.TempoIgnoredInTrack(result.Track.Id, t)));
        }

        var firstTempoIndex = tempos.FindLastIndex(t => t.TickPosition <= 0);
        for (var i = 0; i < firstTempoIndex; i++)
        {
            var removed = tempos[0];
            tempos.RemoveAt(0);
            warnings.Add(new ImportWarning.TempoIgnoredInPreMeasure(removed));
        }
        if (tempos.Count > 0)
            tempos[0] = tempos[0] with { TickPosition = 0 };
        return tempos;
    }

    private static List<TimeSignature> MergeTimeSignatures(List<TrackParseResult> results, List<ImportWarning> warnings)
    {
        var timeSignatures = new List<TimeSignature>();
        var firstWithTs = results.FirstOrDefault(r => r.TimeSignatures.Count > 0);
        if (firstWithTs is not null)
        {
            timeSignatures = firstWithTs.TimeSignatures.ToList();
        }
        else
        {
            timeSignatures.Add(new TimeSignature(0, Constants.DefaultMeterHigh, Constants.DefaultMeterLow));
            warnings.Add(new ImportWarning.TimeSignatureNotFound());
        }

        foreach (var result in results)
        {
            var ignoredTs = result.TimeSignatures.Except(timeSignatures).ToList();
            warnings.AddRange(ignoredTs.Select(ts =>
                (ImportWarning)new ImportWarning.TimeSignatureIgnoredInTrack(result.Track.Id, ts)));
        }

        var firstTsIndex = timeSignatures.FindLastIndex(ts => ts.MeasurePosition <= 0);
        for (var i = 0; i < firstTsIndex; i++)
        {
            var removed = timeSignatures[0];
            timeSignatures.RemoveAt(0);
            warnings.Add(new ImportWarning.TimeSignatureIgnoredInPreMeasure(removed));
        }
        if (timeSignatures.Count > 0)
            timeSignatures[0] = timeSignatures[0] with { MeasurePosition = 0 };
        return timeSignatures;
    }

    private static TrackParseResult ParseTrack(int index, XElement unitNode, string? name, ImportParams importParams)
    {
        var songNode = unitNode.Element("Song");
        var beatNode = songNode?.Element("Beat");
        var timeNodes = beatNode?.Elements("Time").ToList() ?? [];

        var tickCounter = new TickCounter(tickRate: TickRate);
        var timeSignatures = new List<TimeSignature>();
        foreach (var timeNode in timeNodes)
        {
            if (!long.TryParse(timeNode.Attribute("Clock")?.Value, out var tick)) continue;
            if (!int.TryParse(timeNode.Attribute("Beats")?.Value, out var numerator)) continue;
            if (!int.TryParse(timeNode.Attribute("BeatType")?.Value, out var denominator)) continue;
            tickCounter.GoToTick(tick, numerator, denominator);
            timeSignatures.Add(new TimeSignature(tickCounter.Measure, numerator, denominator));
        }

        var tickPrefix = GetTickPrefix(timeSignatures, FixedMeasurePrefix);
        timeSignatures = timeSignatures
            .Select(ts => ts with { MeasurePosition = ts.MeasurePosition - FixedMeasurePrefix })
            .ToList();

        var tempoNode = songNode?.Element("Tempo");
        var soundNodes = tempoNode?.Elements("Sound").ToList() ?? [];
        var tempos = new List<Tempo>();
        foreach (var soundNode in soundNodes)
        {
            if (!long.TryParse(soundNode.Attribute("Clock")?.Value, out var rawTick)) continue;
            if (!double.TryParse(soundNode.Attribute("Tempo")?.Value, out var bpm)) continue;
            var tick = (long)(rawTick / TickRate) - tickPrefix;
            tempos.Add(new Tempo(tick, bpm));
        }

        var scoreNode = songNode?.Element("Score");
        var noteNodes = scoreNode?.Elements("Note").ToList() ?? [];
        var notes = new List<Note>();
        for (var noteIndex = 0; noteIndex < noteNodes.Count; noteIndex++)
        {
            var element = noteNodes[noteIndex];
            var clockStr = element.Attribute("Clock")?.Value
                ?? throw new IllegalFileException.XmlElementAttributeValueIllegal("Clock", "Note");
            var tickOn = (long)(long.Parse(clockStr) / TickRate) - tickPrefix;
            var durationStr = element.Attribute("Duration")?.Value
                ?? throw new IllegalFileException.XmlElementAttributeValueIllegal("Duration", "Note");
            var tickOff = tickOn + (long)(long.Parse(durationStr) / TickRate);

            var pitchStep = int.Parse(element.Attribute("PitchStep")?.Value
                ?? throw new IllegalFileException.XmlElementAttributeValueIllegal("PitchStep", "Note"));
            var pitchOctave = int.Parse(element.Attribute("PitchOctave")?.Value
                ?? throw new IllegalFileException.XmlElementAttributeValueIllegal("PitchOctave", "Note")) - OctaveOffset;
            var key = pitchStep + pitchOctave * Constants.KeyInOctave;

            var lyric = element.Attribute("Lyric")?.Value
                ?? throw new IllegalFileException.XmlElementAttributeValueIllegal("Lyric", "Note");
            var phoneticAttr = element.Attribute("Phonetic");
            var phoneme = phoneticAttr is not null ? phoneticAttr.Value.Replace(",", " ") : null;

            notes.Add(new Note(noteIndex, key, lyric, tickOn, tickOff, phoneme));
        }

        PitchModel? pitch = null;
        if (!importParams.SimpleImport)
        {
            var parameterNode = songNode?.Element("Parameter");
            var logF0Node = parameterNode?.Element("LogF0");
            var dataNodes = logF0Node?.Elements("Data").ToList() ?? [];
            var events = dataNodes.Select(ParsePitchData).Where(e => e is not null).Cast<CevioTrackPitchData.Event>().ToList();
            var cevioData = new CevioTrackPitchData(events, tempos, tickPrefix);
            pitch = CevioPitchConversion.PitchFromCevioTrack(cevioData);
        }

        var trackName = name ?? $"Track {index + 1}";
        var track = new Track(index, trackName, notes, pitch).ValidateNotes();
        return new TrackParseResult(track, tempos, timeSignatures);
    }

    private static CevioTrackPitchData.Event? ParsePitchData(XElement dataElement)
    {
        long? index = long.TryParse(dataElement.Attribute("Index")?.Value, out var i) ? i : null;
        long? repeat = long.TryParse(dataElement.Attribute("Repeat")?.Value, out var r) ? r : null;
        if (!double.TryParse(dataElement.Value, out var value)) return null;
        return new CevioTrackPitchData.Event(index, repeat, value);
    }

    private static long GetTickPrefix(List<TimeSignature> timeSignatures, int measurePrefix)
    {
        var counter = new TickCounter();
        foreach (var ts in timeSignatures.Where(ts => ts.MeasurePosition < measurePrefix))
            counter.GoToMeasure(ts);
        counter.GoToMeasure(measurePrefix);
        return counter.Tick;
    }

    private sealed record TrackParseResult(Track Track, List<Tempo> Tempos, List<TimeSignature> TimeSignatures);

    public static void GenerateFile(Project project, string filePath, IEnumerable<FeatureConfig>? features = null)
    {
        var featureList = features?.ToList() ?? [];
        var doc = GenerateContent(project, featureList);
        using var writer = new StreamWriter(filePath);
        doc.Save(writer);
    }

    private static XDocument GenerateContent(Project project, List<FeatureConfig> features)
    {
        var templateText = LoadTemplate();
        var doc = XDocument.Parse(templateText);
        var scenarioNode = doc.Root!;
        var sceneNode = scenarioNode.Element("Sequence")!.Element("Scene")!;

        var unitsNode = sceneNode.Element("Units")!;
        var emptyUnitNode = unitsNode.Element("Unit")!;
        emptyUnitNode.Remove();
        var groupsNode = sceneNode.Element("Groups")!;
        var emptyGroupNode = groupsNode.Element("Group")!;
        emptyGroupNode.Remove();

        var measurePrefix = FixedMeasurePrefix;
        var firstTsTicksInMeasure = (long)Constants.TicksInFullNote * project.TimeSignatures[0].Numerator / project.TimeSignatures[0].Denominator;
        var tickPrefix = (long)(firstTsTicksInMeasure * TickRate * measurePrefix);

        SetupTempoNodes(emptyUnitNode, project.Tempos, tickPrefix);
        SetupBeatNodes(emptyUnitNode, project.TimeSignatures, tickPrefix);

        foreach (var model in project.Tracks)
        {
            var newUnit = new XElement(emptyUnitNode);
            var newGroup = new XElement(emptyGroupNode);

            var id = Guid.NewGuid().ToString();
            newUnit.SetAttributeValue("Group", id);
            newGroup.SetAttributeValue("Id", id);
            newGroup.SetAttributeValue("Name", model.Name);
            SetupNotes(newUnit, model.Notes, tickPrefix);

            if (features.Contains(new FeatureConfig.ConvertPitchConfig()))
                SetupPitchData(newUnit, model, project.Tempos, tickPrefix);

            unitsNode.Add(newUnit);
            groupsNode.Add(newGroup);
        }

        return doc;
    }

    private static void SetupTempoNodes(XElement emptyUnitNode, List<Tempo> models, long tickPrefix)
    {
        var temposNode = emptyUnitNode.Element("Song")!.Element("Tempo")!;
        var previous = temposNode.Element("Sound")!;
        previous.SetAttributeValue("Tempo", models[0].Bpm.ToString("F2"));
        foreach (var tempo in models.Skip(1))
        {
            var newSound = new XElement(previous);
            newSound.SetAttributeValue("Tempo", tempo.Bpm.ToString("F2"));
            newSound.SetAttributeValue("Clock", ((long)(tempo.TickPosition * TickRate) + tickPrefix).ToString());
            previous.AddAfterSelf(newSound);
            previous = newSound;
        }
    }

    private static void SetupBeatNodes(XElement emptyUnitNode, List<TimeSignature> models, long tickPrefix)
    {
        var beatsNode = emptyUnitNode.Element("Song")!.Element("Beat")!;
        var previous = beatsNode.Element("Time")!;
        previous.SetAttributeValue("Beats", models[0].Numerator.ToString());
        previous.SetAttributeValue("BeatType", models[0].Denominator.ToString());
        var counter = new TickCounter(tickRate: TickRate);
        counter.GoToMeasure(models[0]);
        foreach (var ts in models.Skip(1))
        {
            var newTime = new XElement(previous);
            counter.GoToMeasure(ts);
            newTime.SetAttributeValue("Clock", (counter.OutputTick + tickPrefix).ToString());
            newTime.SetAttributeValue("Beats", ts.Numerator.ToString());
            newTime.SetAttributeValue("BeatType", ts.Denominator.ToString());
            previous.AddAfterSelf(newTime);
            previous = newTime;
        }
    }

    private static void SetupNotes(XElement unitNode, List<Note> models, long tickPrefix)
    {
        var score = unitNode.Element("Song")!.Element("Score")!;
        foreach (var note in models)
        {
            var newNote = new XElement("Note",
                new XAttribute("Clock", ((long)(note.TickOn * TickRate) + tickPrefix).ToString()),
                new XAttribute("PitchStep", (note.Key % Constants.KeyInOctave).ToString()),
                new XAttribute("PitchOctave", (note.Key / Constants.KeyInOctave + OctaveOffset).ToString()),
                new XAttribute("Duration", ((long)(note.Length * TickRate)).ToString()),
                new XAttribute("Lyric", note.Lyric));
            if (!string.IsNullOrWhiteSpace(note.Phoneme))
                newNote.SetAttributeValue("Phonetic", note.Phoneme.Replace(" ", ","));
            score.Add(newNote);
        }
    }

    private static void SetupPitchData(XElement unitNode, Track trackModel, List<Tempo> tempos, long tickPrefix)
    {
        if (trackModel.Pitch is null) return;
        var data = trackModel.Pitch.GenerateForCevio(trackModel.Notes, tempos, (long)(tickPrefix / TickRate));
        if (data is null) return;

        var dataNodes = data.Events.Select(ev =>
        {
            var newDataNode = new XElement("Data");
            if (ev.Index is not null) newDataNode.SetAttributeValue("Index", ev.Index.ToString());
            if (ev.Repeat is not null) newDataNode.SetAttributeValue("Repeat", ev.Repeat.ToString());
            newDataNode.SetValue(ev.Value);
            return newDataNode;
        }).ToList();

        var songNode = unitNode.Element("Song")!;
        var parameterNode = new XElement("Parameter");
        var logF0Node = new XElement("LogF0",
            new XAttribute("Length", data.GetLength().ToString()));
        foreach (var dn in dataNodes)
            logF0Node.Add(dn);
        parameterNode.Add(logF0Node);
        songNode.Add(parameterNode);
    }
}
