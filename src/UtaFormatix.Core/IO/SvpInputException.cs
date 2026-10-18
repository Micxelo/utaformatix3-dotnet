namespace UtaFormatix.Core.IO;

public sealed class SvpInputException(string code, string path, string message)
    : ProjectInputException(code, path, message);
