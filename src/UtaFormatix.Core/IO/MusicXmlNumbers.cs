using System.Globalization;
using UtaFormatix.Core.Models;

namespace UtaFormatix.Core.IO;

internal static class MusicXmlNumbers
{
    public static int Int32(string text, string path)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ProjectInputException("MUSICXML_NUMBER", path, "Expected an exact Int32 integer.");
        return value;
    }

    public static long PositiveInt64(string text, string path)
    {
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ProjectInputException("MUSICXML_NUMBER", path, "Expected an exact Int64 integer.");
        if (value <= 0)
            throw new ProjectInputException("MUSICXML_RANGE", path, "Value must be positive.");
        return value;
    }

    public static decimal Decimal(string text, string path)
    {
        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint |
                                   NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
        if (!decimal.TryParse(text, style, CultureInfo.InvariantCulture, out var value))
            throw new ProjectInputException("MUSICXML_NUMBER", path, "Expected a finite decimal number.");
        if (CanonicalDecimal(text) != CanonicalDecimal(value.ToString("0.############################", CultureInfo.InvariantCulture)))
            throw new ProjectInputException("MUSICXML_RANGE", path, "Decimal cannot be represented without rounding.");
        return value;
    }

    private static string CanonicalDecimal(string text)
    {
        var span = text.AsSpan().Trim();
        var negative = span[0] == '-';
        if (span[0] is '+' or '-') span = span[1..];
        var point = span.IndexOf('.');
        var whole = (point < 0 ? span : span[..point]).TrimStart('0').ToString();
        var fraction = point < 0 ? "" : span[(point + 1)..].TrimEnd('0').ToString();
        if (whole.Length == 0) whole = "0";
        var sign = negative && (whole != "0" || fraction.Length > 0) ? "-" : "";
        return sign + whole + (fraction.Length == 0 ? "" : "." + fraction);
    }

    public static long Position(Int128 value, string path)
    {
        if (value < 0 || value > long.MaxValue)
            throw new ProjectInputException("MUSICXML_RANGE", path, "Position must fit a non-negative Int64 tick.");
        return (long)value;
    }

    public static long Cursor(long measureStart, Int128 units, long divisions, string path) =>
        Position((Int128)measureStart + units * Constants.TicksInBeat / divisions, path);

    public static long MeasureLength(int numerator, int denominator, long ticksInFullNote, string path)
    {
        if (numerator <= 0 || denominator <= 0)
            throw new ProjectInputException("MUSICXML_RANGE", path, "Time signature components must be positive.");
        var length = Position((Int128)ticksInFullNote * numerator / denominator, path);
        if (length == 0)
            throw new ProjectInputException("MUSICXML_RANGE", path, "Time signature is shorter than one tick.");
        return length;
    }

    public static void ValidateExportAxis(Project project)
    {
        var measure = 0;
        Int128 position = 0;
        var length = (long)Constants.TicksInFullNote * 2;
        for (var i = 0; i < project.TimeSignatures.Count; i++)
        {
            var meter = project.TimeSignatures[i];
            var path = $"$.timeSignatures[{i}]";
            if (meter.MeasurePosition < measure || meter.MeasurePosition >= Array.MaxLength - 1)
                throw new ProjectInputException("MUSICXML_RANGE", path + ".measurePosition",
                    "Time signatures must be ordered within the representable measure axis.");
            position += (Int128)(meter.MeasurePosition - measure) * length;
            _ = Position(position, path + ".measurePosition");
            measure = meter.MeasurePosition;
            length = MeasureLength(meter.Numerator, meter.Denominator, Constants.TicksInFullNote * 2, path);
        }
        var lastTick = 0L;
        foreach (var tempo in project.Tempos) lastTick = Math.Max(lastTick, tempo.TickPosition);
        foreach (var track in project.Tracks)
        foreach (var note in track.Notes) lastTick = Math.Max(lastTick, Math.Max(note.TickOn, note.TickOff));
        var count = position > lastTick ? 0 : ((Int128)lastTick - position) / length;
        if (count >= Array.MaxLength - measure - 1L)
            throw new ProjectInputException("MUSICXML_RANGE", "$.tracks", "Too many measures to represent the export timeline.");
        _ = Position(position + (count + 1) * length, "$.tracks");
    }
}
