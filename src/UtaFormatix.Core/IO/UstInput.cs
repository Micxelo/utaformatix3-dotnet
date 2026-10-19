using System.Globalization;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process.Pitch;

namespace UtaFormatix.Core.IO;

internal static class UstInput
{
    private static readonly string[] NumericFields =
        ["Length", "Tempo", "NoteNum", "PBS", "PBW", "PBY", "VBR", "Piches", "Pitches", "PitchBend"];

    public static Dictionary<int, long> Validate(string[] lines, string filePath)
    {
        var lengths = new Dictionary<int, long>();
        var totalTicks = 0L;
        string? noteSection = null;
        var sectionLine = 0;
        var hasLength = false;

        string At(int line, string field) => $"{filePath}:{line + 1} ({field})";
        void FinishSection()
        {
            if (noteSection is not null && !hasLength)
                throw new ProjectInputException("UST_LENGTH", At(sectionLine, noteSection + ".Length"),
                    "A note or rest section requires a positive Length.");
        }

        for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
        {
            var line = lines[lineNumber];
            var section = lineNumber == 0 ? line.TrimStart('\ufeff') : line;
            if (section.StartsWith("[#", StringComparison.Ordinal) && section.EndsWith(']'))
            {
                FinishSection();
                var index = section.AsSpan(2, section.Length - 3);
                if (index.Length > 0 && (index[0] == '+' || index[0] == '-')) index = index[1..];
                noteSection = index.Length > 0 && !index.ContainsAnyExceptInRange('0', '9') ? section : null;
                sectionLine = lineNumber;
                hasLength = false;
            }

            foreach (var field in NumericFields)
            {
                // Use the parser's existing field matching until syntax policy is
                // changed separately; malformed aliases must not bypass validation.
                if (!line.StartsWith(field + "=", StringComparison.CurrentCulture)) continue;
                var value = line[(line.IndexOf('=') + 1)..];
                var path = At(lineNumber, field);
                var cells = field == "PBS" ? value.Split([';', ','])
                    : field is "PBW" or "PBY" or "VBR" or "Piches" or "Pitches" or "PitchBend" ? value.Split(',')
                    : [field == "Tempo" ? value.Replace(',', '.') : value];
                for (var i = 0; i < cells.Length; i++)
                    RejectNonFinite(cells[i], cells.Length > 1 ? path + $"[{i}]" : path);

                if (field != "Length") continue;
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                {
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number <= 0)
                        throw new ProjectInputException("UST_LENGTH", path, "Length must be a positive finite number.");
                    if (number >= 9223372036854775808.0)
                        throw new ProjectInputException("UST_RANGE", path, "Length cannot be represented as an Int64 tick count.");
                    ticks = PitchTickRounding.Round(number);
                }
                if (ticks <= 0)
                    throw new ProjectInputException("UST_LENGTH", path, "Length must be positive and round to at least one tick.");
                try { totalTicks = checked(totalTicks + ticks); }
                catch (OverflowException)
                {
                    throw new ProjectInputException("UST_OVERFLOW", path, "Cumulative note and rest lengths exceed the Int64 tick axis.");
                }
                lengths.Add(lineNumber, ticks);
                hasLength = true;
            }
        }
        FinishSection();
        return lengths;
    }

    private static void RejectNonFinite(string text, string path)
    {
        var token = text.Trim();
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? !double.IsFinite(number)
            : token.TrimStart('+', '-').Equals("inf", StringComparison.OrdinalIgnoreCase)
                || token.TrimStart('+', '-').Equals("infinity", StringComparison.OrdinalIgnoreCase)
                || token.TrimStart('+', '-').Equals("nan", StringComparison.OrdinalIgnoreCase)
                || token.TrimStart('+', '-') == "∞")
            throw new ProjectInputException("UST_NUMBER", path, "Expected a finite number; NaN, infinity and numeric overflow are not supported.");
    }

    public static void ValidateForExport(Project project)
    {
        if (project.Tempos.Count == 0)
            throw new ProjectInputException("UST_EXPORT", "$.tempos", "At least one tempo is required.");
        for (var i = 0; i < project.Tempos.Count; i++)
            if (!double.IsFinite(project.Tempos[i].Bpm))
                throw new ProjectInputException("UST_NUMBER", $"$.tempos[{i}].bpm", "Tempo must be finite.");
        for (var ti = 0; ti < project.Tracks.Count; ti++)
        {
            var track = project.Tracks[ti];
            var writtenTicks = 0L;
            var previousEnd = 0L;
            for (var ni = 0; ni < track.Notes.Count; ni++)
            {
                var note = track.Notes[ni];
                if (note.TickOn < 0 || note.TickOff <= note.TickOn)
                    throw new ProjectInputException("UST_LENGTH", $"$.tracks[{ti}].notes[{ni}]",
                        "Notes must have a non-negative start and a positive duration on the Int64 tick axis.");
                try
                {
                    writtenTicks = checked(writtenTicks + Math.Max(0, note.TickOn - previousEnd));
                    writtenTicks = checked(writtenTicks + note.Length);
                }
                catch (OverflowException)
                {
                    throw new ProjectInputException("UST_OVERFLOW", $"$.tracks[{ti}].notes[{ni}]",
                        "Exported note and rest lengths exceed the Int64 tick axis.");
                }
                previousEnd = note.TickOff;
            }
            if (track.Pitch is not null)
                for (var pi = 0; pi < track.Pitch.Data.Count; pi++)
                    if (track.Pitch.Data[pi].Value is { } value && !double.IsFinite(value))
                        throw new ProjectInputException("UST_NUMBER", $"$.tracks[{ti}].pitch[{pi}]", "Pitch must be finite or null.");
        }
    }
}
