namespace TimeGrapher.Core.Shared;

public static class AudioSampleRates
{
    private static readonly int[] StandardRateValues = { 48000, 96000, 192000, 384000 };

    private static readonly HashSet<int> StandardRateSetValues = new(StandardRateValues);

    public static IReadOnlyList<int> Standard { get; } = Array.AsReadOnly(StandardRateValues);

    public static IReadOnlySet<int> StandardSet => StandardRateSetValues;
}
