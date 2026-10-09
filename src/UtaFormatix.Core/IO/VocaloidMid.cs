using System.Text;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using UtaFormatix.Core.Exceptions;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;
using UtaFormatix.Core.Process.Pitch;

namespace UtaFormatix.Core.IO;

public static class VocaloidMid
{
    private static readonly Encoding ShiftJis;
    private static readonly Encoding Latin1;

    static VocaloidMid()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ShiftJis = Encoding.GetEncoding(932);
        Latin1 = Encoding.GetEncoding(28591);
    }

    private const int MaxVsqOutputTick = 4096 * Constants.TicksInFullNote;
    private const int MinMeasureOffset = 1;
    private const int MaxMeasureOffset = 8;

    public static Project Parse(string filePath, ImportParams? importParams = null, Format? format = null)
    {
        var parameters = importParams ?? new ImportParams();
        var outputFormat = format ?? Format.VocaloidMid;
        var readingSettings = new ReadingSettings { TextEncoding = Latin1 };
        var midiFile = MidiFile.Read(filePath, readingSettings);

        var ticksPerBeat = (midiFile.TimeDivision as TicksPerQuarterNoteTimeDivision)?.TicksPerQuarterNote
            ?? MidiUtil.StandardTimeDivision;
        var tickRate = (double)ticksPerBeat / MidiUtil.StandardTimeDivision;

        var trackChunks = midiFile.GetTrackChunks().ToArray();
        if (trackChunks.Length == 0)
            throw new CannotReadFileException();

        var warnings = new List<ImportWarning>();
        var tracksAsText = ExtractVsqTextsFromMetaEvents(trackChunks).Where(t => t.Length > 0).ToList();
        if (tracksAsText.Count == 0)
            throw new CannotReadFileException();

        var measurePrefix = GetMeasurePrefix(tracksAsText[0]);
        var (tempos, timeSignatures, tickPrefix) = ParseMasterTrack(trackChunks[0], tickRate, measurePrefix, warnings);

        var tracks = tracksAsText.Select((text, i) => ParseTrack(text, i, tickPrefix, parameters)).ToList();

        return new Project(
            Format: outputFormat,
            Name: Path.GetFileNameWithoutExtension(filePath),
            Tracks: tracks,
            TimeSignatures: timeSignatures,
            Tempos: tempos,
            MeasurePrefix: measurePrefix,
            ImportWarnings: warnings);
    }

    public static (byte[] Data, string FileName, List<ExportNotification> Notifications) Generate(
        Project project, IEnumerable<FeatureConfig>? features = null, Format? format = null)
    {
        var featureList = features?.ToList() ?? [];
        var outputFormat = format ?? Format.VocaloidMid;
        var projectFixed = project
            .LengthLimited(MaxVsqOutputTick)
            with { MeasurePrefix = project.MeasurePrefix.Clamp(MinMeasureOffset, MaxMeasureOffset) };
        projectFixed = projectFixed.WithoutEmptyTracks();

        if (projectFixed.Tracks.Count == 0)
            throw new EmptyProjectException();

        var midiFile = GenerateMidiFile(projectFixed, featureList);
        midiFile.TimeDivision = new TicksPerQuarterNoteTimeDivision(MidiUtil.StandardTimeDivision);

        using var ms = new MemoryStream();
        var writingSettings = new WritingSettings { TextEncoding = Latin1 };
        midiFile.Write(ms, MidiFileFormat.MultiTrack, writingSettings);
        var data = ms.ToArray();

        var fileName = outputFormat.GetFileName(projectFixed.Name);
        var notifications = new List<ExportNotification>();
        if (!projectFixed.HasXSampaData)
            notifications.Add(new ExportNotification.PhonemeResetRequiredVsq());
        if (featureList.Contains(Feature.ConvertPitch))
            notifications.Add(new ExportNotification.PitchDataExported());
        if (project != projectFixed)
            notifications.Add(new ExportNotification.DataOverLengthLimitIgnored());

        return (data, fileName, notifications);
    }

    public static void GenerateFile(Project project, string filePath,
        IEnumerable<FeatureConfig>? features = null, Format? format = null)
    {
        var (data, _, _) = Generate(project, features, format);
        File.WriteAllBytes(filePath, data);
    }

    private static List<string> ExtractVsqTextsFromMetaEvents(TrackChunk[] trackChunks)
    {
        return trackChunks.Skip(1).Select(track =>
        {
            var sb = new StringBuilder();
            foreach (var evt in track.Events)
            {
                if (evt is not TextEvent textEvt) continue;
                var latin1Bytes = Latin1.GetBytes(textEvt.Text);
                var text = ShiftJis.GetString(latin1Bytes);
                if (text.Length < 3) continue;
                text = text[3..];
                var colonIndex = text.IndexOf(':');
                if (colonIndex < 0) continue;
                text = text[(colonIndex + 1)..];
                sb.Append(text);
            }
            return sb.ToString();
        }).ToList();
    }

    private static int GetMeasurePrefix(string firstTrack)
    {
        const string parameterName = "PreMeasure";
        foreach (var line in firstTrack.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains(parameterName))
            {
                var value = trimmed.Replace($"{parameterName}=", "").Trim();
                if (int.TryParse(value, out var result))
                    return result;
            }
        }
        return 0;
    }

    private static (List<Tempo> Tempos, List<TimeSignature> TimeSignatures, long TickPrefix)
        ParseMasterTrack(TrackChunk masterTrack, double tickRate, int measurePrefix,
            List<ImportWarning> warnings)
    {
        var rawTempos = new List<Tempo>();
        var rawTimeSignatures = new List<TimeSignature>();
        var tickCounter = new TickCounter(tickRate);

        long rawTick = 0;
        foreach (var evt in masterTrack.Events)
        {
            rawTick += evt.DeltaTime;
            var tickPosition = (long)(rawTick / tickRate);

            if (evt is SetTempoEvent tempoEvt)
            {
                var bpm = MidiUtil.ConvertMidiTempoToBpm((long)tempoEvt.MicrosecondsPerQuarterNote);
                rawTempos.Add(new Tempo(tickPosition, bpm));
            }
            else if (evt is TimeSignatureEvent tsEvt)
            {
                tickCounter.GoToTick(rawTick, tsEvt.Numerator, tsEvt.Denominator);
                rawTimeSignatures.Add(new TimeSignature(tickCounter.Measure, tsEvt.Numerator, tsEvt.Denominator));
            }
        }

        if (rawTimeSignatures.Count == 0)
        {
            rawTimeSignatures.Add(new TimeSignature(0, 4, 4));
            warnings.Add(new ImportWarning.TimeSignatureNotFound());
        }

        if (rawTempos.Count == 0)
        {
            rawTempos.Add(new Tempo(0, Constants.DefaultBpm));
            warnings.Add(new ImportWarning.TempoNotFound());
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

        var tempos = rawTempos
            .Select(t => t with { TickPosition = t.TickPosition - tickPrefix })
            .ToList();
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

        return (tempos, timeSignatures, tickPrefix);
    }

    private static long GetTickPrefix(List<TimeSignature> timeSignatures, int measurePrefix)
    {
        var counter = new TickCounter();
        foreach (var ts in timeSignatures.Where(ts => ts.MeasurePosition < measurePrefix))
            counter.GoToMeasure(ts);
        counter.GoToMeasure(measurePrefix);
        return counter.Tick;
    }

    private static Track ParseTrack(string trackAsText, int trackId, long tickPrefix, ImportParams parameters)
    {
        var lines = trackAsText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var titleWithIndexes = new List<(string Title, int Index)>();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith('[') && line.EndsWith(']'))
                titleWithIndexes.Add((line[1..^1], i));
        }

        var sectionMap = new Dictionary<string, Dictionary<string, string>>();
        for (var i = 0; i < titleWithIndexes.Count; i++)
        {
            var (title, startLine) = titleWithIndexes[i];
            var endLine = i + 1 < titleWithIndexes.Count ? titleWithIndexes[i + 1].Index : lines.Count;
            var section = new Dictionary<string, string>();
            for (var j = startLine + 1; j < endLine; j++)
            {
                var eqIndex = lines[j].IndexOf('=');
                if (eqIndex < 0) continue;
                var key = lines[j][..eqIndex];
                var value = lines[j][(eqIndex + 1)..];
                section[key] = value;
            }
            sectionMap[title] = section;
        }

        var name = sectionMap.TryGetValue("Common", out var commonSection) && commonSection.TryGetValue("Name", out var n)
            ? n
            : $"Track {trackId + 1}";

        if (!sectionMap.TryGetValue("EventList", out var eventList))
            return new Track(trackId, name, []);

        var notes = new List<Note>();
        foreach (var (tickStr, eventId) in eventList)
        {
            if (!tickStr.All(char.IsDigit)) continue;
            if (!sectionMap.TryGetValue(eventId, out var noteSection)) continue;
            if (noteSection.GetValueOrDefault("Type") != "Anote") continue;
            if (!long.TryParse(noteSection.GetValueOrDefault("Length"), out var length)) continue;
            if (!int.TryParse(noteSection.GetValueOrDefault("Note#"), out var key)) continue;

            var tickPosition = long.Parse(tickStr) - tickPrefix;

            string lyric = parameters.DefaultLyric;
            string? phoneme = null;

            if (noteSection.TryGetValue("LyricHandle", out var lyricHandleKey) &&
                sectionMap.TryGetValue(lyricHandleKey, out var lyricHandle) &&
                lyricHandle.TryGetValue("L0", out var l0Value))
            {
                var parts = SplitLyricL0(l0Value);
                if (parts is not null)
                {
                    lyric = parts.Value.Lyric;
                    phoneme = parts.Value.Phoneme;
                }
            }

            notes.Add(new Note(0, key, lyric, tickPosition, tickPosition + length, phoneme));
        }

        var pitch = parameters.SimpleImport ? null : ParsePitchData(sectionMap, tickPrefix);
        return new Track(trackId, name, notes, pitch).ValidateNotes();
    }

    private static (string Lyric, string Phoneme)? SplitLyricL0(string l0)
    {
        var parts = l0.Split(',');
        if (parts.Length < 2) return null;
        var lyric = parts[0].Trim('"');
        var phoneme = parts[1].Trim('"');
        return (lyric, phoneme);
    }

    private static Pitch? ParsePitchData(Dictionary<string, Dictionary<string, string>> sectionMap, long tickPrefix)
    {
        var pit = new List<VocaloidPartPitchData.Event>();
        if (sectionMap.TryGetValue("PitchBendBPList", out var pitSection))
        {
            foreach (var (posStr, valStr) in pitSection)
            {
                if (long.TryParse(posStr, out var pos) && int.TryParse(valStr, out var value))
                    pit.Add(new VocaloidPartPitchData.Event(pos - tickPrefix, value));
            }
        }

        var pbs = new List<VocaloidPartPitchData.Event>();
        if (sectionMap.TryGetValue("PitchBendSensBPList", out var pbsSection))
        {
            foreach (var (posStr, valStr) in pbsSection)
            {
                if (long.TryParse(posStr, out var pos) && int.TryParse(valStr, out var value))
                    pbs.Add(new VocaloidPartPitchData.Event(pos - tickPrefix, value));
            }
        }

        return VocaloidPitchConversion.PitchFromVocaloidParts(
            [new VocaloidPartPitchData(0, pit, pbs)]);
    }

    private static MidiFile GenerateMidiFile(Project project, List<FeatureConfig> features)
    {
        var tickPrefix = Constants.TicksInFullNote * project.TimeSignatures[0].Numerator / project.TimeSignatures[0].Denominator * project.MeasurePrefix;

        var masterTrack = GenerateMasterTrack(project, tickPrefix);
        var noteTracks = project.Tracks.Select(t => GenerateTrack(t, tickPrefix, project.MeasurePrefix, project, features)).ToList();

        var chunks = new List<TrackChunk> { masterTrack };
        chunks.AddRange(noteTracks);
        return new MidiFile(chunks.ToArray());
    }

    private static TrackChunk GenerateMasterTrack(Project project, long tickPrefix)
    {
        var events = new List<MidiEvent>();
        events.Add(new SequenceTrackNameEvent("Master Track"));

        var tickEventPairs = new List<(long Tick, object Event)>();

        foreach (var tempo in project.Tempos)
        {
            var tick = tempo.TickPosition == 0 ? 0L : tempo.TickPosition + tickPrefix;
            tickEventPairs.Add((tick, tempo));
        }

        var counter = new TickCounter();
        counter.GoToMeasure(project.TimeSignatures[0]);
        tickEventPairs.Add((0L, project.TimeSignatures[0]));

        foreach (var ts in project.TimeSignatures.Skip(1))
        {
            counter.GoToMeasure(ts);
            tickEventPairs.Add((counter.OutputTick + tickPrefix, ts));
        }

        tickEventPairs.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        long lastTick = 0;
        for (var i = 0; i < tickEventPairs.Count; i++)
        {
            var (tick, evt) = tickEventPairs[i];
            var delta = tick - lastTick;

            if (evt is TimeSignature ts)
            {
                events.Add(new TimeSignatureEvent((byte)ts.Numerator, (byte)ts.Denominator) { DeltaTime = delta });
            }
            else if (evt is Tempo tempo)
            {
                events.Add(new SetTempoEvent(MidiUtil.ConvertBpmToMidiTempo(tempo.Bpm)) { DeltaTime = delta });
            }
            lastTick = tick;
        }

        return new TrackChunk(events);
    }

    private static TrackChunk GenerateTrack(Track track, long tickPrefix, int measurePrefix,
        Project project, List<FeatureConfig> features)
    {
        var events = new List<MidiEvent>();
        events.Add(new SequenceTrackNameEvent(track.Name));

        var trackText = GenerateTrackText(track, tickPrefix, measurePrefix, project, features);
        var shiftJisBytes = ShiftJis.GetBytes(trackText);

        var textEvents = new List<byte[]>();
        var offset = 0;
        while (offset < shiftJisBytes.Length)
        {
            var id = textEvents.Count;
            var idStringLength = (int)Math.Log(Math.Max(id, 1), 10000f) + 1 * 4;
            var idString = id.ToString().PadLeft(idStringLength, '0');
            var header = Latin1.GetBytes($"DM:{idString}:");
            var availableByteSize = 127 - header.Length;
            var chunk = new byte[header.Length + Math.Min(availableByteSize, shiftJisBytes.Length - offset)];
            Array.Copy(header, 0, chunk, 0, header.Length);
            Array.Copy(shiftJisBytes, offset, chunk, header.Length, Math.Min(availableByteSize, shiftJisBytes.Length - offset));
            textEvents.Add(chunk);
            offset += availableByteSize;
        }

        foreach (var chunk in textEvents)
        {
            var text = Latin1.GetString(chunk);
            events.Add(new TextEvent(text));
        }

        return new TrackChunk(events);
    }

    private static string GenerateTrackText(Track track, long tickPrefix, int measurePrefix,
        Project project, List<FeatureConfig> features)
    {
        var notesLines = new List<string>();
        var lyricsLines = new List<string>();
        var tickList = track.Notes.Select(n => n.TickOn + tickPrefix).ToList();

        for (var i = 0; i < track.Notes.Count; i++)
        {
            var note = track.Notes[i];
            var number = note.Id + 1;
            var numStr = number.ToString().PadLeft(4, '0');

            notesLines.Add($"[ID#{numStr}]");
            notesLines.Add("Type=Anote");
            notesLines.Add($"Length={note.Length}");
            notesLines.Add($"Note#={note.Key}");
            notesLines.Add("Dynamics=64");
            notesLines.Add("PMBendDepth=0");
            notesLines.Add("PMBendLength=0");
            notesLines.Add("PMbPortamentoUse=0");
            notesLines.Add("DEMdecGainRate=0");
            notesLines.Add("DEMaccent=0");
            notesLines.Add($"LyricHandle=h#{numStr}");

            lyricsLines.Add($"[h#{numStr}]");
            var cleanedPhonemes = note.Phoneme?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p.Trim().Length > 0)
                .ToList();
            var phonemes = cleanedPhonemes?.Count > 0 ? cleanedPhonemes : ["a"];
            var phonemesValue = string.Join(" ", phonemes);
            var phonemesCount = phonemes.Count;
            var lockPhonemes = Enumerable.Repeat("0", phonemesCount).ToList();
            if (cleanedPhonemes?.Count > 0)
                lockPhonemes[phonemesCount - 1] = "1";
            var lockPhonemesValue = string.Join(",", lockPhonemes);
            lyricsLines.Add($"L0=\"{note.Lyric}\",\"{phonemesValue}\",0.000000,64,{lockPhonemesValue}");
        }

        var lines = new List<string>
        {
            "[Common]",
            "Version=DSB301",
            $"Name={track.Name}",
            "Color=181,162,123",
            "DynamicsMode=1",
            "PlayMode=1",
        };

        if (track.Id == 0)
        {
            lines.Add("[Master]");
            lines.Add($"PreMeasure={measurePrefix}");
            lines.Add("[Mixer]");
            lines.Add("MasterFeder=0");
            lines.Add("MasterPanpot=0");
            lines.Add("MasterMute=0");
            lines.Add("OutputMode=0");
            lines.Add($"Tracks={project.Tracks.Count}");
            for (var i = 0; i < project.Tracks.Count; i++)
            {
                lines.Add($"Feder{i}=0");
                lines.Add($"Panpot{i}=0");
                lines.Add($"Mute{i}=0");
                lines.Add($"Solo{i}=0");
            }
        }

        lines.Add("[EventList]");
        lines.Add("0=ID#0000");
        for (var i = 0; i < tickList.Count; i++)
            lines.Add($"{tickList[i]}=ID#{(i + 1).ToString().PadLeft(4, '0')}");
        lines.Add($"{track.Notes[^1].TickOff + tickPrefix}=EOS");

        lines.Add("[ID#0000]");
        lines.Add("Type=Singer");
        lines.Add("IconHandle=h#0000");
        lines.AddRange(notesLines);

        lines.Add("[h#0000]");
        lines.Add("IconID=$07010000");
        lines.Add("IDS=Miku");
        lines.Add("Original=0");
        lines.Add("Caption=");
        lines.Add("Length=1");
        lines.Add("Language=0");
        lines.Add("Program=0");
        lines.AddRange(lyricsLines);

        if (features.Contains(Feature.ConvertPitch) && track.Pitch is not null)
            lines.AddRange(GeneratePitchTexts(track.Pitch, tickPrefix, track.Notes));

        return string.Join("\n", lines);
    }

    private static List<string> GeneratePitchTexts(Pitch pitch, long tickPrefix, List<Note> notes)
    {
        var result = new List<string>();
        var pitchRawData = pitch.GenerateForVocaloid(notes);
        if (pitchRawData is null) return result;

        if (pitchRawData.Pit.Count > 0)
        {
            result.Add("[PitchBendBPList]");
            foreach (var evt in pitchRawData.Pit)
                result.Add($"{evt.Pos + tickPrefix}={evt.Value}");
        }

        if (pitchRawData.Pbs.Count > 0)
        {
            result.Add("[PitchBendSensBPList]");
            foreach (var evt in pitchRawData.Pbs)
                result.Add($"{evt.Pos + tickPrefix}={evt.Value}");
        }

        return result;
    }

    private static int Clamp(this int value, int min, int max) =>
        value < min ? min : value > max ? max : value;
}
