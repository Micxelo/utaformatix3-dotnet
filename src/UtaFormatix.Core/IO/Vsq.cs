using UtaFormatix.Core.Models;

namespace UtaFormatix.Core.IO;

public static class Vsq
{
    public static Project Parse(string filePath, ImportParams? importParams = null) =>
        VocaloidMid.Parse(filePath, importParams, Format.Vsq);

    public static (byte[] Data, string FileName, List<ExportNotification> Notifications) Generate(
        Project project, IEnumerable<FeatureConfig>? features = null) =>
        VocaloidMid.Generate(project, features, Format.Vsq);

    public static void GenerateFile(Project project, string filePath,
        IEnumerable<FeatureConfig>? features = null) =>
        VocaloidMid.GenerateFile(project, filePath, features, Format.Vsq);
}
