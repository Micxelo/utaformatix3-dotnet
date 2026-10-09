namespace UtaFormatix.Core.IO;

internal static class MidiUtil
{
    public const int StandardTimeDivision = 480;

    public static double ConvertMidiTempoToBpm(long microsecondsPerBeat) =>
        Math.Round(60_000_000.0 / microsecondsPerBeat, 2);

    public static long ConvertBpmToMidiTempo(double bpm) =>
        (long)(60_000_000.0 / bpm);

    public static byte[] GenerateTimeSignatureBytes(int numerator, int denominator)
    {
        var denominatorLog2 = (byte)Math.Log2(denominator);
        return [0xFF, 0x58, 0x04, (byte)numerator, denominatorLog2, 0x18, 0x08];
    }

    public static long ConvertTickTime(long tick, int sourceTimeDivision) =>
        sourceTimeDivision == StandardTimeDivision
            ? tick
            : tick * StandardTimeDivision / sourceTimeDivision;
}
