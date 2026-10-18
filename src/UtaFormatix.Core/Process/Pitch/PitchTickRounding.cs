namespace UtaFormatix.Core.Process.Pitch;

internal static class PitchTickRounding
{
    public static long Round(double tick)
    {
        // Match Kotlin roundToLong: nearest integer, with ties toward positive infinity.
        // Compare the fraction directly to preserve values adjacent to a half tick.
        var integral = Math.Truncate(tick);
        var fraction = tick - integral;
        if (fraction >= 0.5)
            integral += 1;
        else if (fraction < -0.5)
            integral -= 1;
        return (long)integral;
    }
}
