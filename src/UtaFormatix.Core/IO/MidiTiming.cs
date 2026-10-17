using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace UtaFormatix.Core.IO;

internal static class MidiTiming
{
    private const long DefaultMicrosecondsPerQuarterNote = 500_000;

    public static bool NormalizeSmpte(MidiFile file)
    {
        if (file.TimeDivision is not SmpteTimeDivision division) return false;
        if (division.Resolution == 0)
            throw new InvalidDataException("SMPTE ticks per frame must be in range 1..255.");

        var (framesNumerator, framesDenominator) = division.Format switch
        {
            SmpteFormat.TwentyFour => (24, 1),
            SmpteFormat.TwentyFive => (25, 1),
            SmpteFormat.ThirtyDrop => (30_000, 1_001),
            SmpteFormat.Thirty => (30, 1),
            _ => throw new InvalidDataException($"Unsupported SMPTE frame rate: {division.Format}."),
        };

        // SMPTE deltas are elapsed time, independent of tempo. Convert absolute
        // positions through the conductor's tempo map, rounding once per event
        // to the project's 480-PPQN grid (never truncate each source delta).
        var numerator = MidiUtil.StandardTimeDivision * 1_000_000m * framesDenominator;
        var denominator = (decimal)framesNumerator * division.Resolution;
        decimal ConvertInterval(long sourceTicks, long microsecondsPerQuarterNote) =>
            sourceTicks * numerator / (denominator * microsecondsPerQuarterNote);

        var tracks = file.GetTrackChunks().ToArray();
        var segments = new List<Segment> { new(0, 0, DefaultMicrosecondsPerQuarterNote) };
        long? firstTempoTick = null;
        long? firstSignatureTick = null;
        long sourceTick = 0;
        if (tracks.Length > 0)
        {
            foreach (var evt in tracks[0].Events)
            {
                sourceTick = checked(sourceTick + evt.DeltaTime);
                if (MidiTimeSignature.TryGet(evt, out _, out _)) firstSignatureTick ??= sourceTick;
                if (evt is not SetTempoEvent tempo) continue;
                firstTempoTick ??= sourceTick;
                var previous = segments[^1];
                var position = previous.ProjectTick +
                    ConvertInterval(sourceTick - previous.SourceTick, previous.MicrosecondsPerQuarterNote);
                var segment = new Segment(sourceTick, position, tempo.MicrosecondsPerQuarterNote);
                if (sourceTick == previous.SourceTick) segments[^1] = segment;
                else segments.Add(segment);
            }
        }

        foreach (var track in tracks)
        {
            sourceTick = 0;
            long previousProjectTick = 0;
            var segmentIndex = 0;
            foreach (var evt in track.Events)
            {
                sourceTick = checked(sourceTick + evt.DeltaTime);
                while (segmentIndex + 1 < segments.Count && segments[segmentIndex + 1].SourceTick <= sourceTick)
                    segmentIndex++;
                var segment = segments[segmentIndex];
                var position = segment.ProjectTick +
                    ConvertInterval(sourceTick - segment.SourceTick, segment.MicrosecondsPerQuarterNote);
                var projectTick = checked((long)decimal.Round(position, 0, MidpointRounding.AwayFromZero));
                evt.DeltaTime = checked(projectTick - previousProjectTick);
                previousProjectTick = projectTick;
            }
        }

        // Explicit defaults are needed when the first real marker is delayed;
        // the common parser otherwise moves that marker back to the start.
        if (tracks.Length > 0)
        {
            if (firstTempoTick > 0)
                tracks[0].Events.Insert(0, new SetTempoEvent(DefaultMicrosecondsPerQuarterNote));
            if (firstSignatureTick > 0)
                tracks[0].Events.Insert(0, new TimeSignatureEvent(4, 4));
        }
        file.TimeDivision = new TicksPerQuarterNoteTimeDivision(MidiUtil.StandardTimeDivision);
        return true;
    }

    private readonly record struct Segment(long SourceTick, decimal ProjectTick, long MicrosecondsPerQuarterNote);
}
