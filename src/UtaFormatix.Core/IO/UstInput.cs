using System.Globalization;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;
using UtaFormatix.Core.Process.Pitch;

namespace UtaFormatix.Core.IO;

internal static class UstInput
{
    private static readonly string[] NumericFields =
        ["Length", "Tempo", "NoteNum", "PBS", "PBW", "PBY", "VBR", "Piches", "Pitches", "PitchBend"];
    private static readonly string[] NoteFields =
        ["Length", "Lyric", "NoteNum", "PBS", "PBW", "PBY", "PBM", "VBR", "Piches", "Pitches", "PitchBend"];

    public static Dictionary<int, long> Validate(string[] lines, string filePath, UstPitchBudget budget)
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
            if (section.TrimStart().StartsWith("[#", StringComparison.Ordinal) && !IsSection(section))
                throw new ProjectInputException("UST_SECTION", At(lineNumber, "section"),
                    "Expected a complete section header occupying the whole line: [#...].");
            if (IsSection(section))
            {
                FinishSection();
                noteSection = IsNoteSection(section) ? section : null;
                sectionLine = lineNumber;
                hasLength = false;
            }
            if (noteSection is null && NoteFields.Any(field => line.StartsWith(field + "=", StringComparison.Ordinal)))
                throw new ProjectInputException("UST_SECTION", At(lineNumber, "section"),
                    "Note and pitch fields require a numeric note or rest section.");
            if (line.StartsWith("PBM=", StringComparison.Ordinal))
                CountPitchCells(line[4..], false, At(lineNumber, "PBM"), budget);

            foreach (var field in NumericFields)
            {
                if (!line.StartsWith(field + "=", StringComparison.Ordinal)) continue;
                var value = line[(line.IndexOf('=') + 1)..];
                var path = At(lineNumber, field);
                if (field is "PBS" or "PBW" or "PBY" or "VBR" or "Piches" or "Pitches" or "PitchBend")
                    CountPitchCells(value, field == "PBS", path, budget);
                var cells = field == "PBS" ? value.Split([';', ','])
                    : field is "PBW" or "PBY" or "VBR" or "Piches" or "Pitches" or "PitchBend" ? value.Split(',')
                    : [field == "Tempo" ? value.Replace(',', '.') : value];
                for (var i = 0; i < cells.Length; i++)
                    RejectNonFinite(cells[i], cells.Length > 1 ? path + $"[{i}]" : path);

                if (field == "Tempo" && !string.IsNullOrWhiteSpace(value))
                {
                    if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm))
                        throw new ProjectInputException("UST_INVALID_VALUE", path,
                            "Expected a numeric Tempo; the intended speed cannot be recovered.");
                    ValidateTempo(bpm, path);
                }
                if (field != "Length") continue;
                if (hasLength)
                    throw new ProjectInputException("UST_LENGTH", path,
                        "A note or rest section cannot contain multiple Length fields; the timeline is ambiguous.");
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

    public static bool IsSection(string line) =>
        line.StartsWith("[#", StringComparison.Ordinal) && line.EndsWith(']');

    private static void CountPitchCells(string value, bool semicolon, string path, UstPitchBudget budget)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var count = 1L;
        foreach (var c in value) if (c == ',' || semicolon && c == ';') count++;
        budget.Spend(count, path);
    }

    public static bool IsNoteSection(string section)
    {
        if (!IsSection(section)) return false;
        var index = section.AsSpan(2, section.Length - 3);
        if (index.Length > 0 && (index[0] == '+' || index[0] == '-')) index = index[1..];
        return index.Length > 0 && !index.ContainsAnyExceptInRange('0', '9');
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

    private static void ValidateTempo(double bpm, string path)
    {
        if (!double.IsFinite(bpm))
            throw new ProjectInputException("UST_NUMBER", path, "Tempo must be finite.");
        var secondsPerTick = bpm.BpmToSecPerTick();
        var millisecondsPerTick = 60000.0 / (bpm * Constants.TicksInBeat);
        if (bpm <= 0 || !double.IsFinite(secondsPerTick) || secondsPerTick <= 0
            || !double.IsFinite(secondsPerTick * 1000.0)
            || !double.IsFinite(millisecondsPerTick) || millisecondsPerTick <= 0)
            throw new ProjectInputException("UST_RANGE", path,
                "BPM must be positive and representable in the seconds and milliseconds time mapping.");
    }

    public static void ValidateTempoTimeMapping(List<Tempo> tempos, long endTick, Func<int, string> pathForTempo)
    {
        if (tempos.Count == 0) return;
        void CheckTime(double seconds, int index)
        {
            if (!double.IsFinite(seconds * 1000.0))
                throw new ProjectInputException("UST_RANGE", pathForTempo(index),
                    "BPM produces a non-finite time on this project's tick axis.");
        }

        CheckTime(-(double)tempos[0].TickPosition * tempos[0].Bpm.BpmToSecPerTick(), 0);
        var offset = 0.0;
        for (var i = 1; i < tempos.Count; i++)
        {
            offset += (double)((decimal)tempos[i].TickPosition - tempos[i - 1].TickPosition)
                * tempos[i - 1].Bpm.BpmToSecPerTick();
            CheckTime(offset, i - 1);
        }
        var last = tempos[^1];
        CheckTime(offset + (double)((decimal)Math.Max(endTick, last.TickPosition) - last.TickPosition)
            * last.Bpm.BpmToSecPerTick(), tempos.Count - 1);
    }

    public static void ValidateForExport(Project project)
    {
        if (project.Tempos.Count == 0)
            throw new ProjectInputException("UST_EXPORT", "$.tempos", "At least one tempo is required.");
        for (var i = 0; i < project.Tempos.Count; i++)
        {
            var path = $"$.tempos[{i}].bpm";
            ValidateTempo(project.Tempos[i].Bpm, path);
            if (double.Parse(Ust.FormatTempo(project.Tempos[i].Bpm), CultureInfo.InvariantCulture) <= 0)
                throw new ProjectInputException("UST_RANGE", path,
                    "BPM rounds to zero in UST's two-decimal tempo representation.");
        }
        var endTick = 0L;
        for (var ti = 0; ti < project.Tracks.Count; ti++)
        {
            var track = project.Tracks[ti];
            ValidateText(track.Name, $"$.tracks[{ti}].name");
            var writtenTicks = 0L;
            var previousEnd = 0L;
            for (var ni = 0; ni < track.Notes.Count; ni++)
            {
                var note = track.Notes[ni];
                ValidateText(note.Lyric, $"$.tracks[{ti}].notes[{ni}].lyric");
                if (LyricWhitespace.IsNullOrBlank(note.Lyric) || note.Lyric is "R" or "r")
                    throw new ProjectInputException("UST_LYRIC", $"$.tracks[{ti}].notes[{ni}].lyric",
                        "A sung note requires a non-blank lyric other than the UST rest markers R/r; this lyric would lose the note.");
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
                endTick = Math.Max(endTick, note.TickOff);
            }
            if (track.Pitch is not null)
                for (var pi = 0; pi < track.Pitch.Data.Count; pi++)
                    if (track.Pitch.Data[pi].Value is { } value && !double.IsFinite(value))
                        throw new ProjectInputException("UST_NUMBER", $"$.tracks[{ti}].pitch[{pi}]", "Pitch must be finite or null.");
        }
        ValidateTempoTimeMapping(project.Tempos, endTick, i => $"$.tempos[{i}].bpm");
    }

    private static void ValidateText(string text, string path)
    {
        if (text.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new ProjectInputException("UST_TEXT", path,
                "UST text fields cannot contain CR or LF line breaks; they would create extra fields or sections.");
    }
}
