using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization.NodeDeserializers;
using YamlDotNet.Serialization.TypeResolvers;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;
using UtaFormatix.Core.Process.Pitch;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.IO;

/// <summary>
/// Static YamlDotNet context for USTX serialization. The YamlDotNet source generator
/// fills in the other partial half with the AOT-friendly serializer metadata, so we can
/// use StaticDeserializerBuilder/StaticSerializerBuilder instead of the reflection-based
/// builders (which break under NativeAOT).
/// </summary>
[YamlStaticContext]
public partial class UstxYamlContext : StaticContext
{
}

public static class Ustx
{
    private const string PitchCurveAbbr = "pitd";
    private static readonly UstxYamlInteger YamlInteger = new();

    private static string LoadTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("UtaFormatix.Core.Resources.template.ustx");
        if (stream == null)
            throw new InvalidOperationException("USTX template not found in embedded resources");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static Project Parse(string filePath, ImportParams? importParams = null)
    {
        var parameters = importParams ?? new ImportParams();
        var pitchContext = UstxPitchContext.Create(parameters.Ustx, filePath);
        var warnings = new List<ImportWarning>();
        var input = new UstxInput(filePath, warnings);
        var yamlText = input.ReadText();
        var deserializer = new StaticDeserializerBuilder(new UstxYamlContext())
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithNodeTypeResolver(YamlInteger)
            .WithNodeDeserializer(YamlInteger, location => location.Before<NullNodeDeserializer>())
            .IgnoreUnmatchedProperties()
            .WithDuplicateKeyChecking()
            .Build();
        UstxProject ustxProject;
        try
        {
            ustxProject = UstxYamlParser.Deserialize<UstxProject>(deserializer, yamlText, input.Attach);
            input.Complete();
        }
        catch (YamlException error)
        {
            Exception cause = error;
            while (cause.InnerException is { } inner)
            {
                if (inner is ProjectInputException diagnostic) throw diagnostic;
                cause = inner;
            }
            throw input.Error("USTX_YAML", input.LastPath,
                $"YAML ({error.Start}) - ({error.End}): {cause.Message}");
        }
        input.Normalize(ustxProject);

        var tempos = ParseTempos(ustxProject);
        var timeSignatures = ParseTimeSignatures(ustxProject);
        var previousOffset = 0.0;
        var time = new TickTimeTransformer(tempos, (index, rate, offset) =>
        {
            if (!double.IsFinite(rate) || rate <= 0)
            {
                var field = ustxProject.Tempos is { Count: > 0 } ? $"$.tempos[{index}].bpm" : "$.bpm";
                throw input.Error("USTX_RANGE", field, "Tempo cannot be represented as a finite positive tick duration.");
            }
            if (!double.IsFinite(offset * 1000.0))
                throw input.Error("USTX_RANGE", $"$.tempos[{index}].position", "Elapsed time to the tempo marker exceeds the millisecond range.");
            if (!parameters.SimpleImport && index > 0 && tempos[index].TickPosition > tempos[index - 1].TickPosition &&
                offset <= previousOffset)
                throw input.Error("USTX_RANGE", $"$.tempos[{index}].position", "Distinct tempo markers collapse to the same elapsed time.");
            previousOffset = offset;
        });
        var tracks = ParseTracks(ustxProject, parameters, time, pitchContext);

        return new Project(
            Format: Format.Ustx,
            Name: ustxProject.Name,
            Tracks: tracks,
            TimeSignatures: timeSignatures,
            Tempos: tempos,
            MeasurePrefix: 0,
            ImportWarnings: warnings);
    }

    private static List<Tempo> ParseTempos(UstxProject project)
    {
        if (project.Tempos is { Count: > 0 })
        {
            return project.Tempos.Select(t => new Tempo(t.Position, t.Bpm)).ToList();
        }

        return [new Tempo(0, project.Bpm ?? 120.0)];
    }

    private static List<TimeSignature> ParseTimeSignatures(UstxProject project)
    {
        if (project.TimeSignatures is { Count: > 0 })
        {
            return project.TimeSignatures.Select(ts => new TimeSignature(
                ts.BarPosition, ts.BeatPerBar, ts.BeatUnit)).ToList();
        }

        return [new TimeSignature(0, project.BeatPerBar ?? 4, project.BeatUnit ?? 4)];
    }

    private static List<Track> ParseTracks(UstxProject project, ImportParams parameters,
        TickTimeTransformer time, UstxPitchContext pitchContext)
    {
        var tracks = new List<Track>(project.Tracks.Count);
        var pitchByTrack = new List<PitchModel>[project.Tracks.Count];
        var tuningByTrack = new Dictionary<long, int>?[project.Tracks.Count];
        for (var i = 0; i < project.Tracks.Count; i++)
        {
            var t = project.Tracks[i];
            tracks.Add(new Track(i, t.TrackName ?? $"Track {i + 1}", [], null));
            pitchByTrack[i] = [];
        }

        for (var partIndex = 0; partIndex < project.VoiceParts.Count; partIndex++)
        {
            var voicePart = project.VoiceParts[partIndex];
            var trackId = voicePart.TrackNo;
            var track = tracks[trackId];

            var tickPrefix = voicePart.Position;
            var notes = voicePart.Notes.Select(n =>
            {
                var rawLyrics = n.Lyric;
                var match = Regex.Match(rawLyrics, @"\[([^\[\]]*)\]\z");
                string lyric;
                string? phoneme;
                if (!match.Success)
                {
                    lyric = rawLyrics;
                    phoneme = null;
                }
                else
                {
                    var cleanedPhoneme = match.Groups[1].Value;
                    var beforeBracket = LyricWhitespace.Trim(rawLyrics[..match.Index]);
                    lyric = beforeBracket.Length > 0 ? beforeBracket : cleanedPhoneme;
                    phoneme = cleanedPhoneme;
                }

                return new Note(
                    Id: 0,
                    Key: n.Tone,
                    Lyric: lyric,
                    Phoneme: phoneme,
                    TickOn: checked(n.Position + tickPrefix),
                    TickOff: checked(n.Position + tickPrefix + n.Duration));
            }).ToList();

            track.Notes.AddRange(notes);
            if (parameters.SimpleImport) continue;

            var partContext = pitchContext.At($".voice_parts[{partIndex}]");
            partContext.Samples(voicePart.Notes.Sum(note => (long)note.Pitch.Data.Count), ".notes");
            var notePitches = new List<OpenUtauNotePitchData>(notes.Count);
            for (var i = 0; i < notes.Count; i++)
            {
                var note = voicePart.Notes[i];
                var previousAdjacentNote = i > 0 && notes[i - 1].TickOff == notes[i].TickOn
                    ? voicePart.Notes[i - 1]
                    : null;
                notePitches.Add(ParseNotePitch(note, previousAdjacentNote));
                if (note.Tuning != 0)
                    (tuningByTrack[trackId] ??= [])[notes[i].TickOn] = note.Tuning;
            }

            var curveIndex = voicePart.Curves?.FindIndex(c => c.Abbr == PitchCurveAbbr) ?? -1;
            List<OpenUtauPartPitchData.Point> pitchCurve = [];
            if (curveIndex >= 0)
            {
                var curve = voicePart.Curves![curveIndex];
                partContext.Samples(curve.Xs.Count, $".curves[{curveIndex}]");
                pitchCurve = curve.Xs.Zip(curve.Ys)
                    .Select(xy => new OpenUtauPartPitchData.Point(checked(xy.First + tickPrefix), checked((int)xy.Second)))
                    .ToList();
            }

            if (notePitches.Count == 0 && pitchCurve.Count == 0) continue;
            var partPitchData = new OpenUtauPartPitchData(pitchCurve, notePitches);
            var pitch = OpenUtauPitchConversion.PitchFromUstxPart(notes, partPitchData, time, partContext, curveIndex);
            if (pitch is not null) pitchByTrack[trackId].Add(pitch);
        }

        return tracks.Select(t =>
        {
            var notes = t.Notes.OrderBy(n => n.TickOn).Select((n, i) => n with { Id = i }).ToList();
            var trackContext = pitchContext.At($".tracks[{t.Id}].pitch");
            var pitch = OpenUtauPitchConversion.MergePitchFromUstxParts(pitchByTrack[t.Id], trackContext);
            if (pitch is not null && tuningByTrack[t.Id] is { } tuning)
                pitch = OpenUtauPitchConversion.ApplyNoteTuning(pitch, notes, tuning, trackContext);
            return new Track(t.Id, t.Name, notes, pitch.ReduceRepeatedPitchPointsFromUstxTrack());
        }).ToList();
    }

    private static OpenUtauNotePitchData ParseNotePitch(UstxNote note, UstxNote? previousAdjacentNote)
    {
        var points = note.Pitch.Data.Select(d => new OpenUtauNotePitchData.Point(
            d.X, d.Y, OpenUtauNotePitchData.ParseShape(d.Shape))).ToList();
        if (note.Pitch.SnapFirst && points.Count > 0)
        {
            var firstY = previousAdjacentNote is null
                ? 0.0
                : ((double)previousAdjacentNote.Tone - note.Tone) * 10.0 +
                  ((double)previousAdjacentNote.Tuning - note.Tuning) / 10.0;
            points[0] = points[0] with { Y = firstY };
        }

        var v = note.Vibrato;
        var vibrato = new UtauNoteVibratoParams(
            Length: v.Length,
            Period: v.Period,
            Depth: v.Depth,
            FadeIn: v.In,
            FadeOut: v.Out,
            PhaseShift: v.Shift,
            Shift: v.Drift);

        return new OpenUtauNotePitchData(points, vibrato, note.Tuning);
    }

    public static void GenerateFile(Project project, string filePath, IEnumerable<FeatureConfig>? features = null) =>
        GenerateFile(project, filePath, features, null);

    public static void GenerateFile(Project project, string filePath, IEnumerable<FeatureConfig>? features, UstxPitchOptions? options)
    {
        var yamlText = GenerateContent(project, features?.ToList() ?? [], options);
        File.WriteAllText(filePath, yamlText);
    }

    public static (byte[] Data, string Name, List<ExportNotification> Notifications) Generate(
        Project project, IEnumerable<FeatureConfig>? features = null) =>
        Generate(project, features, null);

    public static (byte[] Data, string Name, List<ExportNotification> Notifications) Generate(
        Project project, IEnumerable<FeatureConfig>? features, UstxPitchOptions? options)
    {
        var featureList = features?.ToList() ?? [];
        var yamlText = GenerateContent(project, featureList, options);
        var notifications = new List<ExportNotification>();
        if (featureList.Contains(Feature.ConvertPitch))
            notifications.Add(new ExportNotification.PitchDataExported());
        return (System.Text.Encoding.UTF8.GetBytes(yamlText), Format.Ustx.GetFileName(project.Name), notifications);
    }

    private static string GenerateContent(Project project, List<FeatureConfig> features, UstxPitchOptions? options)
    {
        var pitchContext = UstxPitchContext.Create(options);
        var templateYamlText = LoadTemplate();
        var deserializer = new StaticDeserializerBuilder(new UstxYamlContext())
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var template = deserializer.Deserialize<UstxProject>(templateYamlText);

        var trackTemplate = template.Tracks.First();
        var tracks = project.Tracks.Select(t => new UstxTrack
        {
            Phonemizer = trackTemplate.Phonemizer,
            Mute = trackTemplate.Mute,
            Solo = trackTemplate.Solo,
            Volume = trackTemplate.Volume,
            TrackName = t.Name,
        }).ToList();

        var voicePartTemplate = template.VoiceParts.First();
        var voiceParts = project.Tracks.Select((t, index) =>
            GenerateVoicePart(voicePartTemplate, t, features, pitchContext.At($".tracks[{index}]"))).ToList();

        var ustx = new UstxProject
        {
            Name = project.Name,
            Comment = template.Comment,
            OutputDir = template.OutputDir,
            CacheDir = template.CacheDir,
            UstxVersion = template.UstxVersion,
            Resolution = template.Resolution,
            Bpm = project.Tempos.First().Bpm,
            BeatPerBar = project.TimeSignatures.First().Numerator,
            BeatUnit = project.TimeSignatures.First().Denominator,
            Tempos = project.Tempos.Select(t => new UstxTempo { Position = t.TickPosition, Bpm = t.Bpm }).ToList(),
            TimeSignatures = project.TimeSignatures.Select(ts => new UstxTimeSignature
            {
                BarPosition = ts.MeasurePosition,
                BeatPerBar = ts.Numerator,
                BeatUnit = ts.Denominator,
            }).ToList(),
            Expressions = template.Expressions,
            Tracks = tracks,
            VoiceParts = voiceParts,
        };

        var serializer = new StaticSerializerBuilder(new UstxYamlContext())
            // Preserve string types and exact whitespace across YAML readers.
            .WithDefaultScalarStyle(ScalarStyle.DoubleQuoted)
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
            .Build();
        return serializer.Serialize(ustx);
    }

    private static UstxVoicePart GenerateVoicePart(
        UstxVoicePart template, Track track, List<FeatureConfig> features, UstxPitchContext context)
    {
        var noteTemplate = template.Notes.First();
        var notes = new List<UstxNote>();

        for (var i = 0; i < track.Notes.Count; i++)
        {
            var lastNote = i > 0 ? track.Notes[i - 1] : null;
            var thisNote = track.Notes[i];
            var duration = context.Distance(thisNote.TickOn, thisNote.TickOff, $".notes[{i}].duration");
            notes.Add(GenerateNote(noteTemplate, lastNote, thisNote, duration));
        }

        var curves = new List<UstxCurve>();
        if (features.Contains(Feature.ConvertPitch))
        {
            var points = track.Pitch.ToOpenUtauPitchData(track.Notes, context.At(".pitch"));
            if (points.Count > 0)
            {
                curves.Add(new UstxCurve
                {
                    Xs = points.Select(p => p.Tick).ToList(),
                    Ys = points.Select(p => p.Value).ToList(),
                    Abbr = PitchCurveAbbr,
                });
            }
        }

        return new UstxVoicePart
        {
            Name = track.Name,
            Comment = template.Comment,
            TrackNo = track.Id,
            Position = 0,
            Notes = notes,
            Curves = curves,
        };
    }

    private static UstxNote GenerateNote(UstxNote template, Note? lastNote, Note thisNote, long duration)
    {
        var firstPitchPointValue = lastNote is not null && lastNote.TickOff == thisNote.TickOn
            ? ((double)lastNote.Key - thisNote.Key) * 10.0
            : 0.0;

        var pitchPoints = template.Pitch.Data.Select((d, i) =>
            i == 0 ? new UstxDatum { X = d.X, Y = firstPitchPointValue, Shape = d.Shape }
                   : new UstxDatum { X = d.X, Y = d.Y, Shape = d.Shape }).ToList();

        var lyric = !LyricWhitespace.IsNullOrBlank(thisNote.Phoneme)
            ? $"{thisNote.Lyric} [{thisNote.Phoneme}]"
            : thisNote.Lyric;

        return new UstxNote
        {
            Position = thisNote.TickOn,
            Duration = duration,
            Tone = thisNote.Key,
            Lyric = lyric,
            Pitch = new UstxPitch { Data = pitchPoints, SnapFirst = template.Pitch.SnapFirst },
            Vibrato = new UstxVibrato
            {
                Length = template.Vibrato.Length,
                Period = template.Vibrato.Period,
                Depth = template.Vibrato.Depth,
                In = template.Vibrato.In,
                Out = template.Vibrato.Out,
                Shift = template.Vibrato.Shift,
                Drift = template.Vibrato.Drift,
            },
        };
    }

    #region YAML Serialization Models

    [YamlSerializable]
    public sealed class UstxProject
    {
        public string Name { get; set; } = "";
        public string Comment { get; set; } = "";
        public string OutputDir { get; set; } = "";
        public string CacheDir { get; set; } = "";
        public double UstxVersion { get; set; }
        public double? Bpm { get; set; }
        public int? BeatPerBar { get; set; }
        public int? BeatUnit { get; set; }
        public int? Resolution { get; set; }
        public List<UstxTimeSignature>? TimeSignatures { get; set; }
        public List<UstxTempo>? Tempos { get; set; }
        public Dictionary<string, UstxExpression> Expressions { get; set; } = new();
        public List<UstxTrack> Tracks { get; set; } = [];
        public List<UstxVoicePart> VoiceParts { get; set; } = [];
    }

    [YamlSerializable]
    public sealed class UstxExpression
    {
        public string Name { get; set; } = "";
        public string Abbr { get; set; } = "";
        public string Type { get; set; } = "";
        public int Min { get; set; }
        public int Max { get; set; }
        public int DefaultValue { get; set; }
        public bool IsFlag { get; set; }
        public string? Flag { get; set; }
        public List<string>? Options { get; set; }
    }

    [YamlSerializable]
    public sealed class UstxTrack
    {
        public string Phonemizer { get; set; } = "";
        public bool Mute { get; set; }
        public bool Solo { get; set; }
        public double Volume { get; set; }
        public string? TrackName { get; set; }
    }

    [YamlSerializable]
    public sealed class UstxVoicePart
    {
        public string Name { get; set; } = "";
        public string Comment { get; set; } = "";
        public int TrackNo { get; set; }
        public long Position { get; set; }
        public List<UstxNote> Notes { get; set; } = [];
        public List<UstxCurve>? Curves { get; set; }
    }

    [YamlSerializable]
    public sealed class UstxNote
    {
        public long Position { get; set; }
        public long Duration { get; set; }
        public int Tone { get; set; }
        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
        public int Tuning { get; set; }
        public string Lyric { get; set; } = "";
        public UstxPitch Pitch { get; set; } = new();
        public UstxVibrato Vibrato { get; set; } = new();
    }

    [YamlSerializable]
    public sealed class UstxPitch
    {
        public List<UstxDatum> Data { get; set; } = [];
        public bool SnapFirst { get; set; } = true;
    }

    [YamlSerializable]
    public sealed class UstxDatum
    {
        public double X { get; set; }
        public double Y { get; set; }
        public string Shape { get; set; } = "io";
    }

    [YamlSerializable]
    public sealed class UstxVibrato
    {
        public double Length { get; set; }
        public double Period { get; set; }
        public double Depth { get; set; }
        public double In { get; set; }
        public double Out { get; set; }
        public double Shift { get; set; }
        public double Drift { get; set; }
    }

    [YamlSerializable]
    public sealed class UstxCurve
    {
        public List<long> Xs { get; set; } = [];
        public List<double> Ys { get; set; } = [];
        public string Abbr { get; set; } = "";
    }

    [YamlSerializable]
    public sealed class UstxTempo
    {
        public long Position { get; set; }
        public double Bpm { get; set; }
    }

    [YamlSerializable]
    public sealed class UstxTimeSignature
    {
        public int BarPosition { get; set; }
        public int BeatPerBar { get; set; }
        public int BeatUnit { get; set; }
    }

    #endregion
}
