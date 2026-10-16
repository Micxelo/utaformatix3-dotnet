using System.Text;

namespace UtaFormatix.Core.IO;

public sealed class ValueTree
{
    public string Type { get; set; } = "";
    public Dictionary<string, ValueTreeValue> Attributes { get; set; } = new();
    public List<ValueTree> Children { get; set; } = [];
}

public sealed class ValueTreeValue
{
    public ValueTreeValueType ValueType { get; set; }
    public object? Value { get; set; }

    public int AsInt() => Convert.ToInt32(Value);
    public double AsDouble() => Convert.ToDouble(Value);
    public string AsString() => (string)Value!;
    public bool AsBool() => ValueType == ValueTreeValueType.BoolTrue;
    public byte[] AsBinary() => (byte[])Value!;
}

public enum ValueTreeValueType
{
    Int = 1,
    BoolTrue = 2,
    BoolFalse = 3,
    Double = 4,
    String = 5,
    Int64 = 6,
    Array = 7,
    Binary = 8,
    Undefined = 9,
}

public static class ValueTreeBinary
{
    public static ValueTree Parse(byte[] data)
    {
        using var stream = new MemoryStream(data);
        return ReadNode(stream);
    }

    public static byte[] Dump(ValueTree tree)
    {
        using var stream = new MemoryStream();
        WriteNode(stream, tree);
        return stream.ToArray();
    }

    private static ValueTree ReadNode(Stream stream)
    {
        var node = new ValueTree
        {
            Type = ReadString(stream),
        };

        var numAttributes = ReadCompressedInt(stream);
        for (var i = 0; i < numAttributes; i++)
        {
            var key = ReadString(stream);
            var value = ReadVariant(stream);
            node.Attributes[key] = value;
        }

        var numChildren = ReadCompressedInt(stream);
        for (var i = 0; i < numChildren; i++)
        {
            node.Children.Add(ReadNode(stream));
        }

        return node;
    }

    private static void WriteNode(Stream stream, ValueTree node)
    {
        WriteString(stream, node.Type);

        WriteCompressedInt(stream, node.Attributes.Count);
        foreach (var (key, value) in node.Attributes)
        {
            WriteString(stream, key);
            WriteVariant(stream, value);
        }

        WriteCompressedInt(stream, node.Children.Count);
        foreach (var child in node.Children)
        {
            WriteNode(stream, child);
        }
    }

    private static ValueTreeValue ReadVariant(Stream stream)
    {
        var numBytes = ReadCompressedInt(stream);
        var typeTag = stream.ReadByte();
        var payloadSize = numBytes - 1;

        var result = new ValueTreeValue();

        switch (typeTag)
        {
            case 1: // int
            {
                result.ValueType = ValueTreeValueType.Int;
                result.Value = ReadInt32LE(stream);
                break;
            }
            case 2: // boolTrue
            {
                result.ValueType = ValueTreeValueType.BoolTrue;
                result.Value = true;
                break;
            }
            case 3: // boolFalse
            {
                result.ValueType = ValueTreeValueType.BoolFalse;
                result.Value = false;
                break;
            }
            case 4: // double
            {
                result.ValueType = ValueTreeValueType.Double;
                result.Value = ReadFloat64LE(stream);
                break;
            }
            case 5: // string
            {
                result.ValueType = ValueTreeValueType.String;
                result.Value = ReadString(stream);
                break;
            }
            case 6: // int64
            {
                result.ValueType = ValueTreeValueType.Int64;
                result.Value = ReadInt64LE(stream);
                break;
            }
            case 7: // array
            {
                result.ValueType = ValueTreeValueType.Array;
                var count = ReadCompressedInt(stream);
                var items = new List<ValueTreeValue>(count);
                for (var i = 0; i < count; i++)
                    items.Add(ReadVariant(stream));
                result.Value = items;
                break;
            }
            case 8: // binary
            {
                result.ValueType = ValueTreeValueType.Binary;
                var buf = new byte[payloadSize];
                ReadExact(stream, buf, 0, payloadSize);
                result.Value = buf;
                break;
            }
            case 9: // undefined
            {
                result.ValueType = ValueTreeValueType.Undefined;
                result.Value = null;
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown ValueTree type tag: {typeTag}");
        }

        return result;
    }

    private static void WriteVariant(Stream stream, ValueTreeValue value)
    {
        var payload = BuildPayload(value);
        WriteCompressedInt(stream, 1 + payload.Length);
        stream.WriteByte((byte)value.ValueType);
        stream.Write(payload, 0, payload.Length);
    }

    private static byte[] BuildPayload(ValueTreeValue value)
    {
        switch (value.ValueType)
        {
            case ValueTreeValueType.Int:
            {
                var v = Convert.ToInt32(value.Value);
                return BitConverter.GetBytes(v);
            }
            case ValueTreeValueType.BoolTrue:
            case ValueTreeValueType.BoolFalse:
                return [];
            case ValueTreeValueType.Double:
            {
                var v = Convert.ToDouble(value.Value);
                return BitConverter.GetBytes(v);
            }
            case ValueTreeValueType.String:
            {
                var s = (string)value.Value!;
                var bytes = Encoding.UTF8.GetBytes(s);
                var result = new byte[bytes.Length + 1];
                Array.Copy(bytes, result, bytes.Length);
                result[^1] = 0;
                return result;
            }
            case ValueTreeValueType.Int64:
            {
                var v = Convert.ToInt64(value.Value);
                return BitConverter.GetBytes(v);
            }
            case ValueTreeValueType.Array:
            {
                using var ms = new MemoryStream();
                var items = (List<ValueTreeValue>)value.Value!;
                WriteCompressedInt(ms, items.Count);
                foreach (var item in items)
                    WriteVariant(ms, item);
                return ms.ToArray();
            }
            case ValueTreeValueType.Binary:
            {
                return (byte[])value.Value!;
            }
            case ValueTreeValueType.Undefined:
                return [];
            default:
                throw new InvalidOperationException($"Unknown ValueTree value type: {value.ValueType}");
        }
    }

    private static string ReadString(Stream stream)
    {
        var bytes = new List<byte>();
        int b;
        while ((b = stream.ReadByte()) > 0)
            bytes.Add((byte)b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0);
    }

    private static int ReadCompressedInt(Stream stream)
    {
        var numBytes = stream.ReadByte();
        if (numBytes == 0) return 0;
        var result = 0;
        for (var i = 0; i < numBytes; i++)
            result |= stream.ReadByte() << (i * 8);
        return result;
    }

    private static void WriteCompressedInt(Stream stream, int value)
    {
        if (value == 0)
        {
            stream.WriteByte(0);
            return;
        }

        var bytes = new List<byte>();
        var v = value;
        while (v > 0)
        {
            bytes.Add((byte)(v & 0xFF));
            v >>= 8;
        }

        stream.WriteByte((byte)bytes.Count);
        foreach (var b in bytes)
            stream.WriteByte(b);
    }

    private static int ReadInt32LE(Stream stream)
    {
        Span<byte> buf = stackalloc byte[4];
        ReadExact(stream, buf);
        return BitConverter.ToInt32(buf);
    }

    private static long ReadInt64LE(Stream stream)
    {
        Span<byte> buf = stackalloc byte[8];
        ReadExact(stream, buf);
        return BitConverter.ToInt64(buf);
    }

    private static double ReadFloat64LE(Stream stream)
    {
        Span<byte> buf = stackalloc byte[8];
        ReadExact(stream, buf);
        return BitConverter.ToDouble(buf);
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0) throw new EndOfStreamException();
            totalRead += read;
        }
    }
}
