using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using UtaFormatix.Core.Models;

namespace UtaFormatix.Core.IO;

internal static class MusicXmlTempo
{
    public static List<Tempo> Parse(List<XElement> measures, List<long> borders, int partIndex)
    {
        var tempos = new List<Tempo>();
        var divisions = 0m;
        for (var i = 0; i < measures.Count; i++)
        {
            var path = $"$.part[{partIndex}].measure[{i}]";
            var position = TickFraction.Zero;
            foreach (var element in measures[i].Elements())
            {
                var at = path + "." + element.Name.LocalName;
                switch (element.Name.LocalName)
                {
                    case "attributes":
                        foreach (var value in element.Elements("divisions"))
                        {
                            divisions = ReadDecimal(value.Value, at + ".divisions");
                            if (divisions <= 0)
                                throw new ProjectInputException("MUSICXML_RANGE", at + ".divisions",
                                    "Divisions must be positive to locate tempo markers.");
                        }
                        break;
                    case "note":
                        if (element.Element("chord") is not null || element.Element("grace") is not null)
                            break;
                        position += Duration(element, divisions, at);
                        break;
                    case "forward":
                        position += Duration(element, divisions, at);
                        break;
                    case "backup":
                        position -= Duration(element, divisions, at);
                        break;
                    case "sound":
                        Append(element, null, position, borders[i], divisions, at, tempos);
                        break;
                    case "direction":
                        var offset = element.Element("offset");
                        var playbackOffset = (string?)offset?.Attribute("sound") == "yes" ? offset : null;
                        foreach (var sound in element.Elements("sound"))
                            Append(sound, playbackOffset, position, borders[i], divisions, at + ".sound", tempos);
                        break;
                }
            }
        }

        // Backup and playback offsets can place later XML elements at earlier ticks.
        var ordered = tempos.OrderBy(tempo => tempo.TickPosition).ToList();
        var result = new List<Tempo>(ordered.Count);
        foreach (var tempo in ordered)
        {
            // The exporter writes each tempo both directly and inside a direction.
            if (result.Count == 0 || result[^1] != tempo)
                result.Add(tempo);
        }
        return result;
    }

    private static TickFraction Duration(XElement element, decimal divisions, string path)
    {
        var value = element.Element("duration")
            ?? throw new ProjectInputException("MUSICXML_REQUIRED", path + ".duration",
                "A duration is required to locate tempo markers.");
        var units = ReadDecimal(value.Value, path + ".duration");
        if (units < 0)
            throw new ProjectInputException("MUSICXML_RANGE", path + ".duration", "Duration cannot be negative.");
        return TickFraction.FromDivisions(units, divisions, path + ".duration");
    }

    private static void Append(XElement sound, XElement? directionOffset, TickFraction position,
        long measureStart, decimal divisions, string path, List<Tempo> tempos)
    {
        if (sound.Attribute("tempo") is not { } attribute) return;
        if (!double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm) ||
            !double.IsFinite(bpm) || bpm <= 0)
            throw new ProjectInputException("MUSICXML_TEMPO", path + ".tempo", "Tempo must be finite and positive.");

        var offset = sound.Element("offset") ?? directionOffset;
        if (offset is not null)
        {
            var units = ReadDecimal(offset.Value, path + ".offset");
            position += TickFraction.FromDivisions(units, divisions, path + ".offset");
        }
        // Add the integral measure position before quantizing, including negative offsets.
        var absolute = position + new TickFraction(measureStart, BigInteger.One);
        var tick = absolute.Numerator / absolute.Denominator;
        if (absolute.Numerator.Sign < 0 || tick > long.MaxValue)
            throw new ProjectInputException("MUSICXML_RANGE", path + ".tempo",
                "Tempo position must fit a non-negative Int64 tick.");
        tempos.Add(new Tempo((long)tick, bpm));
    }

    private static decimal ReadDecimal(string text, string path) => MusicXmlNumbers.Decimal(text, path);

    // Keep cursor arithmetic exact until the tempo is placed on the integral tick axis.
    private readonly record struct TickFraction(BigInteger Numerator, BigInteger Denominator)
    {
        public static TickFraction Zero => new(BigInteger.Zero, BigInteger.One);

        public static TickFraction FromDivisions(decimal units, decimal divisions, string path)
        {
            if (divisions <= 0)
                throw new ProjectInputException("MUSICXML_RANGE", path,
                    "Positive divisions are required to locate tempo markers.");
            var amount = FromDecimal(units);
            var resolution = FromDecimal(divisions);
            return Reduce(amount.Numerator * resolution.Denominator * Constants.TicksInBeat,
                amount.Denominator * resolution.Numerator);
        }

        public static TickFraction operator +(TickFraction left, TickFraction right) =>
            Reduce(left.Numerator * right.Denominator + right.Numerator * left.Denominator,
                left.Denominator * right.Denominator);

        public static TickFraction operator -(TickFraction left, TickFraction right) =>
            left + new TickFraction(-right.Numerator, right.Denominator);

        private static TickFraction FromDecimal(decimal value)
        {
            var bits = decimal.GetBits(value);
            var numerator = (new BigInteger((uint)bits[2]) << 64) +
                            (new BigInteger((uint)bits[1]) << 32) + (uint)bits[0];
            if (value < 0) numerator = -numerator;
            return Reduce(numerator, BigInteger.Pow(10, (bits[3] >> 16) & 0xff));
        }

        private static TickFraction Reduce(BigInteger numerator, BigInteger denominator)
        {
            var factor = BigInteger.GreatestCommonDivisor(numerator, denominator);
            return new TickFraction(numerator / factor, denominator / factor);
        }
    }
}
