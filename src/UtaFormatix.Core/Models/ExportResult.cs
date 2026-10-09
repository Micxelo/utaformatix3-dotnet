namespace UtaFormatix.Core.Models;

public sealed record ExportResult(
    byte[] Data,
    string FileName,
    List<ExportNotification> Notifications);
