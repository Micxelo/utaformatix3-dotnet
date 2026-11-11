using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace UtaFormatix.Core.IO;

internal sealed class UstxYamlParser(IParser parser, string marker) : IParser
{
    public ParsingEvent? Current { get; private set; }

    public static T Deserialize<T>(IDeserializer deserializer, string text, Func<IParser, IParser>? decorate = null)
    {
        if (!text.AsSpan().ContainsAny('\u0085', '\u2028', '\u2029'))
        {
            var plain = new Parser(new StringReader(text));
            return deserializer.Deserialize<T>(decorate?.Invoke(plain) ?? plain);
        }

        var positions = new List<long>();
        for (var i = 0; i < text.Length; i++)
            if (text[i] is '\u0085' or '\u2028' or '\u2029') positions.Add(i);
        IParser ParseProtected(string prefix) => new Parser(new Scanner(
            new StringReader(Protect(text, prefix)), skipComments: true,
            maxKeySize: (int)Math.Min(int.MaxValue, 1024L + (long)positions.Count * prefix.Length)));

        // YamlDotNet scans these as YAML 1.1 line breaks; js-yaml treats them
        // as YAML 1.2 text. Protect them before scanning, then restore scalars.
        // A separate scan checks decoded values too, so escaped user text can
        // never be mistaken for a marker. No object graph is buffered.
        while (true)
        {
            var candidate = "\ue000" + Guid.NewGuid().ToString("N") + "\ue001";
            var probe = "\ue002" + Guid.NewGuid().ToString("N") + "\ue003";
            if (text.Contains(candidate, StringComparison.Ordinal) || text.Contains(probe, StringComparison.Ordinal)) continue;
            var inspection = ParseProtected(probe);
            var collision = false;
            try
            {
                while (inspection.MoveNext())
                {
                    if (inspection.Current is Scalar scalar && scalar.Value.Contains(candidate, StringComparison.Ordinal))
                    {
                        collision = true;
                        break;
                    }
                }
            }
            catch (YamlException error) { throw Remap(error, positions, probe.Length); }
            if (!collision)
            {
                var preserving = new UstxYamlParser(ParseProtected(candidate), candidate);
                try { return deserializer.Deserialize<T>(decorate?.Invoke(preserving) ?? preserving); }
                catch (YamlException error) { throw Remap(error, positions, candidate.Length); }
            }
        }
    }

    private static YamlException Remap(YamlException error, List<long> positions, int expansion)
    {
        long Index(long value)
        {
            var low = 0;
            var high = positions.Count;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (positions[mid] + (long)mid * expansion <= value) low = mid + 1;
                else high = mid;
            }
            if (low == 0) return value;
            var last = low - 1;
            if (value <= positions[last] + (long)last * expansion + expansion) return positions[last];
            return value - (long)low * expansion;
        }
        Mark Map(Mark mark)
        {
            var index = Index(mark.Index);
            return new Mark(index, mark.Line, index - Index(mark.Index - mark.Column + 1) + 1);
        }
        return new YamlException(Map(error.Start), Map(error.End), error.Message, error);
    }

    private static string Protect(string text, string marker) => text
        .Replace("\u0085", marker + "N", StringComparison.Ordinal)
        .Replace("\u2028", marker + "L", StringComparison.Ordinal)
        .Replace("\u2029", marker + "P", StringComparison.Ordinal);

    public bool MoveNext()
    {
        if (!parser.MoveNext())
        {
            Current = null;
            return false;
        }
        Current = parser.Current;
        if (Current is Scalar scalar && scalar.Value.Contains(marker, StringComparison.Ordinal))
        {
            var value = scalar.Value
                .Replace(marker + "N", "\u0085", StringComparison.Ordinal)
                .Replace(marker + "L", "\u2028", StringComparison.Ordinal)
                .Replace(marker + "P", "\u2029", StringComparison.Ordinal);
            Current = new Scalar(scalar.Anchor, scalar.Tag, value, scalar.Style,
                scalar.IsPlainImplicit, scalar.IsQuotedImplicit, scalar.Start, scalar.End, scalar.IsKey);
        }
        return true;
    }
}
