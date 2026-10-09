using System.Text.Json.Serialization;

namespace UtaFormatix.Core.IO;

[JsonSerializable(typeof(UtaFormatix.Data.UfData))]
[JsonSerializable(typeof(UtaFormatix.Data.Project))]
[JsonSerializable(typeof(UtaFormatix.Data.Track))]
[JsonSerializable(typeof(UtaFormatix.Data.Note))]
[JsonSerializable(typeof(UtaFormatix.Data.Pitch))]
[JsonSerializable(typeof(UtaFormatix.Data.Tempo))]
[JsonSerializable(typeof(UtaFormatix.Data.TimeSignature))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
public partial class UfDataJsonContext : JsonSerializerContext;
