using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using UtaFormatix.Core.Models;
using UtaFormatix.Core.Process;
using Data = UtaFormatix.Data;

namespace UtaFormatix.Core.IO;

public static class UfData
{
    private static readonly JsonTypeInfo<Data.UfData> UfDataTypeInfo =
        UfDataJsonContext.Default.UfData;

    public static Models.Project Parse(string json, ImportParams? importParams = null)
    {
        var parameters = importParams ?? new ImportParams();
        var document = JsonSerializer.Deserialize(json, UfDataTypeInfo)
            ?? throw new Exceptions.CannotReadFileException();

        var warnings = new List<ImportWarning>();
        if (document.FormatVersion > Data.UfData.CurrentFormatVersion)
        {
            warnings.Add(new ImportWarning.IncompatibleFormatSerializationVersion(
                Data.UfData.CurrentFormatVersion.ToString(),
                document.FormatVersion.ToString()));
        }

        return new Models.Project(
            Format: Format.UfData,
            Name: document.Project.Name,
            Tracks: document.Project.Tracks
                .Select((t, i) => ParseTrack(i, t, parameters))
                .ToList(),
            TimeSignatures: document.Project.TimeSignatures
                ?.Select(ParseTimeSignature).ToList() ?? [],
            Tempos: document.Project.Tempos.Select(ParseTempo).ToList(),
            MeasurePrefix: document.Project.MeasurePrefix,
            ImportWarnings: warnings);
    }

    public static Models.Project ParseFile(string filePath, ImportParams? importParams = null)
    {
        var json = File.ReadAllText(filePath);
        return Parse(json, importParams);
    }

    public static Models.Project ParseStream(Stream stream, ImportParams? importParams = null)
    {
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return Parse(json, importParams);
    }

    public static (byte[] Data, string FileName, List<ExportNotification> Notifications) Generate(
        Models.Project project,
        IEnumerable<FeatureConfig>? features = null)
    {
        var featureList = features?.ToList() ?? [];
        var document = GenerateDocument(project, featureList);
        var json = JsonSerializer.Serialize(document, UfDataTypeInfo);
        var data = System.Text.Encoding.UTF8.GetBytes(json);
        var fileName = Format.UfData.GetFileName(project.Name);

        var notifications = new List<ExportNotification>();
        if (featureList.Contains(Feature.ConvertPitch))
            notifications.Add(new ExportNotification.PitchDataExported());

        return (data, fileName, notifications);
    }

    public static void GenerateFile(
        Models.Project project,
        string filePath,
        IEnumerable<FeatureConfig>? features = null)
    {
        var (data, _, _) = Generate(project, features);
        File.WriteAllBytes(filePath, data);
    }

    public static Data.UfData GenerateDocument(
        Models.Project project,
        List<FeatureConfig> features)
    {
        return new Data.UfData(
            FormatVersion: Data.UfData.CurrentFormatVersion,
            Project: new Data.Project(
                Name: project.Name,
                Tracks: project.Tracks.Select(t => GenerateTrack(t, features)).ToArray(),
                TimeSignatures: project.TimeSignatures.Select(GenerateTimeSignature).ToArray(),
                Tempos: project.Tempos.Select(GenerateTempo).ToArray(),
                MeasurePrefix: project.MeasurePrefix));
    }

    private static Track ParseTrack(int index, Data.Track track, ImportParams parameters)
    {
        var notes = track.Notes.Select((n, i) =>
            new Note(
                Id: i,
                Key: n.Key,
                Lyric: n.Lyric,
                TickOn: n.TickOn,
                TickOff: n.TickOff,
                Phoneme: n.Phoneme))
            .ToList();

        Pitch? pitch = null;
        if (!parameters.SimpleImport && track.Pitch is { } p)
        {
            var data = p.Ticks.Zip(p.Values, (tick, value) => (tick, value)).ToList();
            pitch = new Pitch(data, p.IsAbsolute);
        }

        return new Track(
            Id: index,
            Name: track.Name,
            Notes: notes,
            Pitch: pitch)
            .ValidateNotes();
    }

    private static TimeSignature ParseTimeSignature(Data.TimeSignature ts) =>
        new(ts.MeasurePosition, ts.Numerator, ts.Denominator);

    private static Tempo ParseTempo(Data.Tempo tempo) =>
        new(tempo.TickPosition, tempo.Bpm);

    private static Data.Track GenerateTrack(Track track, List<FeatureConfig> features)
    {
        var notes = track.Notes.Select(n =>
            new Data.Note(n.Key, n.TickOn, n.TickOff, n.Lyric, n.Phoneme))
            .ToArray();

        Data.Pitch? pitch = null;
        if (features.Contains(Feature.ConvertPitch) && track.Pitch is { } p)
        {
            pitch = new Data.Pitch(
                Ticks: p.Data.Select(d => d.Tick).ToArray(),
                Values: p.Data.Select(d => d.Value).ToArray(),
                IsAbsolute: p.IsAbsolute);
        }

        return new Data.Track(track.Name, notes, pitch ?? new Data.Pitch([], [], false));
    }

    private static Data.TimeSignature GenerateTimeSignature(TimeSignature ts) =>
        new(ts.MeasurePosition, ts.Numerator, ts.Denominator);

    private static Data.Tempo GenerateTempo(Tempo tempo) =>
        new(tempo.TickPosition, tempo.Bpm);
}
