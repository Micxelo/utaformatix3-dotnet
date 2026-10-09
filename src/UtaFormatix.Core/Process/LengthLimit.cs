using UtaFormatix.Core.Models;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.Process;

internal static class LengthLimit
{
    public static Project LengthLimited(this Project project, long maxLength)
    {
        var tracks = project.Tracks.Select(t => LengthLimitedTrack(t, maxLength)).ToList();
        var tickCounter = new TickCounter();
        var timeSignatures = new List<TimeSignature>();
        foreach (var ts in project.TimeSignatures)
        {
            tickCounter.GoToMeasure(ts);
            if (tickCounter.Tick <= maxLength)
                timeSignatures.Add(ts);
        }
        var tempos = project.Tempos.Where(t => t.TickPosition <= maxLength).ToList();
        return project with { Tracks = tracks, TimeSignatures = timeSignatures, Tempos = tempos };
    }

    private static Track LengthLimitedTrack(Track track, long maxLength)
    {
        var notes = track.Notes
            .Where(n => n.TickOff <= maxLength)
            .Select((n, i) => n with { Id = i })
            .ToList();
        var pitch = track.Pitch is { } p
            ? new PitchModel(p.Data.Where(d => d.Tick <= maxLength).ToList(), p.IsAbsolute)
            : null;
        return track with { Notes = notes, Pitch = pitch };
    }
}
