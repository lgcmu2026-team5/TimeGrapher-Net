using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class InfoTabRegistryTests
{
    private const double VarioCapturedMinimumFontSize = 16.0;

    [Fact]
    public void RegistryCreatesCatalogTabsAndConsumers()
    {
        var tabControl = new TabControl();

        InfoTabRegistry registry = InfoTabRegistry.FromCatalog(tabControl, "Arial");

        Assert.Equal(InfoTabCatalog.All.Count, registry.Registrations.Count);
        Assert.Equal(InfoTabCatalog.All.Count, tabControl.ItemCount);
        // The registry throws on a kind without a factory; building it from the
        // catalog proves every tab constructs and yields a consumer per id.
        Assert.Equal(InfoTabCatalog.All.Count, registry.Consumers.Count);
        Assert.Equal(
            InfoTabCatalog.All.Select(tab => tab.Id).OrderBy(id => id, StringComparer.Ordinal),
            registry.Consumers.Select(consumer => consumer.TabId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.NotNull(registry.SoundImageControl);
        Assert.All(InfoTabCatalog.All, definition =>
            Assert.Contains(registry.Registrations, registration => registration.Definition.Id == definition.Id));
        Assert.All(InfoTabCatalog.All, definition =>
            Assert.True(registry.CreateRouter().HasConsumer(definition.Id)));
    }

    [Fact]
    public void RegistryCreatesOneSideBySidePositionsTab()
    {
        var tabControl = new TabControl();

        InfoTabRegistry registry = InfoTabRegistry.FromCatalog(tabControl, "Arial");

        InfoTabRegistration registration = Assert.Single(
            registry.Registrations,
            registration => registration.Definition.Id == InfoTabCatalog.TestPositionsTabId);
        var content = Assert.IsType<Grid>(registration.TabItem.Content);
        var buttonGrid = Assert.IsType<Grid>(content.Children[0]);
        Button[] buttons = buttonGrid.Children.OfType<Button>().ToArray();

        Assert.Equal(2, content.ColumnDefinitions.Count);
        Assert.Single(buttonGrid.ColumnDefinitions);
        Assert.Equal(WatchPositions.Count, buttonGrid.RowDefinitions.Count);
        Assert.Equal(WatchPositions.Count, buttons.Length);
        for (int i = 0; i < buttons.Length; i++)
        {
            Assert.Equal(i, Grid.GetRow(buttons[i]));
            Assert.Equal(VerticalAlignment.Stretch, buttons[i].VerticalAlignment);
        }

        Assert.Single(registry.Consumers, consumer => consumer.TabId == InfoTabCatalog.TestPositionsTabId);
        Assert.DoesNotContain(registry.Registrations, registration => registration.Definition.Title == "Position Seq");
    }

    [Fact]
    public void VarioSummaryShowsVerdictsWithoutNumericSublines()
    {
        Grid content = CreateVarioContent();
        var summaryCard = Assert.IsType<Border>(
            content.Children.Single(child => Grid.GetRow(child) == 0));
        var summaryStack = Assert.IsType<StackPanel>(summaryCard.Child);
        var summaryTopBar = Assert.IsType<Grid>(summaryStack.Children[0]);
        var overallText = Assert.IsType<TextBlock>(
            summaryTopBar.Children.Single(child => Grid.GetColumn(child) == 0));
        var summaryColumns = Assert.IsType<Grid>(summaryStack.Children[1]);
        StackPanel[] measureColumns = summaryColumns.Children
            .OfType<StackPanel>()
            .Where(column => Grid.GetColumn(column) is 0 or 1)
            .ToArray();

        Assert.Equal(2, measureColumns.Length);
        Assert.All(measureColumns, column => Assert.Equal(2, column.Children.Count));
        Assert.True(overallText.MinHeight >= 22);
        Assert.All(
            measureColumns.Select(column => Assert.IsType<TextBlock>(column.Children[1])),
            status => Assert.True(status.FontSize >= 24));
        Assert.Equal(" ", overallText.Text);
        Assert.True(summaryCard.Padding.Bottom <= 4);
        Assert.DoesNotContain(
            Descendants(summaryCard).OfType<TextBlock>(),
            text => text.Text == "VARIO SUMMARY");
    }

    [Fact]
    public void VarioCriteriaFlyoutWrapsRuleText()
    {
        Grid content = CreateVarioContent();
        Button criteriaButton = Descendants(content)
            .OfType<Button>()
            .Single(button => Equals(button.Content, "View criteria ▾"));
        var flyout = Assert.IsType<Flyout>(criteriaButton.Flyout);
        Assert.Equal(PlacementMode.BottomEdgeAlignedRight, flyout.Placement);
        var panel = Assert.IsType<StackPanel>(flyout.Content);
        Assert.True(panel.Width <= 360);
        TextBlock[] rules = panel.Children
            .OfType<TextBlock>()
            .Where(text => text.Text is { } value &&
                (value.StartsWith("Stable · in range:", StringComparison.Ordinal) ||
                 value.StartsWith("In range · unstable:", StringComparison.Ordinal) ||
                 value.StartsWith("Fast / Slow · out of range:", StringComparison.Ordinal) ||
                 value.StartsWith("Healthy:", StringComparison.Ordinal) ||
                 value.StartsWith("Slightly low / High:", StringComparison.Ordinal) ||
                 value.StartsWith("Low · service:", StringComparison.Ordinal)))
            .ToArray();

        Assert.Equal(6, rules.Length);
        Assert.DoesNotContain(rules, rule => rule.Text is { } value &&
            (value.StartsWith("OK:", StringComparison.Ordinal) ||
             value.StartsWith("Watch:", StringComparison.Ordinal) ||
             value.StartsWith("Alert:", StringComparison.Ordinal)));
        Assert.All(rules, rule =>
        {
            Assert.Equal(TextWrapping.Wrap, rule.TextWrapping);
            Assert.True(rule.MaxWidth <= 320);
        });
    }

    [Fact]
    public void VarioCriteriaGuideSitsAboveElapsedReadout()
    {
        Grid content = CreateVarioContent();
        var summaryCard = Assert.IsType<Border>(
            content.Children.Single(child => Grid.GetRow(child) == 0));
        var summaryStack = Assert.IsType<StackPanel>(summaryCard.Child);
        var summaryTopBar = Assert.IsType<Grid>(summaryStack.Children[0]);
        var summaryColumns = Assert.IsType<Grid>(summaryStack.Children[1]);
        Button criteriaButton = Assert.IsType<Button>(
            summaryTopBar.Children.Single(child => Grid.GetColumn(child) == 1));
        StackPanel elapsedColumn = Assert.IsType<StackPanel>(
            summaryColumns.Children.Single(child => Grid.GetColumn(child) == 2));

        Assert.Equal("View criteria ▾", criteriaButton.Content);
        Assert.True(criteriaButton.FontSize >= VarioCapturedMinimumFontSize);
        Assert.True(criteriaButton.MinWidth >= 168);
        Assert.True(criteriaButton.MinHeight >= 36);
        Assert.Equal(HorizontalAlignment.Right, criteriaButton.HorizontalAlignment);
        Assert.Equal(VerticalAlignment.Top, criteriaButton.VerticalAlignment);
        Assert.Equal(160, summaryColumns.ColumnDefinitions[2].Width.Value);
        Assert.Equal(HorizontalAlignment.Left, elapsedColumn.HorizontalAlignment);
        Assert.Contains(
            elapsedColumn.Children.OfType<TextBlock>(),
            text => text.Text == "ELAPSED");
    }

    [Fact]
    public void VarioReadoutStripHeadersAreHighContrast()
    {
        Grid content = CreateVarioContent();
        Border[] readouts = content.Children
            .OfType<Border>()
            .Where(child => Grid.GetRow(child) is 2 or 5)
            .ToArray();
        TextBlock[] headers = readouts
            .Select(readout => Assert.IsType<Grid>(readout.Child))
            .SelectMany(strip => strip.Children
            .OfType<TextBlock>()
            .Where(text => Grid.GetRow(text) == 0))
            .ToArray();

        Assert.Equal(2, readouts.Length);
        Assert.Equal(12, headers.Length);
        Assert.All(headers, header =>
        {
            Assert.True(header.Opacity >= 0.8);
            Assert.Equal(FontWeight.SemiBold, header.FontWeight);
            Assert.True(header.FontSize >= VarioCapturedMinimumFontSize);
        });
    }

    [Fact]
    public void VarioTextUsesCapturedMinimumFontSize()
    {
        Grid content = CreateVarioContent();
        Button criteriaButton = Descendants(content)
            .OfType<Button>()
            .Single(button => Equals(button.Content, "View criteria ▾"));
        var flyout = Assert.IsType<Flyout>(criteriaButton.Flyout);
        var criteriaPanel = Assert.IsType<StackPanel>(flyout.Content);

        TextBlock[] textBlocks = Descendants(content)
            .Concat(Descendants(criteriaPanel))
            .OfType<TextBlock>()
            .ToArray();

        Assert.NotEmpty(textBlocks);
        Assert.All(textBlocks, text => Assert.True(text.FontSize >= VarioCapturedMinimumFontSize));
    }

    [Fact]
    public void VarioGaugeHeadersUseBlankSpaceForAcceptBandBadges()
    {
        Grid content = CreateVarioContent();
        Grid rateHeader = Assert.IsType<Grid>(
            content.Children.Single(child => Grid.GetRow(child) == 1));
        Grid amplitudeHeader = Assert.IsType<Grid>(
            content.Children.Single(child => Grid.GetRow(child) == 4));

        Assert.Contains(
            Descendants(rateHeader).OfType<TextBlock>(),
            text => text.Text == "Acceptable band -10 to +10 s/d");
        Assert.Contains(
            Descendants(amplitudeHeader).OfType<TextBlock>(),
            text => text.Text == "Acceptable band 270 to 300°");
    }

    [Fact]
    public void VarioAcceptableBandBadgesAppearOnlyAfterMeasurementStarts()
    {
        InfoTabRegistration registration = CreateVarioRegistration();
        Grid content = Assert.IsType<Grid>(registration.TabItem.Content);
        Border rateBadge = AcceptBandBadge(content, "Acceptable band -10 to +10 s/d");
        Border amplitudeBadge = AcceptBandBadge(content, "Acceptable band 270 to 300°");

        Assert.False(rateBadge.IsVisible);
        Assert.False(amplitudeBadge.IsVisible);

        registration.Consumer.Initialize(new AnalysisTabResetContext(48000, 10, 250));

        Assert.False(rateBadge.IsVisible);
        Assert.False(amplitudeBadge.IsVisible);

        registration.Consumer.RenderFrame(
            new AnalysisFrame
            {
                MetricsHistory = new BeatMetricsHistorySnapshot
                {
                    Version = 1,
                    RateValid = true,
                    RateSPerDay = 4.5,
                    AmplitudeValid = true,
                    AmplitudeDeg = 285.0,
                    RateStats = new StatsSummary(true, -2.0, 4.5, 1.1, 1.0, 10),
                    AmplitudeStats = new StatsSummary(true, 275.0, 285.0, 280.0, 2.0, 10),
                },
            },
            new AnalysisTabRenderContext(48000, 2));

        Assert.True(rateBadge.IsVisible);
        Assert.True(amplitudeBadge.IsVisible);
    }

    [Fact]
    public void VarioReadoutStripsShowRenderedStatsNearEachGauge()
    {
        InfoTabRegistration registration = CreateVarioRegistration();
        Grid content = Assert.IsType<Grid>(registration.TabItem.Content);

        registration.Consumer.Initialize(new AnalysisTabResetContext(48000, 10, 250));
        registration.Consumer.RenderFrame(
            new AnalysisFrame
            {
                MetricsHistory = new BeatMetricsHistorySnapshot
                {
                    Version = 1,
                    RateValid = true,
                    RateSPerDay = 2.7,
                    AmplitudeValid = true,
                    AmplitudeDeg = 208.0,
                    RateStats = new StatsSummary(true, -8.1, 6.3, 4.2, 1.65, 200),
                    AmplitudeStats = new StatsSummary(true, 192.0, 216.0, 203.0, 5.2, 200),
                },
            },
            new AnalysisTabRenderContext(48000, 2));

        Assert.Equal(
            new[] { "-8.1 s/d", "+4.2 s/d", "+6.3 s/d", "1.65 s/d", "+2.7 s/d", "14.4 s/d" },
            ReadoutValues(content, row: 2));
        Assert.Equal(
            new[] { "192°", "203°", "216°", "5.20°", "208°", "24°" },
            ReadoutValues(content, row: 5));
    }

    [Fact]
    public void VarioLegendNamesLineStylesForMarkers()
    {
        Grid content = CreateVarioContent();
        var legend = Assert.IsType<TextBlock>(
            content.Children.Single(child => Grid.GetRow(child) == 7));
        string legendText = string.Concat(legend.Inlines!.OfType<Run>().Select(run => run.Text));

        Assert.Contains("Amber band", legendText);
        Assert.Contains("acceptable band", legendText);
        Assert.DoesNotContain("acceptable range", legendText);
        Assert.DoesNotContain("Pale green band + blue edge", legendText);
        Assert.Contains("Blue solid", legendText);
        Assert.Contains("Red solid", legendText);
        Assert.DoesNotContain("Current dashed", legendText);
        Assert.Contains("Black Dashed", legendText);
        Assert.Equal(TextWrapping.NoWrap, legend.TextWrapping);

        Run currentSwatch = Assert.Single(legend.Inlines!.OfType<Run>(), run => run.Text == "Black Dashed");
        var currentBrush = Assert.IsType<SolidColorBrush>(currentSwatch.Foreground);
        Assert.Equal(Colors.Black, currentBrush.Color);
    }

    private static Grid CreateVarioContent()
    {
        return Assert.IsType<Grid>(CreateVarioRegistration().TabItem.Content);
    }

    private static InfoTabRegistration CreateVarioRegistration()
    {
        var tabControl = new TabControl();
        InfoTabRegistry registry = InfoTabRegistry.FromCatalog(tabControl, "Arial");
        return Assert.Single(
            registry.Registrations,
            registration => registration.Definition.Id == InfoTabCatalog.VarioTabId);
    }

    private static Border AcceptBandBadge(Control content, string text)
    {
        return Descendants(content)
            .OfType<Border>()
            .Single(border => border.Child is TextBlock { Text: var value } && value == text);
    }

    private static string[] ReadoutValues(Grid content, int row)
    {
        var readout = Assert.IsType<Border>(
            content.Children.Single(child => Grid.GetRow(child) == row));
        var strip = Assert.IsType<Grid>(readout.Child);
        return strip.Children
            .OfType<TextBlock>()
            .Where(text => Grid.GetRow(text) == 1)
            .OrderBy(Grid.GetColumn)
            .Select(text => text.Text ?? string.Empty)
            .ToArray();
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        yield return control;

        if (control is Panel panel)
        {
            foreach (Control child in panel.Children.OfType<Control>())
            {
                foreach (Control descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }

        if (control is ContentControl { Content: Control content })
        {
            foreach (Control descendant in Descendants(content))
            {
                yield return descendant;
            }
        }

        if (control is Decorator { Child: Control childContent })
        {
            foreach (Control descendant in Descendants(childContent))
            {
                yield return descendant;
            }
        }
    }

}
