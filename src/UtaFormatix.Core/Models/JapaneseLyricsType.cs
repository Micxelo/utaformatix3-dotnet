namespace UtaFormatix.Core.Models;

public enum JapaneseLyricsType
{
    Unknown,
    RomajiCv,
    RomajiVcv,
    KanaCv,
    KanaVcv,
}

public static class JapaneseLyricsTypeExtensions
{
    public static bool IsRomaji(this JapaneseLyricsType type) =>
        type is JapaneseLyricsType.RomajiCv or JapaneseLyricsType.RomajiVcv;

    public static bool IsCv(this JapaneseLyricsType type) =>
        type is JapaneseLyricsType.RomajiCv or JapaneseLyricsType.KanaCv;

    public static JapaneseLyricsType? FindBestConversionTarget(
        this JapaneseLyricsType type,
        Format outputFormat)
    {
        if (outputFormat.SuggestedLyricType is { } suggested)
            return suggested;

        var options = outputFormat.PossibleLyricsTypes;
        if (options.Contains(type))
            return type;

        var sameRomaji = options.FirstOrDefault(o => o.IsRomaji() == type.IsRomaji());
        if (sameRomaji != default || options.Count == 0)
            return sameRomaji == default ? null : sameRomaji;

        var sameCv = options.FirstOrDefault(o => o.IsCv() == type.IsCv());
        if (sameCv != default)
            return sameCv;

        return options.FirstOrDefault();
    }
}
