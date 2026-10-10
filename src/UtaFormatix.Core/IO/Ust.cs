using System.IO.Compression;
using System.Text;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;
using UtaFormatix.Core.Process.Pitch;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.IO;

public static class Ust
{
    private static readonly Encoding ShiftJis;

    static Ust()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ShiftJis = Encoding.GetEncoding(932);
    }

    public static Project Parse(string filePath, ImportParams? importParams = null) =>
        Parse([filePath], importParams);

    public static Project Parse(string[] filePaths, ImportParams? importParams = null)
    {
        var parameters = importParams ?? new ImportParams();
        var results = filePaths.Select(ParseFile).ToList();

        var projectName = filePaths.Length == 1
            ? Path.GetFileNameWithoutExtension(filePaths[0])
            : $"{Path.GetFileNameWithoutExtension(filePaths[0])} ({filePaths.Length - 1} more)";

        var tracks = new List<Track>();
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            PitchModel? pitch = null;
            if (!parameters.SimpleImport)
            {
                pitch = result.IsMode2
                    ? UtauMode2PitchConversion.PitchFromUtauMode2Track(result.PitchDataMode2, result.Notes, result.Tempos)
                    : UtauMode1PitchConversion.PitchFromUtauMode1Track(result.PitchDataMode1, result.Notes);
            }

            tracks.Add(new Track(i, result.FileName, result.Notes, pitch).ValidateNotes());
        }

        var warnings = new List<ImportWarning>();
        var temposResult = ResolveTempos(results, warnings);
        var tempos = temposResult.Tempos;
        warnings.AddRange(temposResult.Warnings);

        return new Project(
            Format: Format.Ust,
            Name: projectName,
            Tracks: tracks,
            TimeSignatures: [new TimeSignature(0, Constants.DefaultMeterHigh, Constants.DefaultMeterLow)],
            Tempos: tempos,
            MeasurePrefix: 0,
            ImportWarnings: warnings);
    }

    private static (List<Tempo> Tempos, List<ImportWarning> Warnings) ResolveTempos(
        List<FileParseResult> results, List<ImportWarning> warnings)
    {
        var firstWithTempos = results.FirstOrDefault(r => r.Tempos.Count > 0);
        if (firstWithTempos is null)
        {
            warnings.Add(new ImportWarning.TempoNotFound());
            return ([new Tempo(0, Constants.DefaultBpm)], warnings);
        }

        var tempos = firstWithTempos.Tempos.ToList();
        if (tempos[0].TickPosition == 0 && tempos[0].Bpm > UstConstants.MaxAcceptedBpm)
        {
            warnings.Add(new ImportWarning.DefaultTempoFixed(tempos[0].Bpm));
            tempos[0] = new Tempo(0, Constants.DefaultBpm);
        }

        var tempoSet = tempos.ToHashSet();
        foreach (var result in results)
        {
            foreach (var tempo in result.Tempos)
            {
                if (!tempoSet.Contains(tempo))
                    warnings.Add(new ImportWarning.TempoIgnoredInFile(result.FilePath, tempo));
            }
        }

        return (tempos, warnings);
    }

    private static string ReadFileContent(string filePath)
    {
        var binary = File.ReadAllBytes(filePath);
        try
        {
            var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var text = utf8Strict.GetString(binary);
            return text;
        }
        catch (DecoderFallbackException)
        {
            return ShiftJis.GetString(binary);
        }
    }

    private static FileParseResult ParseFile(string filePath)
    {
        var lines = ReadFileContent(filePath)
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        string? projectName = null;
        var notes = new List<Note>();
        var notePitchDataListMode1 = new List<UtauMode1NotePitchData>();
        var notePitchDataListMode2 = new List<UtauMode2NotePitchData>();
        var tempos = new Dictionary<long, double>();
        var isHeader = true;
        var time = 0L;
        int? pendingNoteKey = null;
        string? pendingNoteLyric = null;
        long? pendingNoteTickOn = null;
        long? pendingNoteTickOff = null;
        var isMode2 = false;
        double? pendingBpm = null;
        List<double>? pendingPitchBend = null;
        (double Start, double? StartShift)? pendingPBS = null;
        List<double>? pendingPBW = null;
        List<double>? pendingPBY = null;
        List<string>? pendingPBM = null;
        List<double>? pendingVBR = null;

        double GetCurrentTempo() =>
            tempos.Count > 0 ? tempos.MaxBy(kv => kv.Key).Value : Constants.DefaultBpm;

        foreach (var line in lines)
        {
            TryGetValue(line, "ProjectName", out var projectNameVal);
            if (projectNameVal is not null) projectName = projectNameVal;

            if (TryGetValue(line, "Tempo", out var tempoVal))
            {
                var normalized = tempoVal.Replace(',', '.');
                if (double.TryParse(normalized, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var bpm))
                {
                    if (isHeader)
                    {
                        tempos[0] = bpm;
                    }
                    else if (pendingNoteTickOn is not null)
                    {
                        tempos[pendingNoteTickOn.Value] = bpm;
                    }
                    else
                    {
                        pendingBpm = bpm;
                    }
                }
            }

            if (line.Contains("Mode2=True")) isMode2 = true;
            if (line.Contains("[#0000]")) isHeader = false;

            if (line.Contains("[#"))
            {
                if (pendingNoteKey is not null &&
                    pendingNoteLyric is not null &&
                    pendingNoteTickOn is not null &&
                    pendingNoteTickOff is not null)
                {
                    notes.Add(new Note(
                        notes.Count,
                        pendingNoteKey.Value,
                        pendingNoteLyric,
                        pendingNoteTickOn.Value,
                        pendingNoteTickOff.Value));

                    notePitchDataListMode2.Add(new UtauMode2NotePitchData(
                        GetCurrentTempo(),
                        pendingPBS?.Start,
                        pendingPBS?.StartShift,
                        pendingPBW ?? [],
                        pendingPBY ?? [],
                        pendingPBM ?? [],
                        ParseVibrato(pendingVBR)));

                    notePitchDataListMode1.Add(new UtauMode1NotePitchData(pendingPitchBend));
                }

                pendingNoteKey = null;
                pendingNoteLyric = null;
                pendingNoteTickOn = null;
                pendingNoteTickOff = null;
                pendingPBS = null;
                pendingPBW = null;
                pendingPBY = null;
                pendingPBM = null;
                pendingVBR = null;
                pendingPitchBend = null;
            }

            if (TryGetValue(line, "Length", out var lengthVal))
            {
                if (double.TryParse(lengthVal, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var lengthD))
                {
                    var length = (long)Math.Round(lengthD);
                    pendingNoteTickOn = time;
                    if (pendingBpm is not null)
                    {
                        tempos[time] = pendingBpm.Value;
                        pendingBpm = null;
                    }
                    time += length;
                    pendingNoteTickOff = time;
                }
            }

            if (TryGetValue(line, "Lyric", out var lyricVal))
            {
                if (lyricVal != "R" && lyricVal != "r")
                    pendingNoteLyric = lyricVal;
            }

            if (TryGetValue(line, "NoteNum", out var noteNumVal))
            {
                if (int.TryParse(noteNumVal, out var key))
                    pendingNoteKey = key;
            }

            if (isMode2)
            {
                if (TryGetValue(line, "PBS", out var pbsVal))
                {
                    var cells = pbsVal.Split([';', ',']);
                    if (double.TryParse(cells[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var start))
                    {
                        double? startShift = cells.Length > 1
                            ? double.TryParse(cells[1], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var ss) ? ss : null
                            : null;
                        pendingPBS = (start, startShift);
                    }
                }

                if (TryGetValue(line, "PBW", out var pbwVal))
                    pendingPBW = pbwVal.Split(',').Select(w =>
                        double.TryParse(w, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.0).ToList();

                if (TryGetValue(line, "PBY", out var pbyVal))
                    pendingPBY = pbyVal.Split(',').Select(s =>
                        double.TryParse(s, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.0).ToList();

                if (TryGetValue(line, "PBM", out var pbmVal))
                    pendingPBM = pbmVal.Split(',').ToList();

                if (TryGetValue(line, "VBR", out var vbrVal))
                    pendingVBR = vbrVal.Split(',')
                        .Where(c => double.TryParse(c, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out _))
                        .Select(c => double.Parse(c, System.Globalization.CultureInfo.InvariantCulture))
                        .ToList();
            }
            else
            {
                if (TryGetValue(line, "Piches", out var pichesVal) && pendingPitchBend is null)
                    pendingPitchBend = ParseMode1PitchData(pichesVal);
                else if (TryGetValue(line, "Pitches", out var pitchesVal) && pendingPitchBend is null)
                    pendingPitchBend = ParseMode1PitchData(pitchesVal);
                else if (TryGetValue(line, "PitchBend", out var pitchBendVal) && pendingPitchBend is null)
                    pendingPitchBend = ParseMode1PitchData(pitchBendVal);
            }
        }

        var tempoList = tempos.Select(kv => new Tempo(kv.Key, kv.Value))
            .OrderBy(t => t.TickPosition)
            .ToList();

        var pitchDataMode1 = notePitchDataListMode1.Count > 0
            ? new UtauMode1TrackPitchData(notePitchDataListMode1)
            : null;
        var pitchDataMode2 = notePitchDataListMode2.Count > 0
            ? new UtauMode2TrackPitchData(notePitchDataListMode2)
            : null;

        return new FileParseResult(
            filePath,
            Path.GetFileNameWithoutExtension(filePath),
            notes,
            tempoList,
            isMode2,
            pitchDataMode1,
            pitchDataMode2);
    }

    private static UtauNoteVibratoParams? ParseVibrato(List<double>? vbr)
    {
        if (vbr is null || vbr.Count < 2) return null;
        return new UtauNoteVibratoParams(
            vbr[0],
            vbr[1],
            vbr.Count > 2 ? vbr[2] : 0.0,
            vbr.Count > 3 ? vbr[3] : 0.0,
            vbr.Count > 4 ? vbr[4] : 0.0,
            vbr.Count > 5 ? vbr[5] : 0.0,
            vbr.Count > 6 ? vbr[6] : 0.0);
    }

    private static List<double> ParseMode1PitchData(string pitchString) =>
        pitchString.Split(',')
            .Select(s => double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.0)
            .ToList();

    private static bool TryGetValue(string line, string key, out string? value)
    {
        value = null;
        var prefix = key + "=";
        if (!line.StartsWith(prefix)) return false;
        var eqIndex = line.IndexOf('=');
        if (eqIndex < 0 || eqIndex >= line.Length - 1) return false;
        var raw = line[(eqIndex + 1)..];
        if (string.IsNullOrWhiteSpace(raw)) return false;
        value = raw;
        return true;
    }

    private sealed record FileParseResult(
        string FilePath,
        string FileName,
        List<Note> Notes,
        List<Tempo> Tempos,
        bool IsMode2,
        UtauMode1TrackPitchData? PitchDataMode1,
        UtauMode2TrackPitchData? PitchDataMode2);

    public static (byte[] Data, string FileName, List<ExportNotification> Notifications) Generate(
        Project project, IEnumerable<FeatureConfig>? features = null)
    {
        var featureList = features?.ToList() ?? [];
        var notifications = new List<ExportNotification>();

        if (project.TimeSignatures.Any(ts =>
            ts.Numerator != Constants.DefaultMeterHigh || ts.Denominator != Constants.DefaultMeterLow))
        {
            notifications.Add(new ExportNotification.TimeSignatureIgnored());
        }

        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var track in project.Tracks)
            {
                var content = GenerateTrackContent(project, track, featureList);
                var contentBytes = ShiftJis.GetBytes(content);
                var trackNameSafe = GetSafeFileName(track.Name);
                var entryName = $"{project.Name}_{track.Id + 1}_{trackNameSafe}.{Format.Ust.Extension}";
                var entry = archive.CreateEntry(entryName);
                using var entryStream = entry.Open();
                entryStream.Write(contentBytes);
            }
        }

        return (zipStream.ToArray(), project.Name + ".zip", notifications);
    }

    public static void GenerateFile(Project project, string filePath, IEnumerable<FeatureConfig>? features = null)
    {
        var (data, _, _) = Generate(project, features);
        File.WriteAllBytes(filePath, data);
    }

    private static string GenerateTrackContent(Project project, Track track, List<FeatureConfig> features)
    {
        var sb = new StringBuilder();

        void AppendLine(string line)
        {
            sb.Append(line);
            sb.Append(UstConstants.LineSeparator);
        }

        AppendLine("[#VERSION]");
        AppendLine("UST Version1.2");
        AppendLine("[#SETTING]");
        var bpm = project.Tempos[0].Bpm.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        AppendLine($"Tempo={bpm}");
        AppendLine("Tracks=1");
        AppendLine($"ProjectName={track.Name}");
        AppendLine("Mode2=True");

        var tickPos = 0L;
        var restCount = 0;
        int? nextTempoIndex = 0;

        void IncreaseNextTempoIndex()
        {
            nextTempoIndex = nextTempoIndex.HasValue
                ? nextTempoIndex.Value + 1 < project.Tempos.Count ? nextTempoIndex.Value + 1 : null
                : null;
        }

        Tempo? GetNextTempo() => nextTempoIndex.HasValue ? project.Tempos[nextTempoIndex.Value] : null;

        IncreaseNextTempoIndex();

        var pitchDataMode1 = features.Contains(Feature.ConvertPitch)
            ? UtauMode1PitchConversion.PitchToUtauMode1Track(track.Pitch, track.Notes)
            : null;
        var pitchDataMode2 = features.Contains(Feature.ConvertPitch)
            ? UtauMode2PitchConversion.PitchToUtauMode2Track(track.Pitch, track.Notes, project.Tempos)
            : null;

        for (var i = 0; i < track.Notes.Count; i++)
        {
            var note = track.Notes[i];

            if (tickPos < note.TickOn)
            {
                var nextTempo = GetNextTempo();
                var restOn = tickPos;
                string? noteBpm = null;

                if (nextTempo is not null && nextTempo.TickPosition >= restOn && nextTempo.TickPosition < note.TickOn)
                {
                    var restNoteNumber = (note.Id + restCount).ToString().PadLeft(4, '0');
                    AppendLine($"[#{restNoteNumber}]");
                    AppendLine($"Length={nextTempo.TickPosition - restOn}");
                    AppendLine("Lyric=R");
                    AppendLine("NoteNum=60");
                    AppendLine("PreUtterance=");
                    restCount++;
                    restOn = nextTempo.TickPosition;
                    noteBpm = nextTempo.Bpm.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    IncreaseNextTempoIndex();
                }

                var restNoteNumber2 = (note.Id + restCount).ToString().PadLeft(4, '0');
                AppendLine($"[#{restNoteNumber2}]");
                AppendLine($"Length={note.TickOn - restOn}");
                AppendLine("Lyric=R");
                AppendLine("NoteNum=60");
                if (noteBpm is not null)
                    AppendLine($"Tempo={noteBpm}");
                AppendLine("PreUtterance=");
                restCount++;
            }

            var nextTempo2 = GetNextTempo();
            string? noteBpm2 = null;
            if (nextTempo2 is not null && nextTempo2.TickPosition >= note.TickOn && nextTempo2.TickPosition < note.TickOff)
            {
                noteBpm2 = nextTempo2.Bpm.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                IncreaseNextTempoIndex();
            }

            var noteNumber = (note.Id + restCount).ToString().PadLeft(4, '0');
            AppendLine($"[#{noteNumber}]");
            AppendLine($"Length={note.Length}");
            AppendLine($"Lyric={note.Lyric}");
            AppendLine($"NoteNum={note.Key}");
            if (noteBpm2 is not null)
                AppendLine($"Tempo={noteBpm2}");
            AppendLine("PreUtterance=");

            if (features.Contains(Feature.ConvertPitch))
            {
                AppendLine("PBType=5");
                var pitchString = MakeMode1PitchDataString(pitchDataMode1?.Notes.ElementAtOrDefault(i));
                if (pitchString is not null)
                    AppendLine($"PitchBend={pitchString}");
                else
                    AppendLine("PitchBend=");
                AppendLine("PBStart=0");

                var mode2Pitch = pitchDataMode2?.Notes.ElementAtOrDefault(i);
                AppendLine($"PBS={FormatDouble(mode2Pitch?.Start)}");
                AppendLine($"PBW=1,{string.Join(",", mode2Pitch?.Widths.Select(w => FormatDouble(w)) ?? [])}");
                AppendLine($"PBY={FormatDouble(mode2Pitch?.StartShift)},{string.Join(",", mode2Pitch?.Shifts.Select(s => FormatDouble(s)) ?? [])}");
                AppendLine($"PBM={string.Join(",", mode2Pitch?.CurveTypes ?? [])}");

                if (mode2Pitch?.VibratoParams is not null)
                {
                    var v = mode2Pitch.VibratoParams;
                    var vibratoText = string.Join(",",
                    [
                        FormatDouble(v.Length), FormatDouble(v.Period), FormatDouble(v.Depth),
                        FormatDouble(v.FadeIn), FormatDouble(v.FadeOut), FormatDouble(v.PhaseShift),
                        FormatDouble(v.Shift)
                    ]);
                    AppendLine(vibratoText);
                }
            }

            tickPos = note.TickOff;
        }

        AppendLine("[#TRACKEND]");
        return sb.ToString();
    }

    private static string? MakeMode1PitchDataString(UtauMode1NotePitchData? notePitch)
    {
        if (notePitch?.PitchPoints is null) return null;
        return string.Join(",", notePitch.PitchPoints.Select(p => ((int)p).ToString()));
    }

    private static string FormatDouble(double? value) =>
        value is null ? "" : value.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture);

    private static string GetSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? "_" : c.ToString()));
    }
}
