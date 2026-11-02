using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.Process.Pitch;

internal sealed record UtauMode1TrackPitchData(List<UtauMode1NotePitchData?> Notes);

internal sealed record UtauMode1NotePitchData(List<double>? PitchPoints);

internal static class UtauMode1PitchConversion
{
    public static PitchModel? PitchFromUtauMode1Track(
        UtauMode1TrackPitchData? pitchData, List<Note> notes, UstPitchContext? context = null)
    {
        if (pitchData is null) return null;

        var pitchPoints = new List<(long Tick, double Value)>();
        var count = Math.Min(notes.Count, pitchData.Notes.Count);

        for (var i = 0; i < count; i++)
        {
            var note = notes[i];
            var notePitch = pitchData.Notes[i];
            if (notePitch?.PitchPoints is null) continue;
            context?.AtNote(i);
            context?.Source(notePitch.PitchPoints.Count, "PitchBend");
            if (notePitch.PitchPoints.Count > 0)
                context?.Add(note.TickOn, (notePitch.PitchPoints.Count - 1L) * UstConstants.Mode1PitchSamplingIntervalTick, "PitchBend");
            if (context?.ValidateOnly == true) continue;

            for (var index = 0; index < notePitch.PitchPoints.Count; index++)
            {
                var tick = context?.Add(note.TickOn, index * UstConstants.Mode1PitchSamplingIntervalTick, "PitchBend")
                    ?? note.TickOn + index * UstConstants.Mode1PitchSamplingIntervalTick;
                var value = notePitch.PitchPoints[index] / 100.0;
                context?.Finite(value, "PitchBend");
                pitchPoints.Add((tick, value));
            }
        }

        if (context?.ValidateOnly == true) return null;
        var relative = new PitchModel(pitchPoints.Select(p => (p.Tick, (double?)p.Value)).ToList(), false);
        var absolute = context is null ? relative.GetAbsoluteData(notes) : context.Absolute(relative, notes);
        return absolute is not null ? new PitchModel(absolute, true) : null;
    }

    public static UtauMode1TrackPitchData? PitchToUtauMode1Track(
        PitchModel? pitch, List<Note> notes, UstPitchContext? context = null)
    {
        if (pitch is null) return null;

        var absolutePitch = context is null ? pitch.GetAbsoluteData(notes) : context.Absolute(pitch, notes);
        if (absolutePitch is null) return null;

        var notePitches = notes.Select((note, index) =>
        {
            context?.AtNote(index);
            var filtered = absolutePitch
                .Where(p => p.Tick >= note.TickOn && p.Tick < note.TickOff)
                .ToList();

            var resampled = context is null ? filtered.DotResampled(UstConstants.Mode1PitchSamplingIntervalTick)
                : context.Resample(filtered, UstConstants.Mode1PitchSamplingIntervalTick);

            var points = resampled
                .Select(p => (p.Value ?? note.Key) - note.Key)
                .Select(v => v * 100)
                .ToList();
            if (context is not null)
                foreach (var value in points)
                {
                    context.Finite(value, "PitchBend");
                    if (Math.Truncate(value) < int.MinValue || Math.Truncate(value) > int.MaxValue)
                        throw context.Range("PitchBend");
                }

            return new UtauMode1NotePitchData(points);
        }).ToList();

        return new UtauMode1TrackPitchData(notePitches.Cast<UtauMode1NotePitchData?>().ToList());
    }
}
