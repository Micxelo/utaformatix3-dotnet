namespace UtaFormatix.Core.IO;

public sealed class S5pInputException(string code, string path, string message)
    : ProjectInputException(code, path, message);
