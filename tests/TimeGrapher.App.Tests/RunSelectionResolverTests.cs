using TimeGrapher.App.Services;
using TimeGrapher.App.ViewModels;
using TimeGrapher.Core.Detection;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class RunSelectionResolverTests
{
    private static readonly int[] AveragingPeriods = { 2, 4, 8, 10, 12, 20, 20, 30 };

    [Fact]
    public void DefaultIndicesResolveByMeaningfulValues()
    {
        MainWindowViewModel vm = CreateViewModel();
        RunSelectionResolver resolver = CreateResolver(vm);

        Assert.Equal(RunSelectionResolver.DefaultAveragingPeriodSeconds, AveragingPeriods[resolver.DefaultAveragingPeriodIndex]);
        Assert.Equal(RunSelectionResolver.DefaultSimulationBph, BphCatalog.ManualBph[resolver.DefaultSimulationBphIndex]);
    }

    [Fact]
    public void AnalysisSelectionValidatesSelectedIndices()
    {
        MainWindowViewModel vm = CreateViewModel();
        RunSelectionResolver resolver = CreateResolver(vm);

        vm.SelectedAveragingPeriodIndex = 5;
        vm.SelectedBphIndex = 0;

        AnalysisSelection auto = resolver.GetAnalysisSelection();

        Assert.True(auto.AutoBph);
        Assert.Equal(20, auto.AveragingPeriod);
        Assert.Equal(0, auto.ManualBph);

        vm.SelectedBphIndex = 6;

        AnalysisSelection manual = resolver.GetAnalysisSelection();

        Assert.False(manual.AutoBph);
        Assert.Equal(BphCatalog.ManualAutoBph[6], manual.ManualBph);

        vm.SelectedAveragingPeriodIndex = -1;

        Assert.Throws<InvalidOperationException>(() => resolver.GetAnalysisSelection());
    }

    [Fact]
    public void SimulationSelectionUsesOnlyAdvertisedSampleRates()
    {
        MainWindowViewModel vm = CreateViewModel();
        RunSelectionResolver resolver = CreateResolver(vm);
        int[] availableSampleRates = { 48000, 96000, 192000, 0, 0 };

        vm.SelectedSimBphIndex = resolver.DefaultSimulationBphIndex;
        vm.SelectedSampleRateIndex = 1;

        SimulationSelection selection = resolver.GetSimulationSelection(availableSampleRates, availableSampleRateCount: 2);

        Assert.Equal(RunSelectionResolver.DefaultSimulationBph, selection.Bph);
        Assert.Equal(96000, selection.SampleRate);

        vm.SelectedSampleRateIndex = 2;

        Assert.Throws<InvalidOperationException>(() => resolver.GetSimulationSelection(availableSampleRates, availableSampleRateCount: 2));
    }

    private static RunSelectionResolver CreateResolver(MainWindowViewModel vm)
    {
        return new RunSelectionResolver(
            vm,
            AveragingPeriods,
            BphCatalog.ManualAutoBph,
            BphCatalog.ManualBph);
    }

    private static MainWindowViewModel CreateViewModel()
    {
        return new MainWindowViewModel(
            () => Task.CompletedTask,
            () => { },
            () => { },
            () => { });
    }
}
