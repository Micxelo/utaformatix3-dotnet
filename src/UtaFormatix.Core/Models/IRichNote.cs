namespace UtaFormatix.Core.Models;

public interface IRichNote<out T> where T : IRichNote<T>
{
    Note Note { get; }
    T CopyWithNote(Note note);
}
