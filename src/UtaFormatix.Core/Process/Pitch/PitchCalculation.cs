using UtaFormatix.Core.Exceptions;
using UtaFormatix.Core.Models;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.Process.Pitch;

internal static class PitchCalculation
{
    public static List<(long Tick, double? Value)>? GetAbsoluteData(
        this PitchModel pitch, List<Note> notes) =>
        ConvertRelativity(pitch, notes, toAbsolute: true);

    public static List<(long Tick, double Value)>? GetRelativeData(
        this PitchModel pitch, List<Note> notes, long borderAppendRadius = 0L) =>
        ConvertRelativity(pitch, notes, toAbsolute: false, borderAppendRadius)?
            .Where(p => p.Value is not null)
            .Select(p => (p.Tick, p.Value!.Value))
            .ToList();

    private static List<(long Tick, double? Value)>? ConvertRelativity(
        PitchModel pitch, List<Note> notes, bool toAbsolute, long borderAppendRadius = 0L)
    {
        if (pitch.IsAbsolute && toAbsolute) return pitch.Data;
        if (!pitch.IsAbsolute && !toAbsolute) return pitch.Data;
        if (notes.Count == 0) return null;

        var borders = GetBorders(notes);
        var index = 0;
        var currentNoteKey = notes[0].Key;
        var nextBorder = borders.Count > 0 ? borders[0] : long.MaxValue;

        var result = pitch.Data.Select(point =>
        {
            while (point.Tick >= nextBorder)
            {
                index++;
                nextBorder = index < borders.Count ? borders[index] : long.MaxValue;
                currentNoteKey = notes[index].Key;
            }

            double? convertedValue = point.Value switch
            {
                not null when pitch.IsAbsolute => point.Value.Value - currentNoteKey,
                not null when !pitch.IsAbsolute => point.Value.Value == 0.0 ? null : point.Value.Value + currentNoteKey,
                null => 0.0,
                _ => point.Value
            };

            return (point.Tick, convertedValue);
        }).ToList();

        if (!toAbsolute)
            result = AppendPointsAtBorders(result, notes, borderAppendRadius);

        return result;
    }

    private static List<long> GetBorders(List<Note> notes)
    {
        var borders = new List<long>();
        var pos = -1L;
        foreach (var note in notes)
        {
            if (pos < 0)
            {
                pos = note.TickOff;
                continue;
            }
            if (pos == note.TickOn)
                borders.Add(pos);
            else if (pos < note.TickOn)
                borders.Add((note.TickOn + pos) / 2);
            else
                throw new NotesOverlappingException();
            pos = note.TickOff;
        }
        return borders;
    }

    private static List<(long Tick, double? Value)> AppendPointsAtBorders(
        List<(long Tick, double? Value)> data, List<Note> notes, long radius)
    {
        if (radius <= 0) return data;

        var result = data.ToList();
        for (var i = 0; i < notes.Count - 1; i++)
        {
            var lastNote = notes[i];
            var thisNote = notes[i + 1];

            if (thisNote.TickOn - lastNote.TickOff > radius) continue;

            var firstPointAtThisNoteIndex = result.FindIndex(p => p.Tick >= thisNote.TickOn);
            if (firstPointAtThisNoteIndex < 0) continue;

            var firstPointAtThisNote = result[firstPointAtThisNoteIndex];
            if (firstPointAtThisNote.Tick == thisNote.TickOn ||
                firstPointAtThisNote.Tick - thisNote.TickOn > radius)
                continue;

            if (firstPointAtThisNote.Value is null) continue;

            var postValue = firstPointAtThisNote.Value.Value;
            var newPointTick = thisNote.TickOn - radius;
            var newPoint = (newPointTick, (double?)postValue);

            result.Insert(firstPointAtThisNoteIndex, newPoint);
            result.RemoveAll(p => p.Tick >= newPointTick && p.Tick < thisNote.TickOn && p != newPoint);
        }
        return result;
    }
}
