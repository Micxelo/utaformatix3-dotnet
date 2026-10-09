namespace UtaFormatix.Core.Models;

public sealed record Note(
    int Id,
    int Key,
    string Lyric,
    long TickOn,
    long TickOff,
    string? Phoneme = null) : IRichNote<Note>
{
    public long Length => TickOff - TickOn;

    Note IRichNote<Note>.Note => this;

    public Note CopyWithNote(Note note) => note;
}
