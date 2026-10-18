namespace UtaFormatix.Core.Process;

internal static class LyricWhitespace
{
    // Match Kotlin's blank predicate: NEXT LINE is text, while U+001C-U+001F are blank.
    public static bool IsNullOrBlank(string? lyric) =>
        lyric is null || lyric.All(c =>
            c is >= '\u001c' and <= '\u001f' ||
            c != '\u0085' && char.IsWhiteSpace(c));
}
