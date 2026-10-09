namespace UtaFormatix.Core.Models;

public sealed record ImportParams(
    bool SimpleImport = false,
    bool MultipleMode = false,
    string DefaultLyric = "あ");
