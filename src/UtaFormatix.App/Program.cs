using System.CommandLine;
using UtaFormatix.Core.IO;
using UtaFormatix.Core.Models;

var inputArg = new Argument<FileInfo?>("input")
{
    Description = "Input project file",
    Arity = ArgumentArity.ZeroOrOne,
};

var outputOption = new Option<FileInfo?>("--output", ["-o"])
{
    Description = "Output file path (default: same directory, new extension)",
};

var formatOption = new Option<string?>("--output-format", ["-f"])
{
    Description = "Output format name or extension",
};

var simpleImportOption = new Option<bool>("--simple-import")
{
    Description = "Simple import (skip pitch/phonemes)",
};

var defaultLyricOption = new Option<string>("--default-lyric")
{
    Description = "Default lyric for empty notes",
    DefaultValueFactory = _ => Constants.DefaultLyric,
};

var convertPitchOption = new Option<bool>("--convert-pitch")
{
    Description = "Enable pitch data conversion",
};

var convertPhonemesOption = new Option<bool>("--convert-phonemes")
{
    Description = "Enable phoneme conversion",
};

var fillRestsOption = new Option<int?>("--fill-rests")
{
    Description = "Fill slight rests between notes (denominator: 8/16/32/64/128)",
};

var japaneseLyricsOption = new Option<string[]>("--japanese-lyrics")
{
    Description = "Convert Japanese lyrics: <from> <to> (e.g., RomajiCv KanaCv)",
    Arity = ArgumentArity.ExactlyOne,
};

var listFormatsOption = new Option<bool>("--list-formats")
{
    Description = "List all supported formats and exit",
};

var rootCommand = new RootCommand("UtaFormatix — Convert singing voice synthesizer project files")
{
    inputArg,
    outputOption,
    formatOption,
    simpleImportOption,
    defaultLyricOption,
    convertPitchOption,
    convertPhonemesOption,
    fillRestsOption,
    japaneseLyricsOption,
    listFormatsOption,
};

rootCommand.SetAction(async (parseResult) =>
{
    var input = parseResult.GetValue(inputArg);
    var output = parseResult.GetValue(outputOption);
    var formatName = parseResult.GetValue(formatOption);
    var simpleImport = parseResult.GetValue(simpleImportOption);
    var defaultLyric = parseResult.GetValue(defaultLyricOption)!;
    var convertPitch = parseResult.GetValue(convertPitchOption);
    var convertPhonemes = parseResult.GetValue(convertPhonemesOption);
    var fillRests = parseResult.GetValue(fillRestsOption);
    var japaneseLyrics = parseResult.GetValue(japaneseLyricsOption);
    var listFormats = parseResult.GetValue(listFormatsOption);

    if (listFormats)
    {
        Console.WriteLine("Importable formats:");
        foreach (var f in Format.Importable)
            Console.WriteLine($"  {f.DisplayName,-20} .{f.Extension}");
        Console.WriteLine();
        Console.WriteLine("Exportable formats:");
        foreach (var f in Format.Exportable)
            Console.WriteLine($"  {f.DisplayName,-20} .{f.Extension}");
        return;
    }

    if (input is null || !input.Exists)
    {
        Console.Error.WriteLine($"Error: input file not found: {input?.FullName ?? "(null)"}");
        Environment.ExitCode = 1;
        return;
    }

    var implementedImportFormats = new HashSet<string>([nameof(Format.UfData), nameof(Format.StandardMid), nameof(Format.VocaloidMid), nameof(Format.Vsq), nameof(Format.Vsqx), nameof(Format.Vpr), nameof(Format.Svp), nameof(Format.S5p), nameof(Format.Ust)]);
    var inputExt = input.Extension.TrimStart('.').ToLowerInvariant();
    var inputFormat = Format.Importable.FirstOrDefault(f => f.MatchExtension(inputExt) && implementedImportFormats.Contains(f.Name))
        ?? Format.Importable.FirstOrDefault(f => f.MatchExtension(inputExt));
    if (inputFormat is null)
    {
        Console.Error.WriteLine($"Error: unsupported input format: .{inputExt}");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine($"Input:  {input.Name} ({inputFormat.DisplayName})");

    var importParams = new ImportParams(
        SimpleImport: simpleImport,
        DefaultLyric: defaultLyric);

    Project project;
    try
    {
        project = inputFormat.Name switch
        {
            nameof(Format.UfData) => UfData.ParseFile(input.FullName, importParams),
            nameof(Format.StandardMid) => StandardMid.Parse(input.FullName, importParams),
            nameof(Format.VocaloidMid) => VocaloidMid.Parse(input.FullName, importParams),
            nameof(Format.Vsq) => Vsq.Parse(input.FullName, importParams),
            nameof(Format.Vsqx) => Vsqx.Parse(input.FullName, importParams),
            nameof(Format.Vpr) => Vpr.Parse(input.FullName, importParams),
            nameof(Format.Svp) => Svp.Parse(input.FullName, importParams),
            nameof(Format.S5p) => S5p.Parse(input.FullName, importParams),
            nameof(Format.Ust) => Ust.Parse(input.FullName, importParams),
            _ => throw new NotSupportedException($"Format '{inputFormat.DisplayName}' is not yet implemented."),
        };
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error reading input: {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine($"  Tracks: {project.Tracks.Count}, Notes: {project.Tracks.Sum(t => t.Notes.Count)}, Tempos: {project.Tempos.Count}");

    var outputFormat = ResolveOutputFormat(formatName, output, input, inputFormat);
    if (outputFormat is null)
    {
        Console.Error.WriteLine("Error: could not determine output format. Use -f or -o with a recognized extension.");
        Environment.ExitCode = 1;
        return;
    }

    var features = new List<FeatureConfig>();
    if (convertPitch && outputFormat.AvailableFeaturesForGeneration.Contains(Feature.ConvertPitch))
        features.Add(new FeatureConfig.ConvertPitchConfig());

    if (outputFormat.AvailableFeaturesForGeneration.Contains(Feature.SplitProject))
        features.Add(FeatureConfig.SplitProjectConfig.GetDefault(outputFormat));

    var outputPath = output?.FullName
        ?? Path.Combine(input.DirectoryName ?? ".", outputFormat.GetFileName(project.Name));

    Console.WriteLine($"Output: {Path.GetFileName(outputPath)} ({outputFormat.DisplayName})");

    try
    {
        switch (outputFormat.Name)
        {
            case nameof(Format.UfData):
                UfData.GenerateFile(project, outputPath, features);
                break;
            case nameof(Format.StandardMid):
                StandardMid.GenerateFile(project, outputPath, features);
                break;
            case nameof(Format.VocaloidMid):
                VocaloidMid.GenerateFile(project, outputPath, features);
                break;
            case nameof(Format.Vsq):
                Vsq.GenerateFile(project, outputPath, features);
                break;
            case nameof(Format.Vsqx):
                Vsqx.GenerateFile(project, outputPath, features);
                break;
            case nameof(Format.Vpr):
                Vpr.GenerateFile(project, outputPath, features);
                break;
            case nameof(Format.Svp):
                Svp.GenerateFile(project, outputPath, features);
                break;
            case nameof(Format.S5p):
                S5p.GenerateFile(project, outputPath, features);
                break;
            case nameof(Format.Ust):
                Ust.GenerateFile(project, outputPath, features);
                break;
            default:
                throw new NotSupportedException($"Output format '{outputFormat.DisplayName}' is not yet implemented.");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error writing output: {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine("Done.");
});

var parseResult = rootCommand.Parse(args);
return await parseResult.InvokeAsync();

static Format? ResolveOutputFormat(string? formatName, FileInfo? output, FileInfo input, Format inputFormat)
{
    var implementedExportFormats = new HashSet<string>([nameof(Format.UfData), nameof(Format.StandardMid), nameof(Format.VocaloidMid), nameof(Format.Vsq), nameof(Format.Vsqx), nameof(Format.Vpr), nameof(Format.Svp), nameof(Format.S5p), nameof(Format.Ust)]);

    if (!string.IsNullOrEmpty(formatName))
    {
        return Format.Exportable.FirstOrDefault(f =>
            string.Equals(f.Name, formatName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.DisplayName, formatName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.Extension, formatName, StringComparison.OrdinalIgnoreCase));
    }

    if (output is not null)
    {
        var ext = output.Extension.TrimStart('.').ToLowerInvariant();
        return Format.Exportable.FirstOrDefault(f => f.MatchExtension(ext) && implementedExportFormats.Contains(f.Name))
            ?? Format.Exportable.FirstOrDefault(f => f.MatchExtension(ext));
    }

    return null;
}
