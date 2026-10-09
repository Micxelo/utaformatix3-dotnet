namespace UtaFormatix.Core.Models;

public sealed record Project(
    Format Format,
    string Name,
    List<Track> Tracks,
    List<TimeSignature> TimeSignatures,
    List<Tempo> Tempos,
    int MeasurePrefix,
    List<ImportWarning> ImportWarnings,
    JapaneseLyricsType JapaneseLyricsType = JapaneseLyricsType.Unknown)
{
    public bool HasXSampaData =>
        Tracks.Any(t => t.Notes.Any(n => n.Phoneme != null));

    public Project WithoutEmptyTracks()
    {
        var filtered = Tracks
            .Where(t => t.Notes.Count > 0)
            .Select((t, i) => t with { Id = i })
            .ToList();
        return filtered.Count == 0 ? this with { Tracks = filtered } : this with { Tracks = filtered };
    }

    public Project Validate()
    {
        foreach (var (track, index) in Tracks.Select((t, i) => (t, i)))
        {
            var firstNote = track.Notes.FirstOrDefault();
            if (firstNote != null && firstNote.TickOn < 0)
                throw new Exceptions.IllegalNotePositionException(firstNote, index);
        }
        return this;
    }
}
