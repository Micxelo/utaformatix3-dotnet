namespace UtaFormatix.Core.IO;

public static class AtomicFile
{
    public static void Write(string path, Action<string> write)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var temporary = Path.Combine(directory,
            $".{Path.GetFileNameWithoutExtension(fullPath)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(fullPath)}");
        try
        {
            write(temporary);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
