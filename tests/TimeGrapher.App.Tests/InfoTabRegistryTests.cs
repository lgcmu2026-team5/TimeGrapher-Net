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
        var summaryColumns = Assert.IsType<Grid>(summaryStack.Children[1]);
        StackPanel[] measureColumns = summaryColumns.Children
            .OfType<StackPanel>()
            .Where(column => Grid.GetColumn(column) is 0 or 1)
            .ToArray();

        Assert.Equal(2, measureColumns.Length);
        Assert.All(measureColumns, column => Assert.Equal(2, column.Children.Count));
    }

    [Fact]
    public void VarioCriteriaFlyoutWrapsRuleText()
    {
        Grid content = CreateVarioContent();
        Button criteriaButton = Descendants(content)
            .OfType<Button>()
            .Single(button => Equals(button.Content, "Criteria ▾"));
        var flyout = Assert.IsType<Flyout>(criteriaButton.Flyout);
        var panel = Assert.IsType<StackPanel>(flyout.Content);
        TextBlock[] rules = panel.Children
            .OfType<TextBlock>()
            .Where(text => text.Text is { } value &&
                (value.StartsWith("OK:", StringComparison.Ordinal) ||
                 value.StartsWith("Watch:", StringComparison.Ordinal) ||
                 value.StartsWith("Alert:", StringComparison.Ordinal)))
            .ToArray();

        Assert.Equal(6, rules.Length);
        Assert.All(rules, rule =>
        {
            Assert.Equal(TextWrapping.Wrap, rule.TextWrapping);
            Assert.True(rule.MaxWidth <= 340);
        });
    }

    [Fact]
    public void VarioTableHeadersAreHighContrast()
    {
        Grid content = CreateVarioContent();
        var table = Assert.IsType<Grid>(
            content.Children.Single(child => Grid.GetRow(child) == 6));
        TextBlock[] headers = table.Children
            .OfType<TextBlock>()
            .Where(text => Grid.GetRow(text) == 0)
            .ToArray();

        Assert.Equal(6, headers.Length);
        Assert.All(headers, header =>
        {
            Assert.True(header.Opacity >= 0.8);
            Assert.Equal(FontWeight.SemiBold, header.FontWeight);
        });
    }

    [Fact]
    public void VarioLegendNamesLineStylesForMarkers()
    {
        Grid content = CreateVarioContent();
        var legend = Assert.IsType<TextBlock>(
            content.Children.Single(child => Grid.GetRow(child) == 7));
        string legendText = string.Concat(legend.Inlines!.OfType<Run>().Select(run => run.Text));

        Assert.Contains("Blue solid", legendText);
        Assert.Contains("Red solid", legendText);
        Assert.Contains("Black dashed", legendText);
    }

    private static Grid CreateVarioContent()
    {
        var tabControl = new TabControl();
        InfoTabRegistry registry = InfoTabRegistry.FromCatalog(tabControl, "Arial");
        InfoTabRegistration registration = Assert.Single(
            registry.Registrations,
            registration => registration.Definition.Id == InfoTabCatalog.VarioTabId);

        return Assert.IsType<Grid>(registration.TabItem.Content);
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
