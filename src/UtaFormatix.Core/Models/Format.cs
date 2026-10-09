namespace UtaFormatix.Core.Models;

public sealed class Format
{
    private Format(
        string name,
        string extension,
        List<string>? otherExtensions = null,
        bool multipleFile = false,
        List<JapaneseLyricsType>? possibleLyricsTypes = null,
        JapaneseLyricsType? suggestedLyricType = null,
        List<Feature>? availableFeaturesForGeneration = null,
        string? alias = null)
    {
        Name = name;
        Extension = extension;
        OtherExtensions = otherExtensions ?? [];
        MultipleFile = multipleFile;
        PossibleLyricsTypes = possibleLyricsTypes ?? [];
        SuggestedLyricType = suggestedLyricType;
        AvailableFeaturesForGeneration = availableFeaturesForGeneration ?? [];
        Alias = alias;
    }

    public string Name { get; }
    public string Extension { get; }
    public List<string> OtherExtensions { get; }
    public bool MultipleFile { get; }
    public List<JapaneseLyricsType> PossibleLyricsTypes { get; }
    public JapaneseLyricsType? SuggestedLyricType { get; }
    public List<Feature> AvailableFeaturesForGeneration { get; }
    public string? Alias { get; }

    public string DisplayName => Alias ?? Name;
    public List<string> AllExtensions => [Extension, .. OtherExtensions];
    public string GetFileName(string name) => $"{name}.{Extension}";

    public bool MatchExtension(string ext) =>
        string.Equals(ext, Extension, StringComparison.OrdinalIgnoreCase) ||
        OtherExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => DisplayName;

    public static readonly Format Vsqx = new(
        nameof(Vsqx), "vsqx",
        possibleLyricsTypes: [JapaneseLyricsType.RomajiCv, JapaneseLyricsType.KanaCv],
        availableFeaturesForGeneration: [Feature.ConvertPitch, Feature.ConvertPhonemes]);

    public static readonly Format Vpr = new(
        nameof(Vpr), "vpr",
        possibleLyricsTypes: [JapaneseLyricsType.RomajiCv, JapaneseLyricsType.KanaCv],
        availableFeaturesForGeneration: [Feature.ConvertPitch, Feature.ConvertPhonemes]);

    public static readonly Format Ust = new(
        nameof(Ust), "ust",
        multipleFile: true,
        possibleLyricsTypes: [JapaneseLyricsType.RomajiCv, JapaneseLyricsType.RomajiVcv, JapaneseLyricsType.KanaCv, JapaneseLyricsType.KanaVcv],
        availableFeaturesForGeneration: [Feature.ConvertPitch]);

    public static readonly Format Ustx = new(
        nameof(Ustx), "ustx",
        possibleLyricsTypes: [JapaneseLyricsType.RomajiCv, JapaneseLyricsType.RomajiVcv, JapaneseLyricsType.KanaCv, JapaneseLyricsType.KanaVcv],
        availableFeaturesForGeneration: [Feature.ConvertPitch, Feature.ConvertPhonemes]);

    public static readonly Format Ccs = new(
        nameof(Ccs), "ccs",
        possibleLyricsTypes: [JapaneseLyricsType.KanaCv],
        availableFeaturesForGeneration: [Feature.ConvertPitch, Feature.ConvertPhonemes]);

    public static readonly Format Svp = new(
        nameof(Svp), "svp",
        possibleLyricsTypes: [JapaneseLyricsType.RomajiCv, JapaneseLyricsType.KanaCv],
        availableFeaturesForGeneration: [Feature.ConvertPitch, Feature.SplitProject, Feature.ConvertPhonemes]);

    public static readonly Format S5p = new(
        nameof(S5p), "s5p",
        possibleLyricsTypes: [JapaneseLyricsType.RomajiCv, JapaneseLyricsType.KanaCv],
        availableFeaturesForGeneration: [Feature.ConvertPitch]);

    public static readonly Format MusicXml = new(
        nameof(MusicXml), "musicxml",
        otherExtensions: ["xml"],
        possibleLyricsTypes: [JapaneseLyricsType.RomajiCv, JapaneseLyricsType.KanaCv],
        suggestedLyricType: JapaneseLyricsType.KanaCv);

    public static readonly Format Dv = new(
        nameof(Dv), "dv",
        possibleLyricsTypes: [JapaneseLyricsType.RomajiCv, JapaneseLyricsType.KanaCv],
        suggestedLyricType: JapaneseLyricsType.RomajiCv,
        availableFeaturesForGeneration: [Feature.ConvertPitch]);

    public static readonly Format Vsq = new(
        nameof(Vsq), "vsq",
        possibleLyricsTypes: [JapaneseLyricsType.RomajiCv, JapaneseLyricsType.KanaCv],
        availableFeaturesForGeneration: [Feature.ConvertPitch, Feature.ConvertPhonemes]);

    public static readonly Format VocaloidMid = new(
        nameof(VocaloidMid), "mid",
        possibleLyricsTypes: [JapaneseLyricsType.RomajiCv, JapaneseLyricsType.KanaCv],
        availableFeaturesForGeneration: [Feature.ConvertPitch],
        alias: "Mid (VOCALOID)");

    public static readonly Format StandardMid = new(
        nameof(StandardMid), "mid",
        possibleLyricsTypes: [JapaneseLyricsType.RomajiCv, JapaneseLyricsType.KanaCv],
        alias: "Mid (Standard)");

    public static readonly Format Ppsf = new(
        nameof(Ppsf), "ppsf",
        possibleLyricsTypes: [JapaneseLyricsType.RomajiCv, JapaneseLyricsType.KanaCv]);

    public static readonly Format Tssln = new(
        nameof(Tssln), "tssln",
        possibleLyricsTypes: [JapaneseLyricsType.KanaCv, JapaneseLyricsType.RomajiCv],
        availableFeaturesForGeneration: [Feature.ConvertPhonemes]);

    public static readonly Format UfData = new(
        nameof(UfData), "ufdata",
        possibleLyricsTypes: [JapaneseLyricsType.RomajiCv, JapaneseLyricsType.RomajiVcv, JapaneseLyricsType.KanaCv, JapaneseLyricsType.KanaVcv],
        availableFeaturesForGeneration: [Feature.ConvertPitch, Feature.ConvertPhonemes]);

    public static IReadOnlyList<Format> Importable { get; } =
    [
        Vsqx, Vpr, Vsq, VocaloidMid, Ust, Ustx, Ccs, MusicXml,
        Svp, S5p, Dv, Ppsf, StandardMid, Tssln, UfData,
    ];

    public static IReadOnlyList<Format> Exportable { get; } =
    [
        Vsqx, Vpr, Vsq, VocaloidMid, Ust, Ustx, Ccs, MusicXml,
        Svp, S5p, Dv, StandardMid, Tssln, UfData,
    ];

    public static IReadOnlyList<Format> VocaloidFormats { get; } =
        [Vsq, Vsqx, VocaloidMid, Vpr];
}
