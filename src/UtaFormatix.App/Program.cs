using UtaFormatix.Core.IO;
using UtaFormatix.Core.Models;

var sampleProject = new Project(
    Format: Format.UfData,
    Name: "Test Project",
    Tracks:
    [
        new Track(
            Id: 0,
            Name: "Track 1",
            Notes:
            [
                new Note(Id: 0, Key: 60, Lyric: "あ", TickOn: 0, TickOff: 480, Phoneme: "a"),
                new Note(Id: 1, Key: 62, Lyric: "い", TickOn: 480, TickOff: 960, Phoneme: "i"),
                new Note(Id: 2, Key: 64, Lyric: "う", TickOn: 960, TickOff: 1440),
            ],
            Pitch: new Pitch(
                Data: [(0L, 60.0), (240L, 60.5), (480L, null)],
                IsAbsolute: true)),
        new Track(
            Id: 1,
            Name: "Track 2",
            Notes:
            [
                new Note(Id: 0, Key: 48, Lyric: "ら", TickOn: 0, TickOff: 960),
            ]),
    ],
    TimeSignatures:
    [
        new TimeSignature(MeasurePosition: 0, Numerator: 4, Denominator: 4),
    ],
    Tempos:
    [
        new Tempo(TickPosition: 0, Bpm: 120),
        new Tempo(TickPosition: 3840, Bpm: 140),
    ],
    MeasurePrefix: 0,
    ImportWarnings: []);

Console.WriteLine("=== UtaFormatix3 .NET — UFDATA Round-trip Test ===");
Console.WriteLine();

Console.WriteLine($"Source project: \"{sampleProject.Name}\"");
Console.WriteLine($"  Tracks: {sampleProject.Tracks.Count}");
Console.WriteLine($"  Tempos: {sampleProject.Tempos.Count}");
Console.WriteLine($"  Time signatures: {sampleProject.TimeSignatures.Count}");
Console.WriteLine($"  Total notes: {sampleProject.Tracks.Sum(t => t.Notes.Count)}");
Console.WriteLine();

var (data, fileName, notifications) = UfData.Generate(sampleProject,
    [new FeatureConfig.ConvertPitchConfig()]);

Console.WriteLine($"Exported: {fileName} ({data.Length} bytes)");
if (notifications.Count > 0)
    Console.WriteLine($"  Notifications: {string.Join(", ", notifications.Select(n => n.GetType().Name))}");
Console.WriteLine();

var tempPath = Path.Combine(Path.GetTempPath(), fileName);
File.WriteAllBytes(tempPath, data);
Console.WriteLine($"Written to: {tempPath}");

var loaded = UfData.ParseFile(tempPath);

Console.WriteLine();
Console.WriteLine($"Loaded project: \"{loaded.Name}\"");
Console.WriteLine($"  Format: {loaded.Format.DisplayName}");
Console.WriteLine($"  Tracks: {loaded.Tracks.Count}");
Console.WriteLine($"  Tempos: {loaded.Tempos.Count}");
Console.WriteLine($"  Time signatures: {loaded.TimeSignatures.Count}");
Console.WriteLine($"  Total notes: {loaded.Tracks.Sum(t => t.Notes.Count)}");
Console.WriteLine();

var ok = true;

if (loaded.Name != sampleProject.Name) { Console.WriteLine("FAIL: name mismatch"); ok = false; }
if (loaded.Tracks.Count != sampleProject.Tracks.Count) { Console.WriteLine("FAIL: track count mismatch"); ok = false; }
if (loaded.Tempos.Count != sampleProject.Tempos.Count) { Console.WriteLine("FAIL: tempo count mismatch"); ok = false; }
if (loaded.TimeSignatures.Count != sampleProject.TimeSignatures.Count) { Console.WriteLine("FAIL: time sig count mismatch"); ok = false; }

for (var i = 0; i < sampleProject.Tracks.Count; i++)
{
    var src = sampleProject.Tracks[i];
    var dst = loaded.Tracks[i];
    if (src.Name != dst.Name) { Console.WriteLine($"FAIL: track {i} name mismatch"); ok = false; }
    if (src.Notes.Count != dst.Notes.Count) { Console.WriteLine($"FAIL: track {i} note count mismatch"); ok = false; continue; }
    for (var j = 0; j < src.Notes.Count; j++)
    {
        var sn = src.Notes[j];
        var dn = dst.Notes[j];
        if (sn.Key != dn.Key || sn.TickOn != dn.TickOn || sn.TickOff != dn.TickOff
            || sn.Lyric != dn.Lyric || sn.Phoneme != dn.Phoneme)
        {
            Console.WriteLine($"FAIL: track {i} note {j} mismatch");
            ok = false;
        }
    }
}

for (var i = 0; i < sampleProject.Tempos.Count; i++)
{
    if (sampleProject.Tempos[i].TickPosition != loaded.Tempos[i].TickPosition
        || Math.Abs(sampleProject.Tempos[i].Bpm - loaded.Tempos[i].Bpm) > 0.001)
    {
        Console.WriteLine($"FAIL: tempo {i} mismatch");
        ok = false;
    }
}

if (sampleProject.Tracks[0].Pitch is { } srcPitch)
{
    var lp = loaded.Tracks[0].Pitch;
    if (lp is null)
    {
        Console.WriteLine("FAIL: pitch data lost");
        ok = false;
    }
    else
    {
        if (srcPitch.IsAbsolute != lp.IsAbsolute) { Console.WriteLine("FAIL: pitch isAbsolute mismatch"); ok = false; }
        if (srcPitch.Data.Count != lp.Data.Count) { Console.WriteLine("FAIL: pitch data count mismatch"); ok = false; }
    }
}

File.Delete(tempPath);

Console.WriteLine();
if (ok)
    Console.WriteLine("PASS — round-trip verified.");
else
    Console.WriteLine("FAIL — see errors above.");

return ok ? 0 : 1;
