using UtaFormatix.Core.IO;

namespace UtaFormatix.Core.Process.Pitch;

internal sealed class PitchSampleBudget(int maximum, string code, string optionPath)
{
    private long used;

    public bool Fits(double count) =>
        maximum > 0 && double.IsFinite(count) && count >= 0 && count <= maximum - used;

    public bool TrySpend(double count)
    {
        if (!Fits(count)) return false;
        used += (long)count;
        return true;
    }

    public void Check(double count, string path)
    {
        if (maximum <= 0)
            throw new ProjectInputException(code, optionPath, "Pitch sample budget must be positive.");
        if (!double.IsFinite(count) || count < 0 || count > maximum - used)
            throw new ProjectInputException(code, path,
                $"Pitch processing exceeds the {maximum} sample budget; reduce pitch data or raise the explicit limit.");
    }

    public void Spend(double count, string path)
    {
        Check(count, path);
        used += (long)count;
    }
}
