using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;
using UtaFormatix.Core.Process.Pitch;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.IO;

public static class Ust
{
    public static Project Parse(string filePath, ImportParams? importParams = null) =>
        Parse([filePath], importParams);

    public static Project Parse(string[] filePaths, ImportParams? importParams = null)
    {
        var parameters = importParams ?? new ImportParams();
        var budget = new UstPitchBudget(parameters.Ust?.MaxPitchSamples ?? new UstImportOptions().MaxPitchSamples);
        budget.Spend(0, "$.ust.maxPitchSamples");
        var results = filePaths.Select(path => ParseFile(path, parameters.Ust, budget)).ToList();

        var projectName = filePaths.Length == 1
            ? Path.GetFileNameWithoutExtension(filePaths[0])
            : $"{Path.GetFileNameWithoutExtension(filePaths[0])} ({filePaths.Length - 1} more)";

        var tracks = new List<Track>();
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            PitchModel? pitch = null;
            var context = new UstPitchContext(budget, parameters.SimpleImport,
                (index, field) => result.PitchSources[index].At(field));
            pitch = result.IsMode2
                ? UtauMode2PitchConversion.PitchFromUtauMode2Track(result.PitchDataMode2, result.Notes,
                    result.Tempos.Count > 0 ? result.Tempos : [new Tempo(0, Constants.DefaultBpm)], context)
                : UtauMode1PitchConversion.PitchFromUtauMode1Track(result.PitchDataMode1, result.Notes, context);

            tracks.Add(new Track(i, result.FileName, result.Notes, pitch).ValidateNotes());
        }

        var warnings = results.SelectMany(r => r.Warnings).ToList();
        var tempos = ResolveTempos(results, warnings);

        return new Project(
            Format: Format.Ust,
            Name: projectName,
            Tracks: tracks,
            TimeSignatures: [new TimeSignature(0, Constants.DefaultMeterHigh, Constants.DefaultMeterLow)],
            Tempos: tempos,
            MeasurePrefix: 0,
            ImportWarnings: warnings);
    }

    private static List<Tempo> ResolveTempos(
        List<FileParseResult> results, List<ImportWarning> warnings)
    {
        var firstWithTempos = results.FirstOrDefault(r => r.Tempos.Count > 0);
        if (firstWithTempos is null)
        {
            warnings.Add(new ImportWarning.TempoNotFound());
            return [new Tempo(0, Constants.DefaultBpm)];
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

        return tempos;
    }

    private static FileParseResult ParseFile(string filePath, UstImportOptions? options, UstPitchBudget budget)
    {
        var lines = UstEncoding.ReadFile(filePath, options)
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        var lengths = UstInput.Validate(lines, filePath, budget);
        var warnings = new List<ImportWarning>();

        string? projectName = null;
        var notes = new List<Note>();
        var notePitchDataListMode1 = new List<UtauMode1NotePitchData>();
        var notePitchDataListMode2 = new List<UtauMode2NotePitchData>();
        var pitchSources = new List<PitchSource>();
        var pendingPitchLines = new Dictionary<string, int>();
        var tempos = new Dictionary<long, double>();
        var tempoLines = new Dictionary<long, int>();
        var isHeader = true;
        var time = 0L;
        int? pendingNoteKey = null;
        string? pendingNoteLyric = null;
        long? pendingNoteTickOn = null;
        long? pendingNoteTickOff = null;
        var isMode2 = false;
        double? pendingBpm = null;
        var pendingBpmLine = 0;
        List<double>? pendingPitchBend = null;
        (double Start, double? StartShift)? pendingPBS = null;
        List<double>? pendingPBW = null;
        List<double>? pendingPBY = null;
        List<string>? pendingPBM = null;
        List<double>? pendingVBR = null;
        string? pendingSection = null;
        var pendingSectionLine = 0;
        int? pendingLyricLine = null;
        int? pendingKeyLine = null;
        int? pendingShiftLine = null;
        int? pendingCurveLine = null;
        var pendingShiftCount = 0;
        var pendingCurveCount = 0;
        var pendingRest = false;
        var unterminatedSection = false;
        var lastSectionLine = 0;

        void Warn(string code, int lineNumber, string field, string message) =>
            warnings.Add(new ImportWarning.InputDiagnostic(code, $"{filePath}:{lineNumber + 1} ({field})", message));

        ProjectInputException Fail(string code, int lineNumber, string field, string message) =>
            new(code, $"{filePath}:{lineNumber + 1} ({field})", message);

        double GetCurrentTempo() =>
            tempos.Count > 0 ? tempos.MaxBy(kv => kv.Key).Value : Constants.DefaultBpm;

        void FinishNote()
        {
            if (pendingNoteKey is not null && pendingNoteLyric is not null &&
                pendingNoteTickOn is not null && pendingNoteTickOff is not null)
            {
                if (isMode2)
                {
                    if (pendingPBS is null && pendingPBY?.Any(v => v != 0.0) == true)
                        throw Fail("UST_PITCH", pendingShiftLine ?? pendingSectionLine, "PBY",
                            "Non-zero pitch shifts require a valid PBS start; the curve cannot be recovered without losing data.");
                    if (pendingPBS is not null)
                    {
                        var widthCount = pendingPBW?.Count ?? 0;
                        if (pendingShiftCount > widthCount)
                            throw Fail("UST_PITCH", pendingShiftLine ?? pendingSectionLine, $"PBY[{widthCount}]",
                                "Pitch shifts exceed the number of PBW intervals; extra shifts cannot be assigned a time.");
                        if (pendingCurveCount > widthCount)
                            throw Fail("UST_PITCH", pendingCurveLine ?? pendingSectionLine, $"PBM[{widthCount}]",
                                "Pitch curve types exceed the number of PBW intervals.");
                    }
                }
                notes.Add(new Note(notes.Count, pendingNoteKey.Value, pendingNoteLyric,
                    pendingNoteTickOn.Value, pendingNoteTickOff.Value));
                notePitchDataListMode2.Add(new UtauMode2NotePitchData(GetCurrentTempo(),
                    pendingPBS?.Start, pendingPBS?.StartShift, pendingPBW ?? [], pendingPBY ?? [],
                    pendingPBM ?? [], ParseVibrato(pendingVBR)));
                notePitchDataListMode1.Add(new UtauMode1NotePitchData(pendingPitchBend));
                pitchSources.Add(new PitchSource(filePath, pendingSectionLine, pendingPitchLines));
            }
            else if (pendingSection is not null && !pendingRest)
            {
                var missing = new List<string>();
                if (pendingNoteLyric is null) missing.Add("Lyric is missing or blank");
                if (pendingNoteKey is null) missing.Add("NoteNum is missing, blank or not a valid Int32 integer");
                if (pendingNoteTickOn is null || pendingNoteTickOff is null) missing.Add("Length is missing");
                var line = pendingNoteLyric is null ? pendingLyricLine : pendingKeyLine;
                var field = pendingNoteLyric is null ? "Lyric" : pendingNoteKey is null ? "NoteNum" : "Length";
                throw Fail("UST_REQUIRED", line ?? pendingSectionLine, $"{pendingSection}.{field}",
                    $"Cannot recover note: {string.Join("; ", missing)}.");
            }
        }

        List<double> ParsePitchNumbers(string text, string field, int lineNumber, out int specifiedCount, bool allowEmpty = true)
        {
            pendingPitchLines[field] = lineNumber;
            var cells = text.Split(',');
            var values = new List<double>(cells.Length);
            specifiedCount = 0;
            for (var i = 0; i < cells.Length; i++)
            {
                if (double.TryParse(cells[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    if (field == "PBW" && value < 0)
                        throw Fail("UST_PITCH", lineNumber, $"{field}[{i}]", "Pitch interval widths cannot be negative.");
                    values.Add(value);
                    specifiedCount = i + 1;
                }
                else if (allowEmpty && string.IsNullOrWhiteSpace(cells[i]))
                {
                    // Preserve the existing zero shorthand and trailing placeholders.
                    values.Add(0.0);
                }
                else
                    throw Fail("UST_INVALID_VALUE", lineNumber, $"{field}[{i}]",
                        "Expected a numeric pitch value; replacing or removing this cell would change the curve.");
            }
            return values;
        }

        for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
        {
            var line = lines[lineNumber];
            if (string.IsNullOrWhiteSpace(line)) continue;
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
                        tempoLines[0] = lineNumber;
                    }
                    else if (pendingNoteTickOn is not null)
                    {
                        tempos[pendingNoteTickOn.Value] = bpm;
                        tempoLines[pendingNoteTickOn.Value] = lineNumber;
                    }
                    else
                    {
                        pendingBpm = bpm;
                        pendingBpmLine = lineNumber;
                    }
                }
                else
                    throw Fail("UST_INVALID_VALUE", lineNumber, "Tempo", "Expected a numeric Tempo; the intended speed cannot be recovered.");
            }

            if (TryGetValue(line, "Mode2", out var modeVal))
            {
                if (!isHeader)
                    throw Fail("UST_SECTION", lineNumber, "Mode2", "Mode2 belongs to the project settings before note sections.");
                if (!bool.TryParse(modeVal, out isMode2))
                    throw Fail("UST_INVALID_VALUE", lineNumber, "Mode2", "Mode2 must be True or False.");
                if (modeVal != (isMode2 ? "True" : "False"))
                    Warn("UST_NORMALIZED", lineNumber, "Mode2", "Normalized the boolean setting without changing its meaning.");
            }

            if (UstInput.IsSection(line))
            {
                FinishNote();

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
                pendingPitchLines = new Dictionary<string, int>();
                pendingSection = UstInput.IsNoteSection(line) ? line : null;
                pendingSectionLine = lineNumber;
                pendingLyricLine = null;
                pendingKeyLine = null;
                pendingShiftLine = null;
                pendingCurveLine = null;
                pendingShiftCount = 0;
                pendingCurveCount = 0;
                pendingRest = false;
                if (pendingSection is not null)
                {
                    isHeader = false;
                    unterminatedSection = true;
                    lastSectionLine = lineNumber;
                }
                if (line == "[#TRACKEND]") unterminatedSection = false;
            }

            if (lengths.TryGetValue(lineNumber, out var length))
            {
                pendingNoteTickOn = time;
                if (pendingBpm is not null)
                {
                    tempos[time] = pendingBpm.Value;
                    tempoLines[time] = pendingBpmLine;
                    pendingBpm = null;
                }
                time = checked(time + length);
                pendingNoteTickOff = time;
            }

            if (line.StartsWith("Lyric=", StringComparison.Ordinal))
            {
                pendingLyricLine = lineNumber;
                pendingNoteLyric = null;
                pendingRest = false;
            }
            if (TryGetValue(line, "Lyric", out var lyricVal))
            {
                pendingRest = lyricVal is "R" or "r";
                pendingNoteLyric = pendingRest ? null : lyricVal;
            }

            if (line.StartsWith("NoteNum=", StringComparison.Ordinal))
            {
                pendingKeyLine = lineNumber;
                pendingNoteKey = null;
            }
            if (TryGetValue(line, "NoteNum", out var noteNumVal))
            {
                if (int.TryParse(noteNumVal, out var key))
                    pendingNoteKey = key;
                else
                    throw Fail("UST_INVALID_VALUE", lineNumber, "NoteNum", "NoteNum must be an Int32 integer; the intended key cannot be recovered.");
            }

            if (isMode2)
            {
                if (TryGetValue(line, "PBS", out var pbsVal))
                {
                    pendingPitchLines["PBS"] = lineNumber;
                    var cells = pbsVal.Split([';', ',']);
                    if (double.TryParse(cells[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var start))
                    {
                        double? startShift = cells.Length > 1
                            ? double.TryParse(cells[1], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var ss) ? ss : null
                            : null;
                        pendingPBS = (start, startShift);
                        if (cells.Length > 1 && !string.IsNullOrWhiteSpace(cells[1]) && startShift is null)
                            throw Fail("UST_INVALID_VALUE", lineNumber, "PBS[1]", "Expected a numeric PBS start shift.");
                    }
                    else
                        throw Fail("UST_INVALID_VALUE", lineNumber, "PBS[0]", "Expected a numeric PBS start.");
                    if (cells.Skip(2).Any(c => !string.IsNullOrWhiteSpace(c)))
                        throw Fail("UST_PITCH", lineNumber, "PBS[2]", "PBS supports only a start and optional start shift; extra values cannot be recovered.");
                }

                if (TryGetValue(line, "PBW", out var pbwVal))
                    pendingPBW = ParsePitchNumbers(pbwVal, "PBW", lineNumber, out _);

                if (TryGetValue(line, "PBY", out var pbyVal))
                {
                    pendingPBY = ParsePitchNumbers(pbyVal, "PBY", lineNumber, out pendingShiftCount);
                    pendingShiftLine = lineNumber;
                }

                if (TryGetValue(line, "PBM", out var pbmVal))
                {
                    pendingPBM = pbmVal.Split(',').ToList();
                    var invalidCurve = pendingPBM.FindIndex(c => c is not ("" or "s" or "j" or "r"));
                    if (invalidCurve >= 0)
                        throw Fail("UST_PITCH", lineNumber, $"PBM[{invalidCurve}]", "Unknown pitch curve type; interpolation cannot be recovered.");
                    pendingCurveLine = lineNumber;
                    pendingCurveCount = pendingPBM.FindLastIndex(c => c != "") + 1;
                }

                if (TryGetValue(line, "VBR", out var vbrVal))
                {
                    pendingVBR = ParsePitchNumbers(vbrVal, "VBR", lineNumber, out _, allowEmpty: false);
                    if (pendingVBR.Count < 2)
                        throw Fail("UST_PITCH", lineNumber, "VBR", "Vibrato data requires at least two numeric parameters.");
                    else if (pendingVBR.Count > 7)
                        throw Fail("UST_PITCH", lineNumber, "VBR[7]", "Vibrato data supports at most seven parameters.");
                    if (pendingVBR[1] == 0.0)
                    {
                        Warn("UST_VIBRATO_IGNORED", lineNumber, "VBR[1]",
                            "Ignored vibrato because its period is zero.");
                        pendingVBR = null;
                    }
                }
            }
            else
            {
                if (TryGetValue(line, "Piches", out var pichesVal) && pendingPitchBend is null)
                    pendingPitchBend = ParsePitchNumbers(pichesVal, "Piches", lineNumber, out _);
                else if (TryGetValue(line, "Pitches", out var pitchesVal) && pendingPitchBend is null)
                    pendingPitchBend = ParsePitchNumbers(pitchesVal, "Pitches", lineNumber, out _);
                else if (TryGetValue(line, "PitchBend", out var pitchBendVal) && pendingPitchBend is null)
                    pendingPitchBend = ParsePitchNumbers(pitchBendVal, "PitchBend", lineNumber, out _);
            }
        }

        if (unterminatedSection)
        {
            FinishNote();
            Warn("UST_MISSING_TRACKEND", lastSectionLine, "TRACKEND",
                "Missing [#TRACKEND]; completed the final section at end of file.");
        }

        var tempoList = tempos.Select(kv => new Tempo(kv.Key, kv.Value))
            .OrderBy(t => t.TickPosition)
            .ToList();
        UstInput.ValidateTempoTimeMapping(tempoList, time,
            i => $"{filePath}:{tempoLines[tempoList[i].TickPosition] + 1} (Tempo)");

        var pitchDataMode1 = notePitchDataListMode1.Count > 0
            ? new UtauMode1TrackPitchData(notePitchDataListMode1.Cast<UtauMode1NotePitchData?>().ToList())
            : null;
        var pitchDataMode2 = notePitchDataListMode2.Count > 0
            ? new UtauMode2TrackPitchData(notePitchDataListMode2.Cast<UtauMode2NotePitchData?>().ToList())
            : null;

        return new FileParseResult(
            filePath,
            Path.GetFileNameWithoutExtension(filePath),
            notes,
            tempoList,
            isMode2,
            pitchDataMode1,
            pitchDataMode2,
            pitchSources,
            warnings);
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

    private static bool TryGetValue(string line, string key, [NotNullWhen(true)] out string? value)
    {
        value = null;
        var prefix = key + "=";
        var isLyric = key == "Lyric";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var eqIndex = line.IndexOf('=');
        if (eqIndex < 0 || eqIndex >= line.Length - 1) return false;
        var raw = line[(eqIndex + 1)..];
        var isBlank = isLyric
            ? LyricWhitespace.IsNullOrBlank(raw)
            : string.IsNullOrWhiteSpace(raw);
        if (isBlank) return false;
        value = raw;
        return true;
    }

    private sealed record PitchSource(string File, int SectionLine, Dictionary<string, int> Lines)
    {
        public string At(string field)
        {
            var index = field.IndexOf('[');
            var key = index < 0 ? field : field[..index];
            if (key == "PitchBend" && !Lines.ContainsKey(key))
                key = Lines.ContainsKey("Piches") ? "Piches" : Lines.ContainsKey("Pitches") ? "Pitches" : key;
            var line = Lines.GetValueOrDefault(key, SectionLine);
            if (key != (index < 0 ? field : field[..index])) field = key + (index < 0 ? "" : field[index..]);
            return $"{File}:{line + 1} ({field})";
        }
    }

    private sealed record FileParseResult(
        string FilePath,
        string FileName,
        List<Note> Notes,
        List<Tempo> Tempos,
        bool IsMode2,
        UtauMode1TrackPitchData? PitchDataMode1,
        UtauMode2TrackPitchData? PitchDataMode2,
        List<PitchSource> PitchSources,
        List<ImportWarning> Warnings);

    public static (byte[] Data, string FileName, List<ExportNotification> Notifications) Generate(
        Project project, IEnumerable<FeatureConfig>? features = null)
        => Generate(project, features, null);

    public static (byte[] Data, string FileName, List<ExportNotification> Notifications) Generate(
        Project project, IEnumerable<FeatureConfig>? features, UstExportOptions? options)
    {
        UstInput.ValidateForExport(project);
        var budget = new UstPitchBudget(options?.MaxPitchSamples ?? new UstExportOptions().MaxPitchSamples);
        budget.Spend(0, "$.ust.maxPitchSamples");
        var encoding = UstEncoding.ForExport(options);
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
            for (var trackIndex = 0; trackIndex < project.Tracks.Count; trackIndex++)
            {
                var track = project.Tracks[trackIndex];
                var context = new UstPitchContext(budget, false,
                    (note, field) => $"$.tracks[{trackIndex}].pitch ({field}, note {note})");
                var content = GenerateTrackContent(project, track, featureList, context);
                var contentBytes = UstEncoding.Encode(content, encoding, trackIndex);
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
        => GenerateFile(project, filePath, features, null);

    public static void GenerateFile(Project project, string filePath, IEnumerable<FeatureConfig>? features, UstExportOptions? options)
    {
        var (data, _, _) = Generate(project, features, options);
        File.WriteAllBytes(filePath, data);
    }

    private static string GenerateTrackContent(Project project, Track track, List<FeatureConfig> features, UstPitchContext context)
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
        var bpm = FormatTempo(project.Tempos[0].Bpm);
        AppendLine($"Tempo={bpm}");
        AppendLine("Tracks=1");
        AppendLine($"ProjectName={track.Name}");
        AppendLine("Mode2=True");

        var tickPos = 0L;
        var restCount = 0;
        string? deferredBpm = null;
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
            ? UtauMode1PitchConversion.PitchToUtauMode1Track(track.Pitch, track.Notes, context)
            : null;
        var pitchDataMode2 = features.Contains(Feature.ConvertPitch)
            ? UtauMode2PitchConversion.PitchToUtauMode2Track(track.Pitch, track.Notes, project.Tempos, context)
            : null;

        for (var i = 0; i < track.Notes.Count; i++)
        {
            var note = track.Notes[i];

            if (tickPos < note.TickOn)
            {
                var nextTempo = GetNextTempo();
                var restOn = tickPos;
                var noteBpm = deferredBpm;
                deferredBpm = null;

                while (nextTempo is not null && nextTempo.TickPosition >= restOn && nextTempo.TickPosition < note.TickOn)
                {
                    if (nextTempo.TickPosition > restOn)
                    {
                        var restNoteNumber = (note.Id + restCount).ToString().PadLeft(4, '0');
                        AppendLine($"[#{restNoteNumber}]");
                        AppendLine($"Length={nextTempo.TickPosition - restOn}");
                        AppendLine("Lyric=R");
                        AppendLine("NoteNum=60");
                        if (noteBpm is not null)
                            AppendLine($"Tempo={noteBpm}");
                        AppendLine("PreUtterance=");
                        restCount++;
                    }
                    restOn = nextTempo.TickPosition;
                    noteBpm = FormatTempo(nextTempo.Bpm);
                    IncreaseNextTempoIndex();
                    nextTempo = GetNextTempo();
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
            var firstTempoTick = nextTempo2?.TickPosition;
            var noteBpm2 = deferredBpm;
            deferredBpm = null;
            while (nextTempo2 is not null && nextTempo2.TickPosition >= note.TickOn && nextTempo2.TickPosition < note.TickOff)
            {
                var nextBpm = FormatTempo(nextTempo2.Bpm);
                // Keep the first tempo position at the note onset; duplicates
                // at that position use the last value. Later changes cannot be
                // placed inside a sung note, so restore their final BPM at the
                // next section boundary without blocking subsequent markers.
                if (nextTempo2.TickPosition == firstTempoTick)
                    noteBpm2 = nextBpm;
                else
                    deferredBpm = nextBpm;
                IncreaseNextTempoIndex();
                nextTempo2 = GetNextTempo();
            }
            if (deferredBpm == noteBpm2) deferredBpm = null;

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
        if (notePitch.PitchPoints.Any(p => !double.IsFinite(p)))
            throw new ProjectInputException("UST_NUMBER", "$.export.pitch", "Converted pitch must be finite.");
        return string.Join(",", notePitch.PitchPoints.Select(p => ((int)p).ToString()));
    }

    internal static string FormatTempo(double bpm)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (!double.IsFinite(bpm) || Math.Abs(bpm) >= 1e21)
            return bpm.ToString("G", culture).Replace('E', 'e');
        if (bpm == 0) return "0.00";

        // Match Number.toFixed(2) using the exact binary value. Scaling a double
        // by 100 first can erase which side of a midpoint the value lies on.
        var bits = BitConverter.DoubleToUInt64Bits(Math.Abs(bpm));
        var exponent = (int)((bits >> 52) & 0x7ff);
        var significand = bits & 0x000f_ffff_ffff_ffff;
        if (exponent != 0) significand |= 1UL << 52;
        var shift = exponent == 0 ? -1074 : exponent - 1075;
        var cents = new BigInteger(significand) * 100;
        if (shift >= 0)
        {
            cents <<= shift;
        }
        else
        {
            var divisor = BigInteger.One << -shift;
            cents = BigInteger.DivRem(cents, divisor, out var remainder);
            if (remainder * 2 >= divisor) cents += BigInteger.One;
        }

        var digits = cents.ToString(culture);
        var text = digits.Length <= 2 ? "0." + digits.PadLeft(2, '0') : digits.Insert(digits.Length - 2, ".");
        return bpm < 0 ? "-" + text : text;
    }

    private static string FormatDouble(double? value)
    {
        if (value is { } number && !double.IsFinite(number))
            throw new ProjectInputException("UST_NUMBER", "$.export.pitch", "Converted pitch must be finite.");
        return value is null ? "" : value.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string GetSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? "_" : c.ToString()));
    }
}
