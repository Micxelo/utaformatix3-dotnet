using UtaFormatix.Core.Models;
using PitchModel = UtaFormatix.Core.Models.Pitch;

namespace UtaFormatix.Core.Process.Pitch;

public sealed record VocaloidPartPitchData(
    long StartPos,
    List<VocaloidPartPitchData.Event> Pit,
    List<VocaloidPartPitchData.Event> Pbs)
{
    public sealed record Event(long Pos, int Value);
}

internal static class VocaloidPitchConversion
{
    private const int PitchMaxValue = 8191;
    private const int DefaultPitchBendSensitivity = 2;
    private const long MinBreakLengthBetweenPitchSections = 480L;
    private const long BorderAppendRadius = 5L;

    public static PitchModel? PitchFromVocaloidParts(List<VocaloidPartPitchData> dataByParts)
    {
        var pitchRawDataByPart = dataByParts.Select(part =>
        {
            var pit = part.Pit;
            var pbs = part.Pbs;
            var pitMultipliedByPbs = new Dictionary<long, int>();
            var pitIndex = 0;
            var pbsCurrentValue = DefaultPitchBendSensitivity;

            foreach (var pbsEvent in pbs)
            {
                for (var i = pitIndex; i <= pit.Count - 1; i++)
                {
                    var pitEvent = pit[i];
                    if (pitEvent.Pos < pbsEvent.Pos)
                    {
                        pitMultipliedByPbs[pitEvent.Pos] = pitEvent.Value * pbsCurrentValue;
                        if (i == pit.Count - 1) pitIndex = i;
                    }
                    else
                    {
                        pitIndex = i;
                        break;
                    }
                }
                pbsCurrentValue = pbsEvent.Value;
            }

            if (pitIndex < pit.Count - 1)
            {
                for (var i = pitIndex; i <= pit.Count - 1; i++)
                {
                    var pitEvent = pit[i];
                    pitMultipliedByPbs[pitEvent.Pos] = pitEvent.Value * pbsCurrentValue;
                }
            }

            return pitMultipliedByPbs.Select(kv => (Pos: kv.Key + part.StartPos, Value: kv.Value)).ToList();
        }).ToList();

        var pitchRawData = pitchRawDataByPart
            .Aggregate(new List<(long Pos, int Value)>(), (accumulator, element) =>
            {
                if (element.Count == 0)
                    return accumulator;

                var firstPos = element[0].Pos;
                var firstInvalidIndexInPrevious = accumulator.FindIndex(p => p.Pos >= firstPos);
                if (firstInvalidIndexInPrevious < 0)
                    return accumulator.Concat(element).ToList();
                return accumulator.Take(firstInvalidIndexInPrevious).Concat(element).ToList();
            });

        var data = pitchRawData
            .Select(p => (Tick: p.Pos, Value: (double?)(p.Value / (double)PitchMaxValue)))
            .ToList();

        return data.Count > 0 ? new PitchModel(data, IsAbsolute: false) : null;
    }

    public static VocaloidPartPitchData? GenerateForVocaloid(this PitchModel pitch, List<Note> notes)
    {
        var data = PitchCalculation.GetRelativeData(pitch, notes, BorderAppendRadius);
        if (data is null) return null;

        var pitchSectioned = new List<List<(long Tick, double Value)>>();
        var currentPos = 0L;

        foreach (var pitchEvent in data)
        {
            if (pitchSectioned.Count == 0)
            {
                pitchSectioned.Add([pitchEvent]);
            }
            else if (pitchEvent.Tick - currentPos >= MinBreakLengthBetweenPitchSections)
            {
                pitchSectioned.Add([pitchEvent]);
            }
            else
            {
                pitchSectioned[^1].Add(pitchEvent);
            }
            currentPos = pitchEvent.Tick;
        }

        var pit = new List<VocaloidPartPitchData.Event>();
        var pbs = new List<VocaloidPartPitchData.Event>();

        foreach (var section in pitchSectioned)
        {
            var maxAbsValue = section.Max(p => Math.Abs(p.Value));
            var pbsForThisSection = (int)Math.Ceiling(maxAbsValue);

            if (pbsForThisSection > DefaultPitchBendSensitivity)
            {
                pbs.Add(new VocaloidPartPitchData.Event(section[0].Tick, pbsForThisSection));
                pbs.Add(new VocaloidPartPitchData.Event(
                    section[^1].Tick + MinBreakLengthBetweenPitchSections / 2,
                    DefaultPitchBendSensitivity));
            }
            else
            {
                pbsForThisSection = DefaultPitchBendSensitivity;
            }

            foreach (var (pitchPos, pitchValue) in section)
            {
                var clamped = (int)Math.Round(pitchValue * PitchMaxValue / pbsForThisSection);
                clamped = clamped < -PitchMaxValue ? -PitchMaxValue : clamped > PitchMaxValue ? PitchMaxValue : clamped;
                pit.Add(new VocaloidPartPitchData.Event(pitchPos, clamped));
            }
        }

        return new VocaloidPartPitchData(0, pit, pbs);
    }
}
