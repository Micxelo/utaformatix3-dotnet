using UtaFormatix.Core.Models;

namespace UtaFormatix.Core.Process;

public static class NoteShaping
{
    public static List<T> ValidateNotes<T>(this List<T> notes) where T : IRichNote<T>
    {
        if (notes.Count == 0) return notes;

        var sorted = notes.OrderBy(n => n.Note.TickOn).ToList();
        var result = new List<T>();

        for (var i = 0; i < sorted.Count - 1; i++)
        {
            var current = sorted[i];
            var next = sorted[i + 1];
            var newTickOff = Math.Min(current.Note.TickOff, next.Note.TickOn);
            var updated = current.CopyWithNote(current.Note with { TickOff = newTickOff });
            if (updated.Note.Length > 0)
                result.Add(updated);
        }

        result.Add(sorted[^1]);

        return result
            .Select((richNote, index) => richNote.CopyWithNote(richNote.Note with { Id = index }))
            .ToList();
    }

    public static Track ValidateNotes(this Track track) =>
        track with { Notes = track.Notes.ValidateNotes() };

    public static Project FillRests(this Project project, long excludedMaxLength) =>
        project with { Tracks = project.Tracks.Select(t => t.FillRests(excludedMaxLength)).ToList() };

    private static Track FillRests(this Track track, long excludedMaxLength)
    {
        if (track.Notes.Count == 0) return track;

        var notes = track.Notes;
        var newNotes = new List<Note>();

        for (var i = 0; i < notes.Count - 1; i++)
        {
            var note = notes[i];
            var nextNote = notes[i + 1];
            if (nextNote.TickOn - note.TickOff < excludedMaxLength)
                newNotes.Add(note with { TickOff = nextNote.TickOn });
            else
                newNotes.Add(note);
        }

        newNotes.Add(notes[^1]);
        return track with { Notes = newNotes };
    }

    public const int RestsFillingMaxLengthDenominatorDefault = 64;

    public static readonly int[] RestsFillingMaxLengthDenominatorOptions = [8, 16, 32, 64, 128];
}
