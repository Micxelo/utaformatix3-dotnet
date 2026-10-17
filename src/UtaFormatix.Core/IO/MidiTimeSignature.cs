using System.Buffers.Binary;
using System.Numerics;
using Melanchall.DryWetMidi.Core;

namespace UtaFormatix.Core.IO;

internal static class MidiTimeSignature
{
    private const byte StatusByte = 0x58;

    public static EventTypesCollection EventTypes { get; } = new()
    {
        { typeof(WideTimeSignatureEvent), StatusByte },
    };

    public static MidiEvent CreateEvent(int numerator, int denominator)
    {
        if (denominator <= 0 || !BitOperations.IsPow2(denominator))
            throw new ArgumentOutOfRangeException(nameof(denominator), denominator,
                "MIDI time signature denominators must be positive powers of two.");

        return denominator <= 128
            ? new TimeSignatureEvent((byte)numerator, (byte)denominator)
            : new WideTimeSignatureEvent((byte)numerator, denominator);
    }

    public static bool TryGet(MidiEvent evt, out int numerator, out int denominator)
    {
        if (evt is TimeSignatureEvent signature)
        {
            numerator = signature.Numerator;
            denominator = signature.Denominator;
            return true;
        }

        if (evt is WideTimeSignatureEvent wide)
        {
            numerator = wide.Numerator;
            denominator = wide.Denominator;
            return true;
        }

        numerator = denominator = 0;
        return false;
    }

    public static MidiFile ReadFile(string path, ReadingSettings settings)
    {
        // DryWetMidi 8 stores the decoded denominator in a byte, and its custom
        // meta-event API cannot override the built-in 0x58 reader. Temporarily
        // substitute 128 at event boundaries, then restore the original exponent
        // as full-width 0x58 events. All other bytes and read policies stay intact.
        var data = File.ReadAllBytes(path);
        var extended = new List<ExtendedSignature>();
        if (!CollectExtendedSignatures(data, extended))
            extended.Clear(); // Let DryWetMidi diagnose malformed/unsupported input.

        foreach (var signature in extended)
        {
            DecodeDenominator(signature.Exponent);
            data[signature.Offset] = 7;
        }

        using var stream = new MemoryStream(data, writable: false);
        var file = MidiFile.Read(stream, settings);
        var tracks = file.GetTrackChunks().ToArray();
        foreach (var group in extended.GroupBy(s => s.Track))
        {
            if (group.Key >= tracks.Length) continue;
            var originals = group.ToDictionary(s => s.Index);
            var events = tracks[group.Key].Events;
            var index = 0;
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i] is not TimeSignatureEvent signature) continue;
                if (originals.TryGetValue(index++, out var original))
                    events[i] = new WideTimeSignatureEvent(signature.Numerator,
                        DecodeDenominator(original.Exponent), signature.ClocksPerClick,
                        signature.ThirtySecondNotesPerBeat) { DeltaTime = signature.DeltaTime };
            }
        }
        return file;
    }

    private static int DecodeDenominator(byte exponent)
    {
        if (exponent > 30)
            throw new InvalidDataException($"MIDI time signature denominator exponent {exponent} exceeds the supported range 0..30.");
        return 1 << exponent;
    }

    private static bool CollectExtendedSignatures(byte[] data, List<ExtendedSignature> signatures)
    {
        var offset = 0;
        var end = data.Length;
        // Follow DryWetMidi's existing RIFF preamble and size interpretation.
        if (end >= 20 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8))
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4));
            if (size > end - 20) return false;
            offset = 20;
            end = offset + (int)size;
        }

        if (offset > end - 8 || !data.AsSpan(offset, 4).SequenceEqual("MThd"u8)) return false;
        var headerSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
        if (headerSize < 6 || headerSize > end - offset - 8) return false;
        offset += 8 + (int)headerSize;
        var track = 0;
        while (offset <= end - 8)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
            if (size > end - offset - 8) return false;
            var content = offset + 8;
            offset = content + (int)size;
            if (data.AsSpan(content - 8, 4).SequenceEqual("MTrk"u8) &&
                !CollectTrackSignatures(data, content, offset, track++, signatures)) return false;
        }
        return offset == end;
    }

    private static bool CollectTrackSignatures(byte[] data, int offset, int end, int track,
        List<ExtendedSignature> signatures)
    {
        byte runningStatus = 0;
        var signatureIndex = 0;
        while (offset < end)
        {
            if (!ReadVariableLength(data, ref offset, end, out _) || offset == end) return false;
            var status = data[offset];
            if (status >= 0x80) offset++;
            else if (runningStatus != 0) status = runningStatus;
            else return false;

            if (status == 0xFF)
            {
                if (offset == end) return false;
                var type = data[offset++];
                if (!ReadVariableLength(data, ref offset, end, out var size) || size > end - offset) return false;
                if (type == StatusByte)
                {
                    if (size < 2) return false;
                    if (data[offset + 1] > 7)
                        signatures.Add(new ExtendedSignature(track, signatureIndex, offset + 1, data[offset + 1]));
                    signatureIndex++;
                }
                offset += size;
                if (type == 0x2F) return true;
            }
            else if (status is 0xF0 or 0xF7)
            {
                if (!ReadVariableLength(data, ref offset, end, out var size) || size > end - offset) return false;
                offset += size;
            }
            else
            {
                int size;
                if (status < 0xF0)
                {
                    runningStatus = status;
                    size = (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
                }
                else
                {
                    size = status switch { 0xF1 or 0xF3 => 1, 0xF2 => 2, 0xF6 or >= 0xF8 => 0, _ => -1 };
                }
                if (size < 0 || size > end - offset) return false;
                offset += size;
            }
        }
        return true;
    }

    private static bool ReadVariableLength(byte[] data, ref int offset, int end, out int value)
    {
        value = 0;
        for (var i = 0; i < 4 && offset < end; i++)
        {
            var b = data[offset++];
            value = (value << 7) | (b & 0x7F);
            if (b < 0x80) return true;
        }
        return false;
    }

    private readonly record struct ExtendedSignature(int Track, int Index, int Offset, byte Exponent);

    private sealed class WideTimeSignatureEvent(byte numerator, int denominator,
        byte clocksPerClick = 24, byte thirtySecondNotesPerBeat = 8) : MetaEvent
    {
        public byte Numerator { get; private set; } = numerator;
        public int Denominator { get; private set; } = denominator;
        private byte ClocksPerClick { get; set; } = clocksPerClick;
        private byte ThirtySecondNotesPerBeat { get; set; } = thirtySecondNotesPerBeat;

        protected override void ReadContent(MidiReader reader, ReadingSettings settings, int size)
        {
            Numerator = reader.ReadByte();
            Denominator = DecodeDenominator(reader.ReadByte());
            if (size >= 4)
            {
                ClocksPerClick = reader.ReadByte();
                ThirtySecondNotesPerBeat = reader.ReadByte();
            }
        }

        protected override void WriteContent(MidiWriter writer, WritingSettings settings) =>
            writer.WriteBytes([Numerator, (byte)BitOperations.Log2((uint)Denominator),
                ClocksPerClick, ThirtySecondNotesPerBeat]);

        protected override int GetContentSize(WritingSettings settings) => 4;

        protected override MidiEvent CloneEvent() =>
            new WideTimeSignatureEvent(Numerator, Denominator, ClocksPerClick, ThirtySecondNotesPerBeat);
    }
}
