using UtaFormatix.Core.Models;

namespace UtaFormatix.Core.Exceptions;

public class CannotReadFileException : Exception;

public class EmptyProjectException : Exception
{
    public EmptyProjectException() : base("This format could not take an empty project.") { }
}

public abstract class IllegalFileException : Exception
{
    protected IllegalFileException(string message) : base(message) { }

    public sealed class UnknownVsqVersion : IllegalFileException
    {
        public UnknownVsqVersion() : base("Cannot identify the version of the loaded vsqx file.") { }
    }

    public sealed class XmlRootNotFound : IllegalFileException
    {
        public XmlRootNotFound() : base("The root element is not found in the xml file.") { }
    }

    public sealed class XmlElementNotFound(string elementName)
        : IllegalFileException($"The required element <{elementName}> is not found in the xml file.");

    public sealed class XmlElementValueIllegal(string elementName)
        : IllegalFileException($"The required element <{elementName}> has an illegal value.");

    public sealed class XmlElementAttributeValueIllegal(string attribute, string elementName)
        : IllegalFileException(
            $"The required attribute \"{attribute}\" in element <{elementName}> is missing or has an illegal value.");

    public sealed class IllegalMidiFile : IllegalFileException
    {
        public IllegalMidiFile() : base("Cannot parse this file as a MIDI file.") { }
    }

    public sealed class IllegalTsslnFile : IllegalFileException
    {
        public IllegalTsslnFile() : base("Cannot parse this file as a tssln file.") { }
    }
}

public class IllegalNotePositionException : Exception
{
    public IllegalNotePositionException(Note note, int trackIndex)
        : base(
            $"Failed to import because note with illegal position({note.TickOn}) exists in Track No.{trackIndex + 1}")
    {
    }
}

public class NotesOverlappingException : Exception
{
    public NotesOverlappingException()
        : base("Failed to process because there are notes overlapping with each other.") { }
}

public class UnsupportedFileFormatException : Exception;

public class UnsupportedLegacyPpsfException : UnsupportedFileFormatException;

public class ValueTooLargeException : Exception
{
    public ValueTooLargeException(string value, string maxValue)
        : base($"Given value {value} is larger than the maximum: {maxValue}.") { }
}
