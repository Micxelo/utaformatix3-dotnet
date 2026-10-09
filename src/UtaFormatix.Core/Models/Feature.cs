namespace UtaFormatix.Core.Models;

public enum Feature
{
    ConvertPitch,
    SplitProject,
    ConvertPhonemes,
}

public abstract record FeatureConfig(Feature Type)
{
    public sealed record ConvertPitchConfig() : FeatureConfig(Feature.ConvertPitch);

    public sealed record SplitProjectConfig(int MaxTrackCount) : FeatureConfig(Feature.SplitProject)
    {
        public static SplitProjectConfig GetDefault(Format format) =>
            format == Format.Svp ? new SplitProjectConfig(3) : new SplitProjectConfig(1);
    }
}

public static class FeatureConfigExtensions
{
    public static bool Contains(this IEnumerable<FeatureConfig> configs, Feature feature) =>
        configs.Any(c => c.Type == feature);
}
