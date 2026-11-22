using System.Text;

namespace UtaFormatix.Core.IO;

// CP932 has valid duplicate byte mappings (for example EE E0 -> 髙) stored
// in the runtime's best-fit decoder table. DecoderExceptionFallback would
// reject them too. Retain these mappings, but reject the decoder's default
// U+30FB replacement for undefined bytes. A literal ・ (81 45) does not use
// fallback and is preserved.
internal sealed class Cp932DecoderFallback(DecoderFallback mappings) : DecoderFallback
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
