using System.Xml.Linq;
using UtaFormatix.Core.Exceptions;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;
using UtaFormatix.Core.Process.Pitch;

namespace UtaFormatix.Core.IO;

public static class Vsqx
{
    private const double BpmRate = 100.0;
    private const int MinMeasureOffset = 1;

    private static readonly string TemplateVsqx = LoadTemplate();

    private static string LoadTemplate()
    {
        var assembly = typeof(Vsqx).Assembly;
        using var stream = assembly.GetManifestResourceStream("UtaFormatix.Core.Resources.template.vsqx");
        if (stream == null)
            throw new InvalidOperationException("VSQX template not found in embedded resources");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static Project Parse(string filePath, ImportParams? importParams = null)
    {
        var parameters = importParams ?? new ImportParams();
        var text = File.ReadAllText(filePath);

        var tagNames = text.Contains("xmlns=\"http://www.yamaha.co.jp/vocaloid/schema/vsq3/\"")
            ? TagNames.Vsq3
            : text.Contains("xmlns=\"http://www.yamaha.co.jp/vocaloid/schema/vsq4/\"")
                ? TagNames.Vsq4
                : throw new IllegalFileException.UnknownVsqVersion();

        return Parse(text, tagNames, Path.GetFileNameWithoutExtension(filePath), parameters);
    }

    private static Project Parse(string text, TagNamesInstance tagNames, string projectName, ImportParams parameters)
    {
        var warnings = new List<ImportWarning>();
        var doc = XDocument.Parse(text);
        var root = doc.Root ?? throw new IllegalFileException.XmlRootNotFound();
        var ns = root.Name.Namespace;

        var masterTrack = root.GetSingleElement(ns, tagNames.MasterTrack);

        var preMeasureNode = masterTrack.GetSingleElement(ns, tagNames.PreMeasure);
        var measurePrefix = int.TryParse(preMeasureNode.Value, out var mp)
            ? mp
            : throw new IllegalFileException.XmlElementValueIllegal(tagNames.PreMeasure);

        var (tickPrefix, timeSignatures) = ParseTimeSignatures(masterTrack, ns, tagNames, measurePrefix, warnings);
        var tempos = ParseTempos(masterTrack, ns, tagNames, tickPrefix, warnings);

        var tracks = root.Elements(ns + tagNames.VsTrack)
            .Select((element, index) => ParseTrack(element, index, ns, tagNames, tickPrefix, parameters))
            .ToList();

        return new Project(
            Format: Format.Vsqx,
            Name: projectName,
            Tracks: tracks,
            TimeSignatures: timeSignatures,
            Tempos: tempos,
            MeasurePrefix: measurePrefix,
            ImportWarnings: warnings);
    }

    private static (long TickPrefix, List<TimeSignature> TimeSignatures) ParseTimeSignatures(
        XElement masterTrack, XNamespace ns, TagNamesInstance tagNames, int measurePrefix, List<ImportWarning> warnings)
    {
        var rawTimeSignatures = masterTrack.Elements(ns + tagNames.TimeSig)
            .SelectNotNull(it =>
            {
                var posMes = it.GetSingleElementOrNull(ns, tagNames.PosMes)?.Value?.ToIntOrNull();
                var nume = it.GetSingleElementOrNull(ns, tagNames.Nume)?.Value?.ToIntOrNull();
                var denomi = it.GetSingleElementOrNull(ns, tagNames.Denomi)?.Value?.ToIntOrNull();
                if (posMes == null || nume == null || denomi == null) return null;
                return new TimeSignature(posMes.Value, nume.Value, denomi.Value);
            })
            .ToList();

        if (rawTimeSignatures.Count == 0)
        {
            warnings.Add(new ImportWarning.TimeSignatureNotFound());
            rawTimeSignatures = [new TimeSignature(0, 4, 4)];
        }

        var tickPrefix = GetTickPrefix(rawTimeSignatures, measurePrefix);

        var timeSignatures = rawTimeSignatures
            .Select(ts => ts with { MeasurePosition = ts.MeasurePosition - measurePrefix })
            .ToList();

        var firstTimeSignatureIndex = timeSignatures
            .Select((ts, i) => (ts, i))
            .Where(x => x.ts.MeasurePosition <= 0)
            .LastOrDefault().i;

        for (var i = 0; i < firstTimeSignatureIndex; i++)
        {
            warnings.Add(new ImportWarning.TimeSignatureIgnoredInPreMeasure(timeSignatures[0]));
            timeSignatures.RemoveAt(0);
        }

        if (timeSignatures.Count > 0)
            timeSignatures[0] = timeSignatures[0] with { MeasurePosition = 0 };

        return (tickPrefix, timeSignatures);
    }

    private static long GetTickPrefix(List<TimeSignature> timeSignatures, int measurePrefix)
    {
        var counter = new TickCounter();
        foreach (var ts in timeSignatures.Where(ts => ts.MeasurePosition < measurePrefix))
            counter.GoToMeasure(ts);
        counter.GoToMeasure(measurePrefix);
        return counter.Tick;
    }

    private static List<Tempo> ParseTempos(
        XElement masterTrack, XNamespace ns, TagNamesInstance tagNames, long tickPrefix, List<ImportWarning> warnings)
    {
        var tempos = masterTrack.Elements(ns + tagNames.Tempo)
            .SelectNotNull(it =>
            {
                var posTick = it.GetSingleElementOrNull(ns, tagNames.PosTick)?.Value?.ToLongOrNull();
                var bpmRaw = it.GetSingleElementOrNull(ns, tagNames.Bpm)?.Value?.ToDoubleOrNull();
                if (posTick == null || bpmRaw == null) return null;
                return new Tempo(posTick.Value - tickPrefix, bpmRaw.Value / BpmRate);
            })
            .ToList();

        if (tempos.Count == 0)
        {
            warnings.Add(new ImportWarning.TempoNotFound());
            tempos = [new Tempo(0, Constants.DefaultBpm)];
        }

        var firstTempoIndex = tempos
            .Select((t, i) => (t, i))
            .Where(x => x.t.TickPosition <= 0)
            .LastOrDefault().i;

        for (var i = 0; i < firstTempoIndex; i++)
        {
            warnings.Add(new ImportWarning.TempoIgnoredInPreMeasure(tempos[0]));
            tempos.RemoveAt(0);
        }

        if (tempos.Count > 0)
            tempos[0] = tempos[0] with { TickPosition = 0 };

        return tempos;
    }

    private static Track ParseTrack(
        XElement trackNode, int id, XNamespace ns, TagNamesInstance tagNames, long tickPrefix, ImportParams parameters)
    {
        var trackName = trackNode.GetSingleElementOrNull(ns, tagNames.TrackName)?.Value ?? $"Track {id + 1}";
        var partNodes = trackNode.Elements(ns + tagNames.MusicalPart).ToList();

        var notes = new List<Note>();
        var noteIndex = 0;
        foreach (var partNode in partNodes)
        {
            var tickOffset = partNode.GetSingleElement(ns, tagNames.PosTick).Value.ToLong() - tickPrefix;
            foreach (var noteNode in partNode.Elements(ns + tagNames.Note))
            {
                var key = noteNode.GetSingleElement(ns, tagNames.NoteNum).Value.ToInt();
                var tickOn = noteNode.GetSingleElement(ns, tagNames.PosTick).Value.ToLong();
                var length = noteNode.GetSingleElement(ns, tagNames.Duration).Value.ToLong();
                var lyric = noteNode.GetSingleElementOrNull(ns, tagNames.Lyric)?.Value ?? parameters.DefaultLyric;
                var xSampa = noteNode.GetSingleElementOrNull(ns, tagNames.XSampa)?.Value;

                notes.Add(new Note(
                    Id: noteIndex++,
                    Key: key,
                    Lyric: lyric,
                    TickOn: tickOn + tickOffset,
                    TickOff: tickOn + tickOffset + length,
                    Phoneme: xSampa));
            }
        }

        Pitch? pitch = null;
        if (!parameters.SimpleImport)
        {
            var pitchByParts = partNodes.Select(partNode =>
            {
                var tickOffset = partNode.GetSingleElement(ns, tagNames.PosTick).Value.ToLong() - tickPrefix;
                var controlNodes = partNode.Elements(ns + tagNames.MCtrl).ToList();

                var pbs = controlNodes
                    .Where(it => it.GetSingleElement(ns, tagNames.Attr).Attribute(tagNames.Id)?.Value == tagNames.PbsName)
                    .Select(it => new VocaloidPartPitchData.Event(
                        it.GetSingleElement(ns, tagNames.PosTick).Value.ToLong(),
                        it.GetSingleElement(ns, tagNames.Attr).Value.ToInt()))
                    .ToList();

                var pit = controlNodes
                    .Where(it => it.GetSingleElement(ns, tagNames.Attr).Attribute(tagNames.Id)?.Value == tagNames.PitName)
                    .Select(it => new VocaloidPartPitchData.Event(
                        it.GetSingleElement(ns, tagNames.PosTick).Value.ToLong(),
                        it.GetSingleElement(ns, tagNames.Attr).Value.ToInt()))
                    .ToList();

                return new VocaloidPartPitchData(tickOffset, pit, pbs);
            }).ToList();

            pitch = VocaloidPitchConversion.PitchFromVocaloidParts(pitchByParts);
        }

        return new Track(id, trackName, notes, pitch).ValidateNotes();
    }

    public static (byte[] Data, string FileName, List<ExportNotification> Notifications) Generate(
        Project project, IEnumerable<FeatureConfig>? features = null)
    {
        var featureList = features?.ToList() ?? [];
        var doc = GenerateContent(project, featureList);
        var content = doc.ToString(SaveOptions.DisableFormatting).CleanEmptyXmlns();
        var data = System.Text.Encoding.UTF8.GetBytes(content);
        var name = Format.Vsqx.GetFileName(project.Name);

        var notifications = new List<ExportNotification>();
        if (!project.HasXSampaData)
            notifications.Add(new ExportNotification.PhonemeResetRequiredV4());
        if (featureList.Contains(Feature.ConvertPitch))
            notifications.Add(new ExportNotification.PitchDataExported());

        return (data, name, notifications);
    }

    public static void GenerateFile(Project project, string filePath, IEnumerable<FeatureConfig>? features = null)
    {
        var (data, _, _) = Generate(project, features);
        File.WriteAllBytes(filePath, data);
    }

    private static XDocument GenerateContent(Project project, List<FeatureConfig> features)
    {
        var tagNames = TagNames.Vsq4;
        var doc = XDocument.Parse(TemplateVsqx);
        var root = doc.Root ?? throw new IllegalFileException.XmlRootNotFound();
        var ns = root.Name.Namespace;

        var mixer = root.GetSingleElement(ns, tagNames.Mixer);
        var masterTrack = root.GetSingleElement(ns, tagNames.MasterTrack);

        var measurePrefix = Math.Max(project.MeasurePrefix, MinMeasureOffset);
        masterTrack.SetSingleChildValue(ns, tagNames.PreMeasure, measurePrefix);
        var tickPrefix = (long)Constants.TicksInFullNote * project.TimeSignatures[0].Numerator / project.TimeSignatures[0].Denominator * measurePrefix;

        SetupTempoNodes(masterTrack, ns, tagNames, project.Tempos, tickPrefix);
        SetupTimeSignatureNodes(masterTrack, ns, tagNames, project.TimeSignatures, measurePrefix);

        var emptyTrack = root.GetSingleElement(ns, tagNames.VsTrack);
        var emptyUnit = mixer.GetSingleElement(ns, tagNames.VsUnit);

        var track = emptyTrack;
        var unit = emptyUnit;

        for (var trackIndex = 0; trackIndex < project.Tracks.Count; trackIndex++)
        {
            var newTrack = GenerateNewTrackNode(emptyTrack, ns, tagNames, trackIndex, project, tickPrefix, features);
            track.AddAfterSelf(newTrack);
            track = newTrack;

            var newUnit = new XElement(emptyUnit);
            newUnit.SetSingleChildValue(ns, tagNames.TrackNum, trackIndex);
            unit.AddAfterSelf(newUnit);
            unit = newUnit;
        }

        emptyTrack.Remove();
        emptyUnit.Remove();

        return doc;
    }

    private static void SetupTempoNodes(XElement masterTrack, XNamespace ns, TagNamesInstance tagNames, List<Tempo> models, long tickPrefix)
    {
        var empty = masterTrack.GetSingleElement(ns, tagNames.Tempo);
        var previous = empty;
        previous.SetSingleChildValue(ns, tagNames.Bpm, (int)(models[0].Bpm * BpmRate));

        foreach (var model in models.Skip(1))
        {
            var adjusted = model.TickPosition == 0L
                ? model
                : model with { TickPosition = model.TickPosition + tickPrefix };

            var newNode = new XElement(empty);
            newNode.SetSingleChildValue(ns, tagNames.PosTick, adjusted.TickPosition);
            newNode.SetSingleChildValue(ns, tagNames.Bpm, (int)(adjusted.Bpm * BpmRate));
            previous.AddAfterSelf(newNode);
            previous = newNode;
        }
    }

    private static void SetupTimeSignatureNodes(XElement masterTrack, XNamespace ns, TagNamesInstance tagNames,
        List<TimeSignature> models, int measurePrefix)
    {
        var empty = masterTrack.GetSingleElement(ns, tagNames.TimeSig);
        var previous = empty;
        previous.SetSingleChildValue(ns, tagNames.Nume, models[0].Numerator);
        previous.SetSingleChildValue(ns, tagNames.Denomi, models[0].Denominator);

        foreach (var model in models.Skip(1))
        {
            var adjusted = model.MeasurePosition == 0
                ? model
                : model with { MeasurePosition = model.MeasurePosition + measurePrefix };

            var newNode = new XElement(empty);
            newNode.SetSingleChildValue(ns, tagNames.PosMes, adjusted.MeasurePosition);
            newNode.SetSingleChildValue(ns, tagNames.Nume, adjusted.Numerator);
            newNode.SetSingleChildValue(ns, tagNames.Denomi, adjusted.Denominator);
            previous.AddAfterSelf(newNode);
            previous = newNode;
        }
    }

    private static XElement GenerateNewTrackNode(XElement emptyTrack, XNamespace ns, TagNamesInstance tagNames, int trackIndex,
        Project project, long tickPrefix, List<FeatureConfig> features)
    {
        var trackModel = project.Tracks[trackIndex];

        var newTrack = new XElement(emptyTrack);
        newTrack.SetSingleChildValue(ns, tagNames.TrackNum, trackIndex);
        newTrack.SetSingleChildValue(ns, tagNames.TrackName, trackModel.Name);

        var part = newTrack.GetSingleElement(ns, tagNames.MusicalPart);
        part.SetSingleChildValue(ns, tagNames.PosTick, tickPrefix);
        part.SetSingleChildValue(ns, tagNames.PlayTime, trackModel.Notes.LastOrDefault()?.TickOff ?? 0);

        SetupPitchControllingNodes(features.Contains(Feature.ConvertPitch), ns, part, trackModel, tagNames);

        var emptyNote = part.GetSingleElement(ns, tagNames.Note);
        var note = emptyNote;

        foreach (var model in trackModel.Notes)
        {
            var newNote = GenerateNewNote(emptyNote, ns, tagNames, model);
            note.AddAfterSelf(newNote);
            note = newNote;
        }

        emptyNote.Remove();

        if (trackModel.Notes.Count == 0)
            part.Remove();

        return newTrack;
    }

    private static XElement GenerateNewNote(XElement emptyNote, XNamespace ns, TagNamesInstance tagNames, Note model)
    {
        var newNote = new XElement(emptyNote);
        newNote.SetSingleChildValue(ns, tagNames.PosTick, model.TickOn);
        newNote.SetSingleChildValue(ns, tagNames.Duration, model.Length);
        newNote.SetSingleChildValue(ns, tagNames.NoteNum, model.Key);

        var lyricElement = newNote.GetSingleElement(ns, tagNames.Lyric);
        lyricElement.Value = model.Lyric;

        if (!string.IsNullOrEmpty(model.Phoneme))
        {
            var xSampaElement = newNote.GetSingleElement(ns, tagNames.XSampa);
            xSampaElement.Value = model.Phoneme;
            xSampaElement.SetAttributeValue(tagNames.XSampaLock, "1");
        }

        return newNote;
    }

    private static void SetupPitchControllingNodes(bool convert, XNamespace ns, XElement part, Track trackModel, TagNamesInstance tagNames)
    {
        var emptyControl = part.GetSingleElement(ns, tagNames.MCtrl);
        var pitchRawData = trackModel.Pitch?.GenerateForVocaloid([.. trackModel.Notes]);

        if (!convert || pitchRawData == null)
        {
            emptyControl.Remove();
            return;
        }

        var currentElement = emptyControl;
        var eventsWithName = pitchRawData.Pbs.Select(e => (e, Name: tagNames.PbsName))
            .Concat(pitchRawData.Pit.Select(e => (e, Name: tagNames.PitName)))
            .OrderBy(x => x.e.Pos)
            .ToList();

        foreach (var (evt, name) in eventsWithName)
        {
            var newControlNode = new XElement(emptyControl);
            newControlNode.SetSingleChildValue(ns, tagNames.PosTick, evt.Pos);
            newControlNode.GetSingleElement(ns, tagNames.Attr).SetAttributeValue(tagNames.Id, name);
            newControlNode.GetSingleElement(ns, tagNames.Attr).Value = evt.Value.ToString();
            currentElement.AddAfterSelf(newControlNode);
            currentElement = newControlNode;
        }

        emptyControl.Remove();
    }

    private static string CleanEmptyXmlns(this string xml) =>
        xml.Replace(" xmlns=\"\"", "");

    private class TagNames
    {
        public static readonly TagNamesInstance Vsq3 = new(
            masterTrack: "masterTrack",
            preMeasure: "preMeasure",
            timeSig: "timeSig",
            posMes: "posMes",
            nume: "nume",
            denomi: "denomi",
            tempo: "tempo",
            posTick: "posTick",
            bpm: "bpm",
            vsTrack: "vsTrack",
            trackName: "trackName",
            musicalPart: "musicalPart",
            note: "note",
            duration: "durTick",
            noteNum: "noteNum",
            lyric: "lyric",
            xSampa: "phnms",
            xSampaLock: "lock",
            mixer: "mixer",
            vsUnit: "vsUnit",
            trackNum: "vsTrackNo",
            playTime: "playTime",
            mCtrl: "mCtrl",
            attr: "attr",
            id: "id",
            pbsName: "PBS",
            pitName: "PIT");

        public static readonly TagNamesInstance Vsq4 = new(
            masterTrack: "masterTrack",
            preMeasure: "preMeasure",
            timeSig: "timeSig",
            posMes: "m",
            nume: "nu",
            denomi: "de",
            tempo: "tempo",
            posTick: "t",
            bpm: "v",
            vsTrack: "vsTrack",
            trackName: "name",
            musicalPart: "vsPart",
            note: "note",
            duration: "dur",
            noteNum: "n",
            lyric: "y",
            xSampa: "p",
            xSampaLock: "lock",
            mixer: "mixer",
            vsUnit: "vsUnit",
            trackNum: "tNo",
            playTime: "playTime",
            mCtrl: "cc",
            attr: "v",
            id: "id",
            pbsName: "S",
            pitName: "P");
    }

    private sealed class TagNamesInstance(
        string masterTrack, string preMeasure, string timeSig, string posMes, string nume, string denomi,
        string tempo, string posTick, string bpm, string vsTrack, string trackName, string musicalPart,
        string note, string duration, string noteNum, string lyric, string xSampa, string xSampaLock,
        string mixer, string vsUnit, string trackNum, string playTime, string mCtrl, string attr, string id,
        string pbsName, string pitName)
    {
        public string MasterTrack { get; } = masterTrack;
        public string PreMeasure { get; } = preMeasure;
        public string TimeSig { get; } = timeSig;
        public string PosMes { get; } = posMes;
        public string Nume { get; } = nume;
        public string Denomi { get; } = denomi;
        public string Tempo { get; } = tempo;
        public string PosTick { get; } = posTick;
        public string Bpm { get; } = bpm;
        public string VsTrack { get; } = vsTrack;
        public string TrackName { get; } = trackName;
        public string MusicalPart { get; } = musicalPart;
        public string Note { get; } = note;
        public string Duration { get; } = duration;
        public string NoteNum { get; } = noteNum;
        public string Lyric { get; } = lyric;
        public string XSampa { get; } = xSampa;
        public string XSampaLock { get; } = xSampaLock;
        public string Mixer { get; } = mixer;
        public string VsUnit { get; } = vsUnit;
        public string TrackNum { get; } = trackNum;
        public string PlayTime { get; } = playTime;
        public string MCtrl { get; } = mCtrl;
        public string Attr { get; } = attr;
        public string Id { get; } = id;
        public string PbsName { get; } = pbsName;
        public string PitName { get; } = pitName;
    }

    private static XElement GetSingleElement(this XElement parent, XNamespace ns, string name)
    {
        var element = parent.Element(ns + name);
        return element ?? throw new IllegalFileException.XmlElementNotFound(name);
    }

    private static XElement? GetSingleElementOrNull(this XElement parent, XNamespace ns, string name) =>
        parent.Element(ns + name);

    private static void SetSingleChildValue(this XElement parent, XNamespace ns, string childName, object value)
    {
        var child = parent.Element(ns + childName);
        if (child != null)
        {
            child.Value = value.ToString()!;
        }
        else
        {
            parent.Add(new XElement(ns + childName, value));
        }
    }

    private static IEnumerable<T> SelectNotNull<T>(this IEnumerable<XElement> source,
        Func<XElement, T?> selector) where T : class
    {
        foreach (var item in source)
        {
            var result = selector(item);
            if (result != null) yield return result;
        }
    }

    private static int? ToIntOrNull(this string s) => int.TryParse(s, out var result) ? result : null;
    private static long? ToLongOrNull(this string s) => long.TryParse(s, out var result) ? result : null;
    private static double? ToDoubleOrNull(this string s) => double.TryParse(s, out var result) ? result : null;
    private static int ToInt(this string s) => int.Parse(s);
    private static long ToLong(this string s) => long.Parse(s);
}
