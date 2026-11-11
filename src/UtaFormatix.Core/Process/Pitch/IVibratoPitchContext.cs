using UtaFormatix.Core.IO;

namespace UtaFormatix.Core.Process.Pitch;

internal interface IVibratoPitchContext
{
    bool ValidateOnly { get; }
    ProjectInputException Range(string field);
    double Finite(double value, string field);
    long Add(long left, long right, string field);
    long Tick(TickTimeTransformer time, double milliseconds, string field);
    void Samples(double count, string field);
    void Preview(double count, string field);
}
