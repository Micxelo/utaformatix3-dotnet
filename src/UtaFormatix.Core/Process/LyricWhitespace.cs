namespace UtaFormatix.Core.Process;

internal static class LyricWhitespace
{
    // Match Kotlin's blank predicate: NEXT LINE is text, while U+001C-U+001F are blank.
    public static bool IsNullOrBlank(string? lyric) =>
        lyric is null || lyric.All(IsWhitespace);

    public static string Trim(string lyric)
    {
        var start = 0;
        var end = lyric.Length;
        while (start < end && IsWhitespace(lyric[start])) start++;
        while (end > start && IsWhitespace(lyric[end - 1])) end--;
        return lyric[start..end];
    }

    private static bool IsWhitespace(char c) =>
        c is >= '\u001c' and <= '\u001f' ||
        c != '\u0085' && char.IsWhiteSpace(c);
}
