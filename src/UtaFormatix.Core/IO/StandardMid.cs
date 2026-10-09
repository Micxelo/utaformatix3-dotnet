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
        var midiFile = MidiFile.Read(filePath);

        var ticksPerBeat = (midiFile.TimeDivision as TicksPerQuarterNoteTimeDivision)?.TicksPerQuarterNote
            ?? MidiUtil.StandardTimeDivision;
        var tickRate = (double)ticksPerBeat / MidiUtil.StandardTimeDivision;

        var tracks = midiFile.GetTrackChunks().ToArray();
        if (tracks.Length == 0)
            throw new Exceptions.CannotReadFileException();

        var warnings = new List<ImportWarning>();
        var (tempos, timeSignatures, tickPrefix) = ParseMasterTrack(tracks[0], tickRate, 0, warnings);

        var projectTracks = new List<Track>();
        for (var i = 0; i < tracks.Length; i++)
        {
            var track = ParseTrack(tracks[i], tickRate, tickPrefix, parameters);
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
        midiFile.Write(ms, MidiFileFormat.MultiTrack);
        var data = ms.ToArray();
        var fileName = Format.StandardMid.GetFileName(project.Name);
        return (data, fileName, []);
    }

    public static void GenerateFile(Project project, string filePath,
        IEnumerable<FeatureConfig>? features = null)
    {
        var midiFile = GenerateMidiFile(project);
        midiFile.TimeDivision = new TicksPerQuarterNoteTimeDivision(MidiUtil.StandardTimeDivision);
        midiFile.Write(filePath, overwriteFile: true, MidiFileFormat.MultiTrack);
    }

    private static (List<Tempo> Tempos, List<TimeSignature> TimeSignatures, long TickPrefix)
        ParseMasterTrack(TrackChunk masterTrack, double tickRate, int measurePrefix,
            List<ImportWarning> warnings)
    {
        var tempos = new List<Tempo>();
        var timeSignatures = new List<TimeSignature>();
        var tickCounter = new TickCounter(tickRate);

        long rawTick = 0;
        foreach (var evt in masterTrack.Events)
        {
            rawTick += evt.DeltaTime;

            if (evt is SetTempoEvent tempoEvt)
            {
                var tick = (long)(rawTick / tickRate);
                var bpm = MidiUtil.ConvertMidiTempoToBpm((long)tempoEvt.MicrosecondsPerQuarterNote);
                tempos.Add(new Tempo(tick, bpm));
            }
            else if (evt is TimeSignatureEvent tsEvt)
            {
                tickCounter.GoToTick(rawTick, tsEvt.Numerator, tsEvt.Denominator);
                timeSignatures.Add(new TimeSignature(tickCounter.Measure, tsEvt.Numerator, tsEvt.Denominator));
            }
        }

        var tickPrefix = tickCounter.Tick;
        if (measurePrefix > 0)
        {
            tickCounter.GoToMeasure(0);
            tickPrefix = tickCounter.Tick;
        }

        var shiftedTempos = tempos
            .Select(t => t with { TickPosition = t.TickPosition - tickPrefix })
            .Where(t => t.TickPosition >= 0)
            .ToList();

        var shiftedTimeSignatures = timeSignatures
            .Where(ts => ts.MeasurePosition >= measurePrefix)
            .Select(ts => ts with { MeasurePosition = ts.MeasurePosition - measurePrefix })
            .ToList();

        if (shiftedTimeSignatures.Count == 0 || shiftedTimeSignatures[0].MeasurePosition != 0)
            shiftedTimeSignatures.Insert(0, new TimeSignature(0, 4, 4));

        if (shiftedTempos.Count == 0)
            warnings.Add(new ImportWarning.TempoNotFound());

        return (shiftedTempos, shiftedTimeSignatures, tickPrefix);
    }

    private static Track ParseTrack(TrackChunk trackChunk, double tickRate, long tickPrefix,
        ImportParams parameters)
    {
        var trackName = "Track";
        var notes = new List<Note>();
        var pendingLyric = (string?)null;
        (Note Note, byte Channel)? pendingNoteHead = null;
        var pendingNotesWithLyric = new Dictionary<byte, (Note Note, string Lyric)>();
        var defaultLyric = parameters.DefaultLyric;
        var tickPosition = tickPrefix;

        foreach (var evt in trackChunk.Events)
        {
            var delta = evt.DeltaTime;

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
                        var cutNote = pending.Note with { TickOff = tickPosition + (long)(delta / tickRate) };
                        if (cutNote.Length > 0)
                            notes.Add(cutNote);
                    }
                    pendingNotesWithLyric.Clear();
                }

                tickPosition += (long)(delta / tickRate);
                pendingLyric = null;
            }

            switch (evt)
            {
                case LyricEvent lyricEvt:
                    pendingLyric = lyricEvt.Text;
                    break;

                case TextEvent textEvt when pendingLyric is null:
                    pendingLyric = textEvt.Text;
                    break;

                case SequenceTrackNameEvent nameEvt:
                    trackName = nameEvt.Text;
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
