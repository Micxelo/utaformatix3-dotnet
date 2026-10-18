using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UtaFormatix.Core.Models;

namespace UtaFormatix.Core.IO;

internal sealed record SynthVInputOptions(bool Recover, bool AllowNumericStrings, long MaxFileBytes,
    int MaxDepth, int MaxJsonTokens, int MaxPitchSamples)
{
    public static SynthVInputOptions From(SvpImportOptions options) => new(options.Recover, options.AllowNumericStrings,
        options.MaxFileBytes, options.MaxDepth, options.MaxJsonTokens, options.MaxPitchSamples);
    public static SynthVInputOptions From(S5pImportOptions options) => new(options.Recover, options.AllowNumericStrings,
        options.MaxFileBytes, options.MaxDepth, options.MaxJsonTokens, options.MaxPitchSamples);
}

internal abstract class SynthVInput(ImportParams parameters, List<ImportWarning> warnings,
    SynthVInputOptions options, string prefix, Func<string, string, string, ProjectInputException> error)
{
    protected readonly ImportParams parameters = parameters;
    protected readonly List<ImportWarning> warnings = warnings;
    protected readonly SynthVInputOptions options = options;
    protected readonly Dictionary<JsonNode, string> paths = new(ReferenceEqualityComparer.Instance);
    private int tokens;

    public bool Recover => options.Recover;
    public string PathOf(JsonNode node) => paths.GetValueOrDefault(node, node.GetPath());
    public void Warn(string code, string path, string message) =>
        warnings.Add(new ImportWarning.InputDiagnostic(code, path, message));
    protected ProjectInputException Fail(string code, string path, string message) => error($"{prefix}_{code}", path, message);

    protected void Drop(ProjectInputException error, string action)
    {
        if (!Recover) throw error;
        Warn($"{prefix}_RECOVERED", error.FieldPath, $"{action} ({error.Code}: {error.Reason})");
    }

    protected string ReadText(string filePath)
    {
        try { _ = new UTF8Encoding(false, true).GetByteCount(parameters.DefaultLyric); }
        catch (EncoderFallbackException) { throw Fail("UNICODE", "$options.defaultLyric", "Default lyric contains an isolated surrogate."); }
        if (options.MaxFileBytes is <= 0 or > int.MaxValue || options.MaxDepth is < 1 or > 1024 ||
            options.MaxJsonTokens <= 0 || options.MaxPitchSamples <= 0)
            throw Fail("LIMIT", "$", "Limits must be positive; depth must be <= 1024 and file size <= Int32.MaxValue.");
        using var file = File.OpenRead(filePath);
        if (file.Length > options.MaxFileBytes)
            throw Fail("LIMIT", "$", $"File exceeds {options.MaxFileBytes} bytes.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw Fail("LIMIT", "$", "File changed while reading.");
        Encoding encoding = new UTF8Encoding(false, true);
        var start = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); start = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, false, true); start = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); start = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); start = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) start = 3;
        string text;
        try { text = encoding.GetString(bytes, start, bytes.Length - start); }
        catch (DecoderFallbackException ex) { throw Fail("ENCODING", "$", $"Invalid {encoding.WebName} bytes near offset {start + ex.Index}."); }

        return text;
    }

    private sealed class Frame(string path, bool isObject)
    {
        public string Path { get; } = path;
        public bool IsObject { get; } = isObject;
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
        public string Property { get; set; } = "";
        public int Index { get; set; }
    }

    protected void CheckJson(string text, string root)
    {
        var stack = new Stack<Frame>();
        var at = root;
        string NextPath()
        {
            if (stack.Count == 0) return root;
            var frame = stack.Peek();
            return frame.IsObject ? frame.Path + "[\"" + JsonEncodedText.Encode(frame.Property) + "\"]" : $"{frame.Path}[{frame.Index++}]";
        }
        try
        {
            // Enforce the configured depth ourselves so recovery cannot treat
            // a hard resource limit as a skippable syntax error.
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), new JsonReaderOptions { MaxDepth = int.MaxValue });
            while (reader.Read())
            {
                if (tokens >= options.MaxJsonTokens) throw Fail("LIMIT", at, $"Input exceeds {options.MaxJsonTokens} JSON tokens.");
                tokens++;
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        var name = reader.GetString()!;
                        var frame = stack.Peek();
                        at = frame.Path + "[\"" + JsonEncodedText.Encode(name) + "\"]";
                        if (!frame.Names.Add(name)) throw Fail("DUPLICATE_KEY", at, "Duplicate JSON member.");
                        frame.Property = name;
                        break;
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        at = NextPath();
                        if (stack.Count >= options.MaxDepth) throw Fail("LIMIT", at, $"Input exceeds the configured depth of {options.MaxDepth}.");
                        stack.Push(new Frame(at, reader.TokenType == JsonTokenType.StartObject));
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        stack.Pop();
                        break;
                    default:
                        at = NextPath();
                        if (reader.TokenType == JsonTokenType.String) _ = reader.GetString();
                        break;
                }
            }
        }
        catch (JsonException ex) { throw Fail("JSON", at, $"{ex.Message}"); }
        catch (InvalidOperationException ex) { throw Fail("UNICODE", at, ex.Message); }
    }

    protected JsonObject Object(JsonNode? node, string path)
    {
        if (node is not JsonObject obj) throw Fail("TYPE", path, "Expected an object.");
        paths.TryAdd(obj, path);
        return obj;
    }

    protected JsonArray Array(JsonObject owner, string key, string path, bool nullable = false)
    {
        if (!owner.TryGetPropertyValue(key, out var node) || node is null && nullable)
        { var empty = new JsonArray(); owner[key] = empty; return empty; }
        if (node is JsonArray array) return array;
        var error = Fail("TYPE", path + "." + key, "Expected an array; explicit null is not an empty array.");
        Drop(error, "Discarded the invalid collection.");
        var replacement = new JsonArray(); owner[key] = replacement; return replacement;
    }

    protected void Objects(JsonArray array, string path, Action<JsonObject, string> action, Action? onDrop = null)
    {
        for (var i = array.Count - 1; i >= 0; i--)
        {
            var at = $"{path}[{i}]";
            try { action(Object(array[i], at), at); }
            catch (ProjectInputException ex) when (ex.Code != $"{prefix}_LIMIT")
            { Drop(ex, "Discarded this array item."); array.RemoveAt(i); onDrop?.Invoke(); }
        }
    }

    protected string? String(JsonObject owner, string key, string path, bool required = false)
    {
        var node = owner[key];
        if (node is null && !required) return null;
        if (node?.GetValueKind() != JsonValueKind.String) throw Fail("TYPE", path + "." + key, "Expected a string.");
        var value = node.GetValue<string>();
        if (required && string.IsNullOrWhiteSpace(value)) throw Fail("REQUIRED", path + "." + key, "A non-blank identifier is required.");
        return value;
    }

    protected string NumericLiteral(JsonObject owner, string key, string path)
        => NumericLiteral(owner[key], path + "." + key);

    protected string NumericLiteral(JsonNode? node, string path)
    {
        if (node?.GetValueKind() == JsonValueKind.Number) return node.ToJsonString();
        if (node?.GetValueKind() == JsonValueKind.String && (options.AllowNumericStrings || Recover))
        {
            var text = node.GetValue<string>();
            if (text.Length <= 4096)
            {
                try
                {
                    using var parsed = JsonDocument.Parse(text);
                    if (parsed.RootElement.ValueKind == JsonValueKind.Number)
                    { Warn($"{prefix}_COERCED_NUMBER", path, "Read a numeric string using explicit compatibility/recovery mode."); return parsed.RootElement.GetRawText(); }
                }
                catch (JsonException) { }
            }
        }
        throw Fail("NUMBER", path, "Expected a JSON number.");
    }

    protected long Integer(JsonObject owner, string key, string path, bool required = true, long defaultValue = 0, bool nullable = false, bool int32 = false)
    {
        if (!owner.ContainsKey(key) && !required || owner[key] is null && nullable)
        { owner[key] = int32 ? JsonValue.Create(checked((int)defaultValue)) : JsonValue.Create(defaultValue); return defaultValue; }
        var raw = NumericLiteral(owner, key, path);
        if (!TryExactInteger(raw, out var value) || int32 && (value < int.MinValue || value > int.MaxValue))
            throw Fail("INTEGER", path + "." + key, $"Expected an exact {(int32 ? "Int32" : "Int64")} integer within range.");
        owner[key] = int32 ? JsonValue.Create((int)value) : JsonValue.Create(value);
        return value;
    }

    // Work on the decimal literal itself: no double/decimal rounding, and no
    // huge exponent allocations. An integer result has at most 19 digits.
    internal static bool TryExactInteger(string raw, out long value)
    {
        value = 0;
        if (raw.Length > 4096) return false;
        var negative = raw.StartsWith('-');
        var start = negative ? 1 : 0;
        var e = raw.IndexOfAny(['e', 'E']);
        var significand = e < 0 ? raw[start..] : raw[start..e];
        var dot = significand.IndexOf('.');
        var fraction = dot < 0 ? 0 : significand.Length - dot - 1;
        var digits = significand.Replace(".", "").TrimStart('0');
        if (digits.Length == 0) return true;
        var exponent = 0;
        if (e >= 0)
        {
            var p = e + 1;
            var sign = 1;
            if (raw[p] is '+' or '-') { if (raw[p] == '-') sign = -1; p++; }
            for (; p < raw.Length; p++) exponent = Math.Min(8192, exponent * 10 + raw[p] - '0');
            exponent *= sign;
        }
        var shift = exponent - fraction;
        if (shift < 0)
        {
            var remove = -shift;
            if (remove > digits.Length || digits.AsSpan(digits.Length - remove).ContainsAnyExcept('0')) return false;
            digits = digits[..(digits.Length - remove)];
        }
        else
        {
            if (digits.Length + shift > 19) return false;
            digits += new string('0', shift);
        }
        return long.TryParse((negative ? "-" : "") + digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    protected double Number(JsonObject owner, string key, string path)
    {
        var value = Number(owner[key], path + "." + key);
        owner[key] = value;
        return value;
    }

    protected double Number(JsonNode? node, string path)
    {
        double value;
        if (node is JsonValue json && node.GetValueKind() == JsonValueKind.Number && json.TryGetValue<double>(out value))
        {
            if (!double.IsFinite(value)) throw Fail("NUMBER", path, "Expected a finite number.");
            return value;
        }
        var raw = NumericLiteral(node, path);
        if (raw.Length > 4096 || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
            throw Fail("NUMBER", path, "Expected a finite number.");
        return value;
    }

}
