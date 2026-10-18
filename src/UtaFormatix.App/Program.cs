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
    Description = "Simple import (skip pitch conversion; SVP field validation still applies)",
};

var recoverOption = new Option<bool>("--recover") { Description = "Recover damaged SVP data with explicit loss diagnostics" };
var numericStringsOption = new Option<bool>("--svp-numeric-strings") { Description = "Allow SVP numeric strings with a warning (also enabled by --recover)" };
var svpDefaults = new SvpImportOptions();
var maxFileOption = new Option<long>("--svp-max-file-bytes") { Description = "Maximum SVP file size in bytes", DefaultValueFactory = _ => svpDefaults.MaxFileBytes };
var maxDepthOption = new Option<int>("--svp-max-depth") { Description = "Maximum SVP JSON nesting depth (1-1024)", DefaultValueFactory = _ => svpDefaults.MaxDepth };
var maxTokensOption = new Option<int>("--svp-max-json-tokens") { Description = "Maximum SVP JSON token count", DefaultValueFactory = _ => svpDefaults.MaxJsonTokens };
var maxSamplesOption = new Option<int>("--svp-max-pitch-samples") { Description = "Maximum SVP pitch processing sample budget", DefaultValueFactory = _ => svpDefaults.MaxPitchSamples };

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

var splitProjectOption = new Option<int?>("--split-project")
{
    Description = "Max tracks per output file (split multi-track projects); omit to keep a single file",
};

var rootCommand = new RootCommand("UtaFormatix — Convert singing voice synthesizer project files")
{
    inputArg,
    outputOption,
    formatOption,
    simpleImportOption,
    recoverOption,
    numericStringsOption,
    maxFileOption,
    maxDepthOption,
    maxTokensOption,
    maxSamplesOption,
    defaultLyricOption,
    convertPitchOption,
    convertPhonemesOption,
    fillRestsOption,
    japaneseLyricsOption,
    listFormatsOption,
    splitProjectOption,
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
    var splitProject = parseResult.GetValue(splitProjectOption);
    var svpOptions = new SvpImportOptions(
        Recover: parseResult.GetValue(recoverOption),
        AllowNumericStrings: parseResult.GetValue(numericStringsOption),
        MaxFileBytes: parseResult.GetValue(maxFileOption),
        MaxDepth: parseResult.GetValue(maxDepthOption),
        MaxJsonTokens: parseResult.GetValue(maxTokensOption),
        MaxPitchSamples: parseResult.GetValue(maxSamplesOption));

    if (listFormats)
    {
        Console.WriteLine("Importable formats:");
        foreach (var f in Format.Importable)
            Console.WriteLine($"  {f.DisplayName,-20} .{f.Extension}");
        Console.WriteLine();
        Console.WriteLine("Exportable formats:");
        foreach (var f in Format.Exportable)
            Console.WriteLine($"  {f.DisplayName,-20} .{f.Extension}");
        return 0;
    }

    if (input is null || !input.Exists)
    {
        Console.Error.WriteLine($"Error: input file not found: {input?.FullName ?? "(null)"}");
        return 1;
    }

    var implementedImportFormats = new HashSet<string>([nameof(Format.UfData), nameof(Format.StandardMid), nameof(Format.VocaloidMid), nameof(Format.Vsq), nameof(Format.Vsqx), nameof(Format.Vpr), nameof(Format.Svp), nameof(Format.S5p), nameof(Format.Ust), nameof(Format.Ustx), nameof(Format.MusicXml), nameof(Format.Ccs), nameof(Format.Dv), nameof(Format.Ppsf), nameof(Format.Tssln)]);
    var inputExt = input.Extension.TrimStart('.').ToLowerInvariant();
    var inputFormat = Format.Importable.FirstOrDefault(f => f.MatchExtension(inputExt) && implementedImportFormats.Contains(f.Name))
        ?? Format.Importable.FirstOrDefault(f => f.MatchExtension(inputExt));
    if (inputFormat is null)
    {
        Console.Error.WriteLine($"Error: unsupported input format: .{inputExt}");
        return 1;
    }

    Console.WriteLine($"Input:  {input.Name} ({inputFormat.DisplayName})");

    if (inputFormat != Format.Svp && svpOptions != svpDefaults)
    {
        Console.Error.WriteLine("Error: recovery and --svp-* options apply only to SVP input.");
        return 1;
    }

    var importParams = new ImportParams(
        SimpleImport: simpleImport,
        DefaultLyric: defaultLyric) { Svp = svpOptions };

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
            nameof(Format.Ustx) => Ustx.Parse(input.FullName, importParams),
            nameof(Format.MusicXml) => MusicXml.Parse(input.FullName, importParams),
            nameof(Format.Ccs) => Ccs.Parse(input.FullName, importParams),
            nameof(Format.Dv) => Dv.Parse(input.FullName, importParams),
            nameof(Format.Ppsf) => Ppsf.Parse(input.FullName, importParams),
            nameof(Format.Tssln) => Tssln.Parse(input.FullName, importParams),
            _ => throw new NotSupportedException($"Format '{inputFormat.DisplayName}' is not yet implemented."),
        };
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error reading input: {ex.Message}");
        return 1;
    }

    foreach (var warning in project.ImportWarnings)
    {
        var message = warning switch
        {
            ImportWarning.InputDiagnostic diagnostic => $"{diagnostic.Code} at {diagnostic.Path}: {diagnostic.Message}",
            ImportWarning.TempoNotFound => "Tempo markers are missing or empty; used default tempo 120 BPM at tick 0.",
            ImportWarning.TimeSignatureNotFound => "Time signatures are missing or empty; used default time signature 4/4 at measure 0.",
            ImportWarning.DefaultTempoInserted tempo =>
                $"First tempo event occurs at source tick {tempo.FirstEventTick}; inserted default tempo 120 BPM at tick 0.",
            ImportWarning.DefaultTimeSignatureInserted signature =>
                $"First time signature event occurs at source tick {signature.FirstEventTick}; inserted default time signature 4/4 at tick 0.",
            _ => null,
        };
        if (message is not null) Console.Error.WriteLine($"Warning: {message}");
    }

    Console.WriteLine($"  Tracks: {project.Tracks.Count}, Notes: {project.Tracks.Sum(t => t.Notes.Count)}, Tempos: {project.Tempos.Count}");

    var outputFormat = ResolveOutputFormat(formatName, output, input, inputFormat);
    if (outputFormat is null)
    {
        Console.Error.WriteLine("Error: could not determine output format. Use -f or -o with a recognized extension.");
        return 1;
    }

    var features = new List<FeatureConfig>();
    if (convertPitch && outputFormat.AvailableFeaturesForGeneration.Contains(Feature.ConvertPitch))
        features.Add(new FeatureConfig.ConvertPitchConfig());

    if (splitProject is { } maxTracks)
    {
        if (maxTracks <= 0)
        {
            Console.Error.WriteLine("Error: --split-project must be a positive integer.");
            return 1;
        }
        if (!outputFormat.AvailableFeaturesForGeneration.Contains(Feature.SplitProject))
        {
            Console.Error.WriteLine($"Error: {outputFormat.DisplayName} does not support splitting the project.");
            return 1;
        }
        features.Add(new FeatureConfig.SplitProjectConfig(maxTracks));
    }

    var outputPath = output?.FullName
        ?? Path.Combine(input.DirectoryName ?? ".", outputFormat.GetFileName(project.Name));

    Console.WriteLine($"Output: {Path.GetFileName(outputPath)} ({outputFormat.DisplayName})");

    try
    {
        AtomicFile.Write(outputPath, temporaryPath =>
        {
            switch (outputFormat.Name)
            {
                case nameof(Format.UfData):
                    UfData.GenerateFile(project, temporaryPath, features);
                    break;
                case nameof(Format.StandardMid):
                    StandardMid.GenerateFile(project, temporaryPath, features);
                    break;
                case nameof(Format.VocaloidMid):
                    VocaloidMid.GenerateFile(project, temporaryPath, features);
                    break;
                case nameof(Format.Vsq):
                    Vsq.GenerateFile(project, temporaryPath, features);
                    break;
                case nameof(Format.Vsqx):
                    Vsqx.GenerateFile(project, temporaryPath, features);
                    break;
                case nameof(Format.Vpr):
                    Vpr.GenerateFile(project, temporaryPath, features);
                    break;
                case nameof(Format.Svp):
                    Svp.GenerateFile(project, temporaryPath, features);
                    break;
                case nameof(Format.S5p):
                    S5p.GenerateFile(project, temporaryPath, features);
                    break;
                case nameof(Format.Ust):
                    Ust.GenerateFile(project, temporaryPath, features);
                    break;
                case nameof(Format.Ustx):
                    Ustx.GenerateFile(project, temporaryPath, features);
                    break;
                case nameof(Format.MusicXml):
                    MusicXml.GenerateFile(project, temporaryPath, features);
                    break;
                case nameof(Format.Ccs):
                    Ccs.GenerateFile(project, temporaryPath, features);
                    break;
                case nameof(Format.Dv):
                    Dv.GenerateFile(project, temporaryPath, features);
                    break;
                case nameof(Format.Tssln):
                    Tssln.GenerateFile(project, temporaryPath, features);
                    break;
                default:
                    throw new NotSupportedException($"Output format '{outputFormat.DisplayName}' is not yet implemented.");
            }
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error writing output: {ex.Message}");
        return 1;
    }

    Console.WriteLine("Done.");
    return 0;
});

var parseResult = rootCommand.Parse(args);
return await parseResult.InvokeAsync();

static Format? ResolveOutputFormat(string? formatName, FileInfo? output, FileInfo input, Format inputFormat)
{
    var implementedExportFormats = new HashSet<string>([nameof(Format.UfData), nameof(Format.StandardMid), nameof(Format.VocaloidMid), nameof(Format.Vsq), nameof(Format.Vsqx), nameof(Format.Vpr), nameof(Format.Svp), nameof(Format.S5p), nameof(Format.Ust), nameof(Format.Ustx), nameof(Format.MusicXml), nameof(Format.Ccs), nameof(Format.Dv), nameof(Format.Tssln)]);

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
