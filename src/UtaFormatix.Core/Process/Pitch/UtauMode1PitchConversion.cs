using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.Process.Pitch;

internal sealed record UtauMode1TrackPitchData(List<UtauMode1NotePitchData?> Notes);

internal sealed record UtauMode1NotePitchData(List<double>? PitchPoints);

internal static class UtauMode1PitchConversion
{
    public static PitchModel? PitchFromUtauMode1Track(
        UtauMode1TrackPitchData? pitchData, List<Note> notes)
    {
        if (pitchData is null) return null;

        var pitchPoints = new List<(long Tick, double Value)>();
        var count = Math.Min(notes.Count, pitchData.Notes.Count);

        for (var i = 0; i < count; i++)
        {
            var note = notes[i];
            var notePitch = pitchData.Notes[i];
            if (notePitch?.PitchPoints is null) continue;

            for (var index = 0; index < notePitch.PitchPoints.Count; index++)
            {
                var tick = note.TickOn + index * UstConstants.Mode1PitchSamplingIntervalTick;
                var value = notePitch.PitchPoints[index] / 100.0;
                pitchPoints.Add((tick, value));
            }
        }

        var absolute = new PitchModel(pitchPoints.Select(p => (p.Tick, (double?)p.Value)).ToList(), false)
            .GetAbsoluteData(notes);
        return absolute is not null ? new PitchModel(absolute, true) : null;
    }

    public static UtauMode1TrackPitchData? PitchToUtauMode1Track(
        PitchModel? pitch, List<Note> notes)
    {
        if (pitch is null) return null;

        var absolutePitch = pitch.GetAbsoluteData(notes);
        if (absolutePitch is null) return null;

        var notePitches = notes.Select(note =>
        {
            var filtered = absolutePitch
                .Where(p => p.Tick >= note.TickOn && p.Tick < note.TickOff)
                .ToList();

            var resampled = filtered.DotResampled(UstConstants.Mode1PitchSamplingIntervalTick);

            var points = resampled
                .Select(p => (p.Value ?? note.Key) - note.Key)
                .Select(v => v * 100)
                .ToList();

            return new UtauMode1NotePitchData(points);
        }).ToList();

        return new UtauMode1TrackPitchData(notePitches);
    }
}
