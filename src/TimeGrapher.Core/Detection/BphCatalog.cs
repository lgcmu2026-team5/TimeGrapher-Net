namespace TimeGrapher.Core.Detection;

public static class BphCatalog
{
    private static readonly int[] AutoBphValues =
    {
        12000, 14400, 18000, 19800, 21600, 25200, 28800, 36000, 43200
    };

    private static readonly int[] ManualBphValues =
    {
         3600,  6000,  7200,  7380,  7440,  7800,  9000,  9100, 10800, 11880,
        12000, 12342, 12480, 12600, 13320, 13440, 13500, 14000, 14040, 14160,
        14200, 14280, 14400, 14520, 14580, 14760, 14850, 15000, 15360, 15600,
        16200, 16320, 16800, 17196, 17258, 17280, 17786, 17897, 18000, 18049,
        18514, 19332, 19440, 19800, 20160, 20222, 20944, 21000, 21031, 21306,
        21600, 25200, 28800, 32400, 36000, 43200
    };

    private static readonly int[] ManualAutoBphValues = CreateManualAutoBphValues();

    public static IReadOnlyList<int> ManualBph { get; } = Array.AsReadOnly(ManualBphValues);

    public static IReadOnlyList<int> ManualAutoBph { get; } = Array.AsReadOnly(ManualAutoBphValues);

    internal static int[] AutoBphArray => AutoBphValues;

    internal static int[] ManualBphArray => ManualBphValues;

    private static int[] CreateManualAutoBphValues()
    {
        var values = new int[ManualBphValues.Length + 1];
        values[0] = 0;
        Array.Copy(ManualBphValues, 0, values, 1, ManualBphValues.Length);
        return values;
    }
}
