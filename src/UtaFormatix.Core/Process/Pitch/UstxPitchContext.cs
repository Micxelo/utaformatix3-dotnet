using UtaFormatix.Core.IO;
using UtaFormatix.Core.Models;

namespace UtaFormatix.Core.Process.Pitch;

internal sealed class UstxPitchContext
    (PitchSampleBudget budget, string path, string? filePath) : IVibratoPitchContext
{
    private const string OptionPath = "$.ustx.maxPitchSamples";

    public static UstxPitchContext Create(UstxPitchOptions? options, string? filePath = null)
    {
        var budget = new PitchSampleBudget((options ?? new UstxPitchOptions()).MaxPitchSamples, "USTX_LIMIT", OptionPath);
        budget.Check(0, OptionPath);
        return new UstxPitchContext(budget, "$", filePath);
    }

    public UstxPitchContext At(string field) => new(budget, path + field, filePath);

    private string Location(string field)
    {
        // The shared vibrato calculation reports UST VBR field names.
        var location = path + (field switch
        {
            "VBR" => ".vibrato",
            "VBR[0]" => ".vibrato.length",
            "VBR[1]" => ".vibrato.period",
            "VBR[3]" => ".vibrato.in",
            "VBR[4]" => ".vibrato.out",
            "VBR[6]" => ".vibrato.drift",
            _ => field,
        });
        return filePath is null ? location : $"{filePath} ({location})";
    }

    bool IVibratoPitchContext.ValidateOnly => false;

    public ProjectInputException Range(string field) => new("USTX_RANGE", Location(field),
        "Pitch calculation is outside the supported numeric, tick, or output-field range.");

    public double Finite(double value, string field)
    {
        if (!double.IsFinite(value)) throw Range(field);
        return value;
    }

    public long Add(long left, long right, string field)
    {
        try { return checked(left + right); }
        catch (OverflowException) { throw Range(field); }
    }

    public long Distance(long from, long to, string field)
    {
        try { return checked(to - from); }
        catch (OverflowException) { throw Range(field); }
    }

    public long Tick(TickTimeTransformer time, double milliseconds, string field)
    {
        Finite(milliseconds, field);
        try { return time.SecToTickChecked(milliseconds / 1000.0); }
        catch (OverflowException) { throw Range(field); }
    }

    public void Samples(double count, string field)
    {
        if (!budget.TrySpend(count)) budget.Check(count, Location(field));
    }

    public void Preview(double count, string field)
    {
        if (!budget.Fits(count)) budget.Check(count, Location(field));
    }
}
