using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace UtaFormatix.Core.IO;

internal static class MusicXmlEncoding
{
    public static readonly Encoding OutputEncoding = new UTF8Encoding(false, true);

    static MusicXmlEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static XDocument ReadDocument(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var (codePage, bomLength) = GetUnicodeHint(bytes);
        var encoding = StrictEncoding(codePage == 0 ? ReadDeclaredEncoding(bytes).CodePage : codePage);
        string text;
        try
        {
            text = encoding.GetString(bytes.AsSpan(bomLength));
        }
        catch (DecoderFallbackException error)
        {
            throw new ProjectInputException("MUSICXML_ENCODING", filePath,
                $"Invalid {encoding.WebName} data near byte offset {bomLength + error.Index}.");
        }

        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null,
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (document.Declaration?.Encoding is { Length: > 0 } declared)
        {
            var matches = declared.Equals("utf-16", StringComparison.OrdinalIgnoreCase)
                ? encoding.CodePage is 1200 or 1201
                : declared.Equals("utf-32", StringComparison.OrdinalIgnoreCase)
                    ? encoding.CodePage is 12000 or 12001
                    : Resolve(declared, filePath).CodePage == encoding.CodePage;
            if (!matches)
                throw new ProjectInputException("MUSICXML_ENCODING", filePath,
                    $"XML encoding '{declared}' conflicts with the detected {encoding.WebName} byte encoding.");
        }
        return document;
    }

    private static Encoding ReadDeclaredEncoding(byte[] bytes)
    {
        // A declaration must be the first token. Without a Unicode byte hint or
        // a declaration, XML uses UTF-8; leave DTD and entity parsing to the load.
        if (!bytes.AsSpan().StartsWith("<?xml"u8)) return OutputEncoding;
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = new XmlTextReader(input)
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };
        reader.Read();
        return reader.Encoding ?? OutputEncoding;
    }

    private static Encoding Resolve(string name, string path)
    {
        try { return Encoding.GetEncoding(name); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new ProjectInputException("MUSICXML_ENCODING", path, $"Unsupported XML encoding '{name}'.");
        }
    }

    private static Encoding StrictEncoding(int codePage) => codePage switch
    {
        65001 => OutputEncoding,
        1200 => new UnicodeEncoding(false, false, true),
        1201 => new UnicodeEncoding(true, false, true),
        12000 => new UTF32Encoding(false, false, true),
        12001 => new UTF32Encoding(true, false, true),
        932 => Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback,
            new Cp932DecoderFallback(Encoding.GetEncoding(932).DecoderFallback)),
        _ => Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
    };

    private static (int CodePage, int BomLength) GetUnicodeHint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xff, 0xfe, 0, 0])) return (12000, 4);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0, 0, 0xfe, 0xff])) return (12001, 4);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xef, 0xbb, 0xbf])) return (65001, 3);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xff, 0xfe])) return (1200, 2);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xfe, 0xff])) return (1201, 2);
        // Unicode XML can also be identified by its opening '<' byte pattern.
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x3c, 0, 0, 0])) return (12000, 0);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0, 0, 0, 0x3c])) return (12001, 0);
        if (bytes.Length >= 4 && bytes[0] == 0x3c && bytes[1] == 0 && bytes[3] == 0) return (1200, 0);
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0x3c && bytes[2] == 0) return (1201, 0);
        return (0, 0);
    }
}
