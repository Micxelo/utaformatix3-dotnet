namespace UtaFormatix.Core.Models;

public sealed record Track(
    int Id,
    string Name,
    List<Note> Notes,
    Pitch? Pitch = null);
