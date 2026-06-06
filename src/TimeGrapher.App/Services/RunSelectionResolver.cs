using TimeGrapher.App.ViewModels;

namespace TimeGrapher.App.Services;

internal readonly record struct AnalysisSelection(
    int AveragingPeriod,
    bool AutoBph,
    int ManualBph);

internal readonly record struct SimulationSelection(
    int Bph,
    int SampleRate);

internal sealed class RunSelectionResolver
{
    public const int DefaultAveragingPeriodSeconds = 20;
    public const int DefaultSimulationBph = 28800;

    private readonly MainWindowViewModel _viewModel;
    private readonly IReadOnlyList<int> _averagingPeriods;
    private readonly IReadOnlyList<int> _manualAutoBph;
    private readonly IReadOnlyList<int> _simulationBph;

    public RunSelectionResolver(
        MainWindowViewModel viewModel,
        IReadOnlyList<int> averagingPeriods,
        IReadOnlyList<int> manualAutoBph,
        IReadOnlyList<int> simulationBph)
    {
        _viewModel = viewModel;
        _averagingPeriods = averagingPeriods;
        _manualAutoBph = manualAutoBph;
        _simulationBph = simulationBph;
    }

    public int DefaultAveragingPeriodIndex => FindValue(_averagingPeriods, DefaultAveragingPeriodSeconds);

    public int DefaultSimulationBphIndex => FindValue(_simulationBph, DefaultSimulationBph);

    public AnalysisSelection GetAnalysisSelection()
    {
        int averagingPeriod = RequireSelectedValue(
            _averagingPeriods,
            _viewModel.SelectedAveragingPeriodIndex,
            "averaging period");
        int selectedBphIndex = _viewModel.SelectedBphIndex;
        if (selectedBphIndex == 0)
        {
            return new AnalysisSelection(
                averagingPeriod,
                AutoBph: true,
                ManualBph: 0);
        }

        return new AnalysisSelection(
            averagingPeriod,
            AutoBph: false,
            ManualBph: RequireSelectedValue(_manualAutoBph, selectedBphIndex, "manual BPH"));
    }

    public SimulationSelection GetSimulationSelection(IReadOnlyList<int> availableSampleRates, int availableSampleRateCount)
    {
        return new SimulationSelection(
            RequireSelectedValue(_simulationBph, _viewModel.SelectedSimBphIndex, "simulation BPH"),
            RequireSelectedValue(availableSampleRates, availableSampleRateCount, _viewModel.SelectedSampleRateIndex, "sample rate"));
    }

    public static int FindValue(IReadOnlyList<int> items, int value)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] == value)
            {
                return i;
            }
        }

        return -1;
    }

    private static int RequireSelectedValue(IReadOnlyList<int> items, int selectedIndex, string selectionName)
    {
        return RequireSelectedValue(items, items.Count, selectedIndex, selectionName);
    }

    private static int RequireSelectedValue(
        IReadOnlyList<int> items,
        int itemCount,
        int selectedIndex,
        string selectionName)
    {
        int boundedCount = Math.Min(itemCount, items.Count);
        if (selectedIndex < 0 || selectedIndex >= boundedCount)
        {
            throw new InvalidOperationException("No valid " + selectionName + " is selected.");
        }

        return items[selectedIndex];
    }
}
