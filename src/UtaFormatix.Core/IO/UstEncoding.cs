using System.Globalization;
using System.Text;
using UtaFormatix.Core.Models;

namespace UtaFormatix.Core.IO;

internal static class UstEncoding
{
    static UstEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static Encoding Resolve(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ProjectInputException("UST_ENCODING", path, "An encoding name or code page is required.");
        try
        {
            var encoding = int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var codePage)
                ? Encoding.GetEncoding(codePage) : Encoding.GetEncoding(name);
            return encoding.CodePage switch
            {
                65001 => new UTF8Encoding(false, true),
                1200 => new UnicodeEncoding(false, true, true),
                1201 => new UnicodeEncoding(true, true, true),
                12000 => new UTF32Encoding(false, true, true),
                12001 => new UTF32Encoding(true, true, true),
                932 => Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, new Cp932DecoderFallback(encoding.DecoderFallback)),
                _ => Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new ProjectInputException("UST_ENCODING", path, $"Unsupported encoding '{name}'. Specify an encoding name or code page such as utf-8, shift_jis or 932.");
        }
    }

    private static (int CodePage, int Length) GetBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xff, 0xfe, 0x00, 0x00])) return (12000, 4);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xfe, 0xff])) return (12001, 4);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xef, 0xbb, 0xbf])) return (65001, 3);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xff, 0xfe])) return (1200, 2);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xfe, 0xff])) return (1201, 2);
        return (0, 0);
    }

    public static string ReadFile(string filePath, UstImportOptions? options)
    {
        var name = options?.Encoding;
        var automatic = name is null || name.Equals("auto", StringComparison.OrdinalIgnoreCase);
        // Resolve explicit options before opening the source, even for empty files.
        var encoding = automatic ? null : Resolve(name!, "$.ust.encoding");
        var bytes = File.ReadAllBytes(filePath);
        var bom = GetBom(bytes);
        if (bom.Length > 0)
        {
            if (encoding is not null && encoding.CodePage != bom.CodePage)
                throw new ProjectInputException("UST_ENCODING", filePath,
                    $"The BOM declares code page {bom.CodePage}, which conflicts with the selected encoding '{name}'.");
            encoding ??= Resolve(bom.CodePage.ToString(CultureInfo.InvariantCulture), filePath);
        }
        if (encoding is null)
        {
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { encoding = Resolve("shift_jis", filePath); }
        }
        try { return encoding.GetString(bytes.AsSpan(bom.Length)); }
        catch (DecoderFallbackException ex)
        {
            throw new ProjectInputException("UST_ENCODING", filePath,
                $"Invalid {encoding.WebName} data near byte offset {bom.Length + ex.Index}.");
        }
    }

    public static Encoding ForExport(UstExportOptions? options) =>
        Resolve((options ?? new UstExportOptions()).Encoding, "$.ust.encoding");

    public static byte[] Encode(string content, Encoding encoding, int trackIndex)
    {
        try { return [.. encoding.GetPreamble(), .. encoding.GetBytes(content)]; }
        catch (EncoderFallbackException ex)
        {
            throw new ProjectInputException("UST_ENCODING", $"$.tracks[{trackIndex}]",
                $"Text cannot be represented in {encoding.WebName} near character offset {ex.Index}. Choose an encoding such as utf-8 that supports the text.");
        }
    }

    // CP932 has valid duplicate byte mappings (for example EE E0 -> 髙) stored
    // in the runtime's best-fit decoder table. DecoderExceptionFallback would
    // reject them too. Retain these mappings, but reject the decoder's default
    // U+30FB replacement for undefined bytes. A literal ・ (81 45) does not use
    // fallback and is preserved.
    private sealed class Cp932DecoderFallback(DecoderFallback mappings) : DecoderFallback
    {
        public override int MaxCharCount => 1;
        public override DecoderFallbackBuffer CreateFallbackBuffer() => new Buffer(mappings.CreateFallbackBuffer());

        private sealed class Buffer(DecoderFallbackBuffer mappings) : DecoderFallbackBuffer
        {
            private char value;
            private int remaining;

            public override bool Fallback(byte[] bytesUnknown, int index)
            {
                if (remaining != 0) throw new ArgumentException("Recursive decoder fallback.");
                mappings.Fallback(bytesUnknown, index);
                value = mappings.GetNextChar();
                mappings.Reset();
                if (value is '\u30fb' or '\0')
                    throw new DecoderFallbackException("Invalid CP932 byte sequence.", bytesUnknown, index);
                remaining = 1;
                return true;
            }

            public override char GetNextChar()
            {
                if (remaining == 0) return '\0';
                remaining = 0;
                return value;
            }

            public override bool MovePrevious()
            {
                if (remaining != 0 || value == '\0') return false;
                remaining = 1;
                return true;
            }

            public override int Remaining => remaining;
            public override void Reset() { value = '\0'; remaining = 0; mappings.Reset(); }
        }
    }
}
