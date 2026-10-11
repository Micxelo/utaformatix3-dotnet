using System.Text;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;

namespace UtaFormatix.Core.IO;

public static class StandardMid
{
    public static Project Parse(string filePath, ImportParams? importParams = null)
    {
        var parameters = importParams ?? new ImportParams();
        var midiFile = MidiFile.Read(filePath, StandardMidReadingSettings);

        var ticksPerBeat = (midiFile.TimeDivision as TicksPerQuarterNoteTimeDivision)?.TicksPerQuarterNote
            ?? MidiUtil.StandardTimeDivision;

        var tracks = midiFile.GetTrackChunks().ToArray();
        if (tracks.Length == 0)
            throw new Exceptions.CannotReadFileException();

        var warnings = new List<ImportWarning>();
        var (tempos, timeSignatures, tickPrefix) = ParseMasterTrack(tracks[0], ticksPerBeat, 0, warnings);

        var projectTracks = new List<Track>();
        for (var i = 0; i < tracks.Length; i++)
        {
            var track = ParseTrack(tracks[i], i, ticksPerBeat, tickPrefix, parameters);
            projectTracks.Add(track);
        }

        if (projectTracks.Count > 0 && projectTracks[0].Notes.Count == 0)
            projectTracks.RemoveAt(0);

        if (tempos.Count == 0)
            warnings.Add(new ImportWarning.TempoNotFound());

        return new Project(
            Format: Format.StandardMid,
            Name: Path.GetFileNameWithoutExtension(filePath),
            Tracks: projectTracks.Select((t, i) => t with { Id = i }).ToList(),
            TimeSignatures: timeSignatures.Count > 0 ? timeSignatures : [new TimeSignature(0, 4, 4)],
            Tempos: tempos.Count > 0 ? tempos : [new Tempo(0, Constants.DefaultBpm)],
            MeasurePrefix: 0,
            ImportWarnings: warnings);
    }

    public static (byte[] Data, string FileName, List<ExportNotification> Notifications) Generate(
        Project project,
        IEnumerable<FeatureConfig>? features = null)
    {
        var midiFile = GenerateMidiFile(project);
        midiFile.TimeDivision = new TicksPerQuarterNoteTimeDivision(MidiUtil.StandardTimeDivision);
        using var ms = new MemoryStream();
        const MidiFileFormat format = MidiFileFormat.MultiTrack;
        midiFile.Write(ms, format, StandardMidWritingSettings);
        var data = ms.ToArray();
        var fileName = Format.StandardMid.GetFileName(project.Name);
        return (data, fileName, []);
    }

    public static void GenerateFile(Project project, string filePath,
        IEnumerable<FeatureConfig>? features = null)
    {
        var midiFile = GenerateMidiFile(project);
        midiFile.TimeDivision = new TicksPerQuarterNoteTimeDivision(MidiUtil.StandardTimeDivision);
        midiFile.Write(filePath, overwriteFile: true, MidiFileFormat.MultiTrack, StandardMidWritingSettings);
    }

    private static readonly WritingSettings StandardMidWritingSettings = new()
    {
        TextEncoding = Encoding.UTF8,
    };

    private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

    // 读取标准 MIDI 时先按 Latin-1 逐字节解码，保留原始字节；再经
    // EncodingDetector 自动识别编码（UTF-8/Shift_JIS/…），与 Kotlin 的 detect 一致。
    private static readonly ReadingSettings StandardMidReadingSettings = new()
    {
        TextEncoding = Latin1,
    };

    private static string DecodeText(string latin1Text) =>
        EncodingDetector.Decode(Latin1.GetBytes(latin1Text));

    private static (List<Tempo> Tempos, List<TimeSignature> TimeSignatures, long TickPrefix)
        ParseMasterTrack(TrackChunk masterTrack, int timeDivision, int measurePrefix,
            List<ImportWarning> warnings)
    {
        var rawTempos = new List<Tempo>();
        var rawTimeSignatures = new List<TimeSignature>();
        // 整数换算语义（tickRate=1，传入标准 tick），避免浮点误差（见缺陷 4）。
        var tickCounter = new TickCounter();

        long rawTick = 0;
        foreach (var evt in masterTrack.Events)
        {
            rawTick += evt.DeltaTime;
            var tick = MidiUtil.ConvertTickTime(rawTick, timeDivision);

            if (evt is SetTempoEvent tempoEvt)
            {
                var bpm = MidiUtil.ConvertMidiTempoToBpm((long)tempoEvt.MicrosecondsPerQuarterNote);
                rawTempos.Add(new Tempo(tick, bpm));
            }
            else if (evt is TimeSignatureEvent tsEvt)
            {
                tickCounter.GoToTick(tick, tsEvt.Numerator, tsEvt.Denominator);
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

        // 删除前缀内拍号，保留最后一个作为首个（与 Kotlin 的 prefix 折叠一致）
        var timeSignatures = rawTimeSignatures
            .Select(ts => ts with { MeasurePosition = ts.MeasurePosition - measurePrefix })
            .ToList();
        var firstTimeSignatureIndex = timeSignatures
            .Select((ts, i) => (ts, i))
            .Where(x => x.ts.MeasurePosition <= 0)
            .Select(x => (int?)x.i)
            .LastOrDefault() ?? -1;
        for (var i = 0; i < firstTimeSignatureIndex; i++)
        {
            warnings.Add(new ImportWarning.TimeSignatureIgnoredInPreMeasure(timeSignatures[0]));
            timeSignatures.RemoveAt(0);
        }
        if (timeSignatures.Count > 0)
            timeSignatures[0] = timeSignatures[0] with { MeasurePosition = 0 };

        // 删除前缀内 tempo，保留最后一个作为首个（与 Kotlin 的 prefix 折叠一致）
        var tempos = rawTempos
            .Select(t => t with { TickPosition = t.TickPosition - tickPrefix })
            .ToList();
        var firstTempoIndex = tempos
            .Select((t, i) => (t, i))
            .Where(x => x.t.TickPosition <= 0)
            .Select(x => (int?)x.i)
            .LastOrDefault() ?? -1;
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

    private static Track ParseTrack(TrackChunk trackChunk, int trackId, int timeDivision, long tickPrefix,
        ImportParams parameters)
    {
        var trackName = $"Track {trackId + 1}";
        var notes = new List<Note>();
        var pendingLyric = (string?)null;
        (Note Note, byte Channel)? pendingNoteHead = null;
        var pendingNotesWithLyric = new Dictionary<byte, (Note Note, string Lyric)>();
        var defaultLyric = parameters.DefaultLyric;
        var tickPosition = tickPrefix;

        foreach (var evt in trackChunk.Events)
        {
            // 整数换算到标准 480 division，避免浮点误差（见缺陷 4）。
            var delta = MidiUtil.ConvertTickTime(evt.DeltaTime, timeDivision);

            if (delta > 0)
            {
                if (pendingNoteHead is { } noteHead)
                {
                    var lyric = pendingLyric ?? defaultLyric;
                    pendingNotesWithLyric[noteHead.Channel] = (noteHead.Note, lyric);
                    pendingNoteHead = null;
                }

                if (pendingLyric != null)
                {
                    foreach (var (channel, pending) in pendingNotesWithLyric)
                    {
                        var cutNote = pending.Note with
                        {
                            TickOff = tickPosition + delta,
                            Lyric = pending.Lyric,
                        };
                        if (cutNote.Length > 0)
                            notes.Add(cutNote);
                    }
                    pendingNotesWithLyric.Clear();
                }

                tickPosition += delta;
                pendingLyric = null;
            }

            switch (evt)
            {
                case LyricEvent lyricEvt:
                    pendingLyric = DecodeText(lyricEvt.Text);
                    break;

                case TextEvent textEvt when pendingLyric is null:
                    pendingLyric = DecodeText(textEvt.Text);
                    break;

                case SequenceTrackNameEvent nameEvt:
                    trackName = DecodeText(nameEvt.Text);
                    break;

                case NoteOnEvent noteOnEvt when noteOnEvt.Velocity == 0:
                {
                    var channel = (byte)noteOnEvt.Channel;
                    if (pendingNotesWithLyric.TryGetValue(channel, out var pending))
                    {
                        var finalNote = pending.Note with { TickOff = tickPosition, Lyric = pending.Lyric };
                        if (finalNote.Length > 0)
                            notes.Add(finalNote);
                        pendingNotesWithLyric.Remove(channel);
                    }
                    else if (pendingNoteHead is { } head && head.Channel == channel)
                    {
                        var finalNote = head.Note with { TickOff = tickPosition };
                        if (finalNote.Length > 0)
                            notes.Add(finalNote);
                        pendingNoteHead = null;
                    }
                    break;
                }

                case NoteOnEvent noteOnEvt:
                {
                    var note = new Note(
                        Id: 0,
                        Key: noteOnEvt.NoteNumber,
                        Lyric: defaultLyric,
                        TickOn: tickPosition,
                        TickOff: tickPosition);
                    pendingNoteHead = (note, (byte)noteOnEvt.Channel);
                    break;
                }

                case NoteOffEvent noteOffEvt:
                {
                    var channel = (byte)noteOffEvt.Channel;
                    if (pendingNotesWithLyric.TryGetValue(channel, out var pending))
                    {
                        var finalNote = pending.Note with { TickOff = tickPosition, Lyric = pending.Lyric };
                        if (finalNote.Length > 0)
                            notes.Add(finalNote);
                        pendingNotesWithLyric.Remove(channel);
                    }
                    else if (pendingNoteHead is { } head && head.Channel == channel)
                    {
                        var finalNote = head.Note with { TickOff = tickPosition };
                        if (finalNote.Length > 0)
                            notes.Add(finalNote);
                        pendingNoteHead = null;
                    }
                    break;
                }
            }
        }

        var track = new Track(
            Id: 0,
            Name: trackName,
            Notes: notes).ValidateNotes();
        return track;
    }

    private static MidiFile GenerateMidiFile(Project project)
    {
        var masterTrack = GenerateMasterTrack(project);
        var noteTracks = project.Tracks.Select(GenerateTrack).ToArray();

        var tracks = new List<TrackChunk> { masterTrack };
        tracks.AddRange(noteTracks);

        return new MidiFile(tracks.ToArray());
    }

    private static TrackChunk GenerateMasterTrack(Project project)
    {
        var events = new List<MidiEvent>();

        events.Add(new SequenceTrackNameEvent("Master Track"));

        var allEvents = new List<(long Tick, MidiEvent Event)>();

        foreach (var tempo in project.Tempos)
        {
            var usPerBeat = MidiUtil.ConvertBpmToMidiTempo(tempo.Bpm);
            allEvents.Add((tempo.TickPosition, new SetTempoEvent(usPerBeat)));
        }

        foreach (var ts in project.TimeSignatures)
        {
            var tickCounter = new TickCounter();
            tickCounter.GoToMeasure(ts.MeasurePosition, ts.Numerator, ts.Denominator);
            allEvents.Add((tickCounter.OutputTick,
                new TimeSignatureEvent((byte)ts.Numerator, (byte)ts.Denominator)));
        }

        allEvents.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        long lastTick = 0;
        foreach (var (tick, evt) in allEvents)
        {
            if (tick < lastTick) continue;
            evt.DeltaTime = (long)(tick - lastTick);
            events.Add(evt);
            lastTick = tick;
        }

        return new TrackChunk(events);
    }

    private static TrackChunk GenerateTrack(Track track)
    {
        var events = new List<MidiEvent>();

        events.Add(new SequenceTrackNameEvent(track.Name));

        var sortedNotes = track.Notes.OrderBy(n => n.TickOn).ToList();
        var allNoteEvents = new List<(long Tick, MidiEvent Event)>();

        foreach (var note in sortedNotes)
        {
            var lyric = string.IsNullOrWhiteSpace(note.Lyric) ? Constants.DefaultLyric : note.Lyric;
            allNoteEvents.Add((note.TickOn, new LyricEvent(lyric)));
            allNoteEvents.Add((note.TickOn, new NoteOnEvent((SevenBitNumber)note.Key, (SevenBitNumber)127)));
            allNoteEvents.Add((note.TickOff, new NoteOffEvent((SevenBitNumber)note.Key, (SevenBitNumber)0)));
        }

        allNoteEvents.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        long lastTick = 0;
        foreach (var (tick, evt) in allNoteEvents)
        {
            if (tick < lastTick) continue;
            evt.DeltaTime = (long)(tick - lastTick);
            events.Add(evt);
            lastTick = tick;
        }

        return new TrackChunk(events);
    }
}
