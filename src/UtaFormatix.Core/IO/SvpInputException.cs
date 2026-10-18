namespace UtaFormatix.Core.IO;

public sealed class SvpInputException(string code, string path, string message)
    : Exception($"{code} at {path}: {message}")
{
    public string Code { get; } = code;
    public string FieldPath { get; } = path;
    public string Reason { get; } = message;
}
