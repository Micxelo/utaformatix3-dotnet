namespace UtaFormatix.Core.IO;

public class ProjectInputException(string code, string path, string message)
    : Exception($"{code} at {path}: {message}")
{
    public string Code { get; } = code;
    public string FieldPath { get; } = path;
    public string Reason { get; } = message;
}
