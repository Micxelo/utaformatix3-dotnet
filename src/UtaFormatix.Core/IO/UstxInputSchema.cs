using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace UtaFormatix.Core.IO;

// Validate presence and container shapes before static deserialization supplies
// CLR defaults. Frames are bounded by YAML depth, not by the number of points.
internal sealed class UstxInputSchema(IParser parser, UstxInput input) : IParser
{
    private enum ObjectKind { None, Project, Track, Part, Note, Pitch, Point, Vibrato, Curve, Tempo, Meter }
    private enum Kind { Ignore, Scalar, Mapping, Sequence }
    private sealed record Rule(Kind Kind, ObjectKind Object = ObjectKind.None,
        bool Required = false, bool Nullable = false, bool Default = false);
    private sealed class Frame(string path, Rule rule)
    {
        public string Path { get; } = path;
        public Rule Rule { get; } = rule;
        public HashSet<string> Fields { get; } = new(StringComparer.Ordinal);
        public string? Key { get; set; }
        public int Index { get; set; }
    }
    private readonly record struct Alias(Rule Rule, bool Null);
    private readonly Stack<Frame> frames = [];
    private readonly Dictionary<AnchorName, Alias> anchors = [];
    private int ignoredDepth;
    private int documents;
    private bool rootSeen;
    private bool rootComplete;
    private string lastParent = "$";
    private string? lastKey;
    private int lastIndex = -1;
    public string LastPath => lastKey is not null ? lastParent + "." + lastKey
        : lastIndex >= 0 ? $"{lastParent}[{lastIndex}]" : lastParent;
    public ParsingEvent? Current { get; private set; }

    private static Rule Scalar(bool required = false, bool nullable = false, bool defaults = false) =>
        new(Kind.Scalar, Required: required, Nullable: nullable, Default: defaults);
    private static Rule Map(ObjectKind kind, bool defaults = false) => new(Kind.Mapping, kind, Default: defaults, Nullable: defaults);
    private static Rule Array(ObjectKind kind = ObjectKind.None, bool required = false, bool nullable = false) =>
        new(Kind.Sequence, kind, Required: required, Nullable: nullable);
    private static readonly Rule Ignore = new(Kind.Ignore, Nullable: true);
    private static readonly Dictionary<ObjectKind, Dictionary<string, Rule>> Rules = new()
    {
        [ObjectKind.Project] = new(StringComparer.Ordinal)
        {
            ["name"] = Scalar(nullable: true, defaults: true),
            ["tracks"] = Array(ObjectKind.Track, required: true), ["voice_parts"] = Array(ObjectKind.Part, required: true),
            ["tempos"] = Array(ObjectKind.Tempo, nullable: true), ["time_signatures"] = Array(ObjectKind.Meter, nullable: true),
            ["bpm"] = Scalar(nullable: true), ["beat_per_bar"] = Scalar(nullable: true),
            ["beat_unit"] = Scalar(nullable: true), ["resolution"] = Scalar(nullable: true),
            ["comment"] = Ignore, ["output_dir"] = Ignore, ["cache_dir"] = Ignore,
            ["ustx_version"] = Ignore, ["expressions"] = Ignore,
        },
        [ObjectKind.Track] = new(StringComparer.Ordinal)
        {
            ["track_name"] = Scalar(nullable: true), ["phonemizer"] = Ignore,
            ["mute"] = Ignore, ["solo"] = Ignore, ["volume"] = Ignore,
        },
        [ObjectKind.Part] = new(StringComparer.Ordinal)
        {
            ["track_no"] = Scalar(required: true), ["position"] = Scalar(required: true),
            ["notes"] = Array(ObjectKind.Note, required: true), ["curves"] = Array(ObjectKind.Curve, nullable: true),
            ["name"] = Ignore, ["comment"] = Ignore,
        },
        [ObjectKind.Note] = new(StringComparer.Ordinal)
        {
            ["position"] = Scalar(required: true), ["duration"] = Scalar(required: true),
            ["tone"] = Scalar(required: true), ["lyric"] = Scalar(required: true), ["tuning"] = Scalar(),
            ["pitch"] = Map(ObjectKind.Pitch, defaults: true), ["vibrato"] = Map(ObjectKind.Vibrato, defaults: true),
        },
        [ObjectKind.Pitch] = new(StringComparer.Ordinal)
        { ["data"] = Array(ObjectKind.Point, required: true), ["snap_first"] = Scalar() },
        [ObjectKind.Point] = new(StringComparer.Ordinal)
        { ["x"] = Scalar(required: true), ["y"] = Scalar(required: true), ["shape"] = Scalar(nullable: true, defaults: true) },
        [ObjectKind.Vibrato] = new(StringComparer.Ordinal)
        {
            ["length"] = Scalar(required: true), ["period"] = Scalar(nullable: true), ["depth"] = Scalar(nullable: true),
            ["in"] = Scalar(nullable: true), ["out"] = Scalar(nullable: true),
            ["shift"] = Scalar(nullable: true), ["drift"] = Scalar(nullable: true),
        },
        [ObjectKind.Curve] = new(StringComparer.Ordinal)
        { ["abbr"] = Scalar(required: true), ["xs"] = Array(required: true), ["ys"] = Array(required: true) },
        [ObjectKind.Tempo] = new(StringComparer.Ordinal)
        { ["position"] = Scalar(required: true), ["bpm"] = Scalar(required: true) },
        [ObjectKind.Meter] = new(StringComparer.Ordinal)
        { ["bar_position"] = Scalar(required: true), ["beat_per_bar"] = Scalar(required: true), ["beat_unit"] = Scalar(required: true) },
    };

    internal static bool IsNull(Scalar scalar) =>
        scalar.Tag == "tag:yaml.org,2002:null" ||
        scalar.Tag.IsEmpty && scalar.Style == ScalarStyle.Plain &&
        (scalar.Value.Length == 0 || scalar.Value == "~" || scalar.Value.Equals("null", StringComparison.OrdinalIgnoreCase));

    private void CompleteValue()
    {
        if (frames.Count == 0) { rootComplete = true; return; }
        var parent = frames.Peek();
        if (parent.Rule.Kind == Kind.Sequence) parent.Index++;
        else parent.Key = null;
    }

    private void Remember(AnchorName anchor, Rule rule, bool isNull = false)
    {
        if (!anchor.IsEmpty) anchors[anchor] = new Alias(rule, isNull);
    }

    public bool MoveNext()
    {
        if (!parser.MoveNext()) { Current = null; return false; }
        Current = parser.Current;
        // YamlDotNet's null resolver checks plain spelling even for !!str.
        // Quoted style preserves explicitly tagged strings such as !!str null.
        if (Current is YamlDotNet.Core.Events.Scalar tagged && tagged.Style == ScalarStyle.Plain &&
            (tagged.Tag == "tag:yaml.org,2002:str" || tagged.Tag == "!"))
            Current = new YamlDotNet.Core.Events.Scalar(tagged.Anchor, tagged.Tag, tagged.Value,
                ScalarStyle.DoubleQuoted, tagged.IsPlainImplicit, tagged.IsQuotedImplicit,
                tagged.Start, tagged.End, tagged.IsKey);
        var item = Current!;
        if (item is DocumentStart && ++documents > 1)
            throw input.Error("USTX_STRUCTURE", "$", "Expected one YAML document; additional documents cannot be silently ignored.");
        if (item is StreamStart or StreamEnd or DocumentStart or DocumentEnd or Comment) return true;
        if (ignoredDepth > 0)
        {
            if (item is NodeEvent node)
                Remember(node.Anchor, item is MappingStart ? Map(ObjectKind.None) : item is SequenceStart ? Array() : Scalar(), item is YamlDotNet.Core.Events.Scalar s && IsNull(s));
            if (item is MappingStart or SequenceStart) ignoredDepth++;
            else if (item is MappingEnd or SequenceEnd && --ignoredDepth == 0) CompleteValue();
            return true;
        }
        if (item is MappingEnd or SequenceEnd)
        {
            var frame = frames.Pop();
            if (frame.Rule.Kind == Kind.Mapping)
                foreach (var (key, rule) in Rules[frame.Rule.Object])
                    if (!frame.Fields.Contains(key))
                    {
                        var path = frame.Path + "." + key;
                        if (rule.Required) throw input.Error("USTX_REQUIRED", path, "Required field is missing; its intended value cannot be inferred.");
                        input.RecordMissing(path, rule.Default);
                    }
            CompleteValue();
            return true;
        }
        if (frames.TryPeek(out var map) && map.Rule.Kind == Kind.Mapping && map.Key is null)
        {
            if (item is not YamlDotNet.Core.Events.Scalar key)
                throw input.Error("USTX_STRUCTURE", map.Path, "Expected a scalar mapping key.");
            map.Key = key.Value;
            if (key.Value == "<<" && (key.Style == ScalarStyle.Plain || key.Tag == "tag:yaml.org,2002:merge"))
                throw input.Error("USTX_MERGE", map.Path + ".<<", "YAML merge keys are not supported; expand them before import.");
            if (Rules[map.Rule.Object].ContainsKey(key.Value) && !map.Fields.Add(key.Value))
                throw input.Error("USTX_DUPLICATE", map.Path + "." + key.Value, "Duplicate fields are ambiguous and cannot be silently overwritten.");
            Remember(key.Anchor, Scalar(), IsNull(key));
            return true;
        }

        Rule expected;
        if (frames.Count == 0)
        {
            if (rootSeen) throw input.Error("USTX_STRUCTURE", "$", "Expected one root project mapping.");
            rootSeen = true; expected = Map(ObjectKind.Project);
            lastParent = "$"; lastKey = null; lastIndex = -1;
        }
        else
        {
            var parent = frames.Peek(); lastParent = parent.Path;
            lastKey = parent.Key; lastIndex = parent.Rule.Kind == Kind.Sequence ? parent.Index : -1;
            expected = parent.Rule.Kind == Kind.Sequence
                ? parent.Rule.Object == ObjectKind.None ? Scalar() : Map(parent.Rule.Object)
                : Rules[parent.Rule.Object].GetValueOrDefault(parent.Key!, Ignore);
        }
        if (item is AnchorAlias alias)
        {
            if (!anchors.TryGetValue(alias.Value, out var definition))
                throw input.Error("USTX_ALIAS", LastPath, "Alias must refer to an earlier validated value.");
            if (definition.Null)
            {
                if (!expected.Nullable) throw input.Error("USTX_NULL", LastPath, "Required data cannot be null.");
                input.RecordMissing(LastPath, expected.Default);
            }
            else if (expected.Kind != Kind.Ignore && (definition.Rule.Kind != expected.Kind ||
                expected.Kind is Kind.Mapping or Kind.Sequence && definition.Rule.Object != expected.Object))
                throw input.Error("USTX_ALIAS", LastPath, "Alias does not refer to the expected validated structure.");
            CompleteValue();
            return true;
        }
        if (item is YamlDotNet.Core.Events.Scalar scalar)
        {
            var isNull = IsNull(scalar); Remember(scalar.Anchor, Scalar(), isNull);
            if (isNull)
            {
                if (!expected.Nullable) throw input.Error("USTX_NULL", LastPath, "Required data cannot be null.");
                input.RecordMissing(LastPath, expected.Default);
            }
            else if (expected.Kind is not (Kind.Scalar or Kind.Ignore))
                throw input.Error("USTX_STRUCTURE", LastPath, "Expected a mapping or sequence, not a scalar.");
            CompleteValue();
            return true;
        }
        var actual = item is MappingStart ? Kind.Mapping : item is SequenceStart ? Kind.Sequence : Kind.Ignore;
        if (expected.Kind == Kind.Ignore)
        {
            if (item is NodeEvent ignored) Remember(ignored.Anchor, new Rule(actual));
            ignoredDepth = 1;
        }
        else
        {
            if (actual != expected.Kind)
                throw input.Error("USTX_STRUCTURE", LastPath, "Unexpected mapping or sequence for this field.");
            if (item is NodeEvent node) Remember(node.Anchor, expected);
            frames.Push(new Frame(LastPath, expected));
        }
        return true;
    }

    public void Complete()
    {
        while (MoveNext()) { }
        if (!rootSeen || !rootComplete) throw input.Error("USTX_EMPTY", "$", "Expected a complete root project mapping.");
    }
}
