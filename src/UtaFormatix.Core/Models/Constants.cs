namespace UtaFormatix.Core.Models;

public static class Constants
{
    public const int TicksInBeat = 480;
    public const int TicksInFullNote = TicksInBeat * 4;
    public const int KeyInOctave = 12;
    public const string DefaultLyric = "あ";
    public const double DefaultBpm = 120.0;
    public const int DefaultMeterHigh = 4;
    public const int DefaultMeterLow = 4;
    public const int DefaultKey = 60;
    public const double KeyCenterC = 60.0;
    public const double LogFrqCenterC = 5.566914341;
    public const double LogFrqDiffOneKey = 0.05776226505;
}
