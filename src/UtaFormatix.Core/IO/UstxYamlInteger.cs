using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace UtaFormatix.Core.IO;

internal sealed class UstxYamlInteger : INodeDeserializer, INodeTypeResolver
{
    private const string IntegerTag = "tag:yaml.org,2002:int";

    private static bool IsInt32(Type type) => type == typeof(int) || type == typeof(int?);
    private static bool IsInt64(Type type) => type == typeof(long) || type == typeof(long?);
    private static bool IsDouble(Type type) => type == typeof(double) || type == typeof(double?);

    public bool Resolve(NodeEvent? nodeEvent, ref Type currentType) =>
        // Keep the destination type for !!int. YamlDotNet normally resolves
        // this tag to Int32 even when the property is an Int64 coordinate.
        nodeEvent is Scalar scalar && scalar.Tag == IntegerTag &&
        (IsInt32(currentType) || IsInt64(currentType) || IsDouble(currentType));

    public bool Deserialize(IParser parser, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value, ObjectDeserializer rootDeserializer)
    {
        value = null;
        var narrow = IsInt32(expectedType);
        var floating = IsDouble(expectedType);
        if (!(narrow || floating || IsInt64(expectedType)) || !parser.Accept<Scalar>(out var scalar) ||
            UstxInputSchema.IsNull(scalar))
            return false;

        var digits = scalar.Value.AsSpan().Trim(" \t\r\n".AsSpan());
        var negative = !digits.IsEmpty && digits[0] == '-';
        if (!digits.IsEmpty && digits[0] is '+' or '-')
            digits = digits[1..];

        // YAML 1.2 requires an explicit octal prefix. Also retain binary
        // literals and digit separators used by the original js-yaml reader.
        var radix = digits.Length >= 2 && digits[0] == '0'
            ? digits[1] switch { 'o' => 8, 'x' => 16, 'b' => 2, _ => 10 }
            : 10;
        if (floating && scalar.Tag != IntegerTag && (radix == 10 || !scalar.Tag.IsEmpty))
            return false;
        if (radix != 10)
            digits = digits[2..];

        var integer = Parse(digits, radix, negative, narrow);
        if (narrow)
            value = (int)integer;
        else if (floating)
            value = (double)integer;
        else
            value = integer;

        // Parse before advancing so the source diagnostic points at this value.
        parser.MoveNext();
        return true;
    }

    private static long Parse(ReadOnlySpan<char> digits, int radix, bool negative, bool narrow)
    {
        if (digits.IsEmpty || digits[^1] == '_' || (radix == 10 && digits[0] == '_'))
            throw InvalidInteger();

        var limit = (narrow ? (ulong)int.MaxValue : (ulong)long.MaxValue) + (negative ? 1UL : 0);
        ulong magnitude = 0;
        foreach (var character in digits)
        {
            if (character == '_') continue;
            var digit = character switch
            {
                >= '0' and <= '9' => character - '0',
                >= 'a' and <= 'f' => character - 'a' + 10,
                >= 'A' and <= 'F' => character - 'A' + 10,
                _ => -1,
            };
            if (digit < 0 || digit >= radix)
                throw InvalidInteger();
            if (magnitude > (limit - (uint)digit) / (uint)radix)
                throw new OverflowException($"YAML integer is outside the {(narrow ? "Int32" : "Int64")} range.");
            magnitude = magnitude * (uint)radix + (uint)digit;
        }

        if (!negative) return (long)magnitude;
        return magnitude == (1UL << 63) ? long.MinValue : -(long)magnitude;
    }

    private static FormatException InvalidInteger() =>
        new("Expected an integer in decimal, 0o octal, 0x hexadecimal, or 0b binary notation.");
}
