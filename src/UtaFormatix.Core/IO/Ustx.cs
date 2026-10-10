using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process.Pitch;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.IO;

public static class Ustx
{
    private const string PitchCurveAbbr = "pitd";

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
        var yamlText = File.ReadAllText(filePath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var ustxProject = deserializer.Deserialize<UstxProject>(yamlText);

        var tempos = ParseTempos(ustxProject);
        var timeSignatures = ParseTimeSignatures(ustxProject);
        var tracks = ParseTracks(ustxProject, importParams ?? new ImportParams(), tempos);

        return new Project(
            Format: Format.Ustx,
            Name: ustxProject.Name,
            Tracks: tracks,
            TimeSignatures: timeSignatures,
            Tempos: tempos,
            MeasurePrefix: 0,
            ImportWarnings: []);
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

    private static List<Track> ParseTracks(UstxProject project, ImportParams parameters, List<Tempo> tempos)
    {
        var trackMap = new Dictionary<int, Track>();
        for (var i = 0; i < project.Tracks.Count; i++)
        {
            var t = project.Tracks[i];
            trackMap[i] = new Track(i, t.TrackName ?? $"Track {i + 1}", [], null);
        }

        foreach (var voicePart in project.VoiceParts)
        {
            var trackId = voicePart.TrackNo;
            if (!trackMap.TryGetValue(trackId, out var track)) continue;

            var tickPrefix = voicePart.Position;
            var notes = voicePart.Notes.Select(n =>
            {
                var rawLyrics = n.Lyric;
                var match = Regex.Match(rawLyrics, @"\[([^\[\]]*)\]$");
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
                    var beforeBracket = rawLyrics[..match.Index].Trim();
                    lyric = beforeBracket.Length > 0 ? beforeBracket : cleanedPhoneme;
                    phoneme = cleanedPhoneme;
                }

                return new Note(
                    Id: 0,
                    Key: n.Tone,
                    Lyric: lyric,
                    Phoneme: phoneme,
                    TickOn: n.Position + tickPrefix,
                    TickOff: n.Position + n.Duration + tickPrefix);
            }).ToList();

            List<OpenUtauNotePitchData>? notePitches = null;
            if (!parameters.SimpleImport)
            {
                notePitches = voicePart.Notes.Select(ParseNotePitch).ToList();
            }

            var (validatedNotes, validatedNotePitches) = GetValidatedNotes(notes, notePitches);

            OpenUtauPartPitchData.Point[]? pitchCurve = null;
            if (!parameters.SimpleImport)
            {
                var curve = voicePart.Curves?.FirstOrDefault(c => c.Abbr == PitchCurveAbbr);
                if (curve is not null)
                {
                    pitchCurve = curve.Xs.Zip(curve.Ys)
                        .Select(xy => new OpenUtauPartPitchData.Point(xy.First + tickPrefix, (int)xy.Second))
                        .ToArray();
                }
            }

            PitchModel? pitch = null;
            if ((validatedNotePitches is { Count: > 0 } || pitchCurve is { Length: > 0 }))
            {
                var partPitchData = new OpenUtauPartPitchData(
                    pitchCurve?.ToList() ?? [],
                    validatedNotePitches ?? []);
                pitch = OpenUtauPitchConversion.PitchFromUstxPart(validatedNotes, partPitchData, tempos);
            }

            var mergedPitch = OpenUtauPitchConversion.MergePitchFromUstxParts(track.Pitch, pitch);
            trackMap[trackId] = new Track(
                track.Id,
                track.Name,
                track.Notes.Concat(validatedNotes).ToList(),
                mergedPitch);
        }

        return trackMap.Values
            .Select(t => new Track(
                t.Id,
                t.Name,
                t.Notes.Select((n, i) => n with { Id = i }).ToList(),
                t.Pitch.ReduceRepeatedPitchPointsFromUstxTrack()))
            .OrderBy(t => t.Id)
            .ToList();
    }

    private static OpenUtauNotePitchData ParseNotePitch(UstxNote note)
    {
        var points = note.Pitch.Data.Select(d => new OpenUtauNotePitchData.Point(
            d.X, d.Y, OpenUtauNotePitchData.ParseShape(d.Shape))).ToList();

        var v = note.Vibrato;
        var vibrato = new UtauNoteVibratoParams(
            Length: v.Length,
            Period: v.Period,
            Depth: v.Depth,
            FadeIn: v.In,
            FadeOut: v.Out,
            PhaseShift: v.Shift,
            Shift: v.Drift);

        return new OpenUtauNotePitchData(points, vibrato);
    }

    private static (List<Note> Notes, List<OpenUtauNotePitchData>? Pitches) GetValidatedNotes(
        List<Note> notes, List<OpenUtauNotePitchData>? notePitches)
    {
        var validatedNotes = new List<Note>();
        var validatedNotePitches = notePitches is not null ? new List<OpenUtauNotePitchData>() : null;
        var pos = 0L;

        for (var i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            if (note.TickOn >= pos)
            {
                validatedNotes.Add(note);
                if (notePitches is not null)
                    validatedNotePitches!.Add(notePitches[i]);
                pos = note.TickOff;
            }
        }

        return (validatedNotes, validatedNotePitches);
    }

    public static void GenerateFile(Project project, string filePath, IEnumerable<FeatureConfig>? features = null)
    {
        var yamlText = GenerateContent(project, features?.ToList() ?? []);
        File.WriteAllText(filePath, yamlText);
    }

    public static (byte[] Data, string Name, List<ExportNotification> Notifications) Generate(
        Project project, IEnumerable<FeatureConfig>? features = null)
    {
        var featureList = features?.ToList() ?? [];
        var yamlText = GenerateContent(project, featureList);
        var notifications = new List<ExportNotification>();
        if (featureList.Contains(Feature.ConvertPitch))
            notifications.Add(new ExportNotification.PitchDataExported());
        return (System.Text.Encoding.UTF8.GetBytes(yamlText), Format.Ustx.GetFileName(project.Name), notifications);
    }

    private static string GenerateContent(Project project, List<FeatureConfig> features)
    {
        var templateYamlText = LoadTemplate();
        var deserializer = new DeserializerBuilder()
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
        var voiceParts = project.Tracks.Select(t => GenerateVoicePart(voicePartTemplate, t, features)).ToList();

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

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
            .Build();
        return serializer.Serialize(ustx);
    }

    private static UstxVoicePart GenerateVoicePart(
        UstxVoicePart template, Track track, List<FeatureConfig> features)
    {
        var noteTemplate = template.Notes.First();
        var notes = new List<UstxNote>();

        for (var i = 0; i < track.Notes.Count; i++)
        {
            var lastNote = i > 0 ? track.Notes[i - 1] : null;
            var thisNote = track.Notes[i];
            notes.Add(GenerateNote(noteTemplate, lastNote, thisNote));
        }

        var curves = new List<UstxCurve>();
        if (features.Contains(Feature.ConvertPitch))
        {
            var points = track.Pitch.ToOpenUtauPitchData(track.Notes);
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

    private static UstxNote GenerateNote(UstxNote template, Note? lastNote, Note thisNote)
    {
        var firstPitchPointValue = lastNote is not null && lastNote.TickOff == thisNote.TickOn
            ? (lastNote.Key - thisNote.Key) * 10.0
            : 0.0;

        var pitchPoints = template.Pitch.Data.Select((d, i) =>
            i == 0 ? new UstxDatum { X = d.X, Y = firstPitchPointValue, Shape = d.Shape }
                   : new UstxDatum { X = d.X, Y = d.Y, Shape = d.Shape }).ToList();

        var lyric = !string.IsNullOrWhiteSpace(thisNote.Phoneme)
            ? $"{thisNote.Lyric} [{thisNote.Phoneme}]"
            : thisNote.Lyric;

        return new UstxNote
        {
            Position = thisNote.TickOn,
            Duration = thisNote.Length,
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

    public sealed class UstxTrack
    {
        public string Phonemizer { get; set; } = "";
        public bool Mute { get; set; }
        public bool Solo { get; set; }
        public double Volume { get; set; }
        public string? TrackName { get; set; }
    }

    public sealed class UstxVoicePart
    {
        public string Name { get; set; } = "";
        public string Comment { get; set; } = "";
        public int TrackNo { get; set; }
        public long Position { get; set; }
        public List<UstxNote> Notes { get; set; } = [];
        public List<UstxCurve>? Curves { get; set; }
    }

    public sealed class UstxNote
    {
        public long Position { get; set; }
        public long Duration { get; set; }
        public int Tone { get; set; }
        public string Lyric { get; set; } = "";
        public UstxPitch Pitch { get; set; } = new();
        public UstxVibrato Vibrato { get; set; } = new();
    }

    public sealed class UstxPitch
    {
        public List<UstxDatum> Data { get; set; } = [];
        public bool SnapFirst { get; set; }
    }

    public sealed class UstxDatum
    {
        public double X { get; set; }
        public double Y { get; set; }
        public string Shape { get; set; } = "io";
    }

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

    public sealed class UstxCurve
    {
        public List<long> Xs { get; set; } = [];
        public List<double> Ys { get; set; } = [];
        public string Abbr { get; set; } = "";
    }

    public sealed class UstxTempo
    {
        public long Position { get; set; }
        public double Bpm { get; set; }
    }

    public sealed class UstxTimeSignature
    {
        public int BarPosition { get; set; }
        public int BeatPerBar { get; set; }
        public int BeatUnit { get; set; }
    }

    #endregion
}
