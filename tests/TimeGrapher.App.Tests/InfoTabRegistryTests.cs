using Avalonia.Controls;
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

        Assert.Equal(2, content.ColumnDefinitions.Count);
        Assert.Single(registry.Consumers, consumer => consumer.TabId == InfoTabCatalog.TestPositionsTabId);
        Assert.DoesNotContain(registry.Registrations, registration => registration.Definition.Title == "Position Seq");
    }

    [Fact]
    public void RegistryRejectsMissingConsumer()
    {
        var tabControl = new TabControl();
        tabControl.Items.Add(new TabItem { Tag = InfoTabCatalog.RateScopeTabId });
        tabControl.Items.Add(new TabItem { Tag = InfoTabCatalog.SoundPrintTabId });

        var consumers = new IAnalysisFrameConsumer[]
        {
            new FakeConsumer(InfoTabCatalog.RateScopeTabId),
        };

        Assert.Throws<InvalidOperationException>(() => InfoTabRegistry.FromTabControl(tabControl, consumers));
    }

    private sealed class FakeConsumer : IAnalysisFrameConsumer
    {
        public FakeConsumer(string tabId)
        {
            TabId = tabId;
        }

        public string TabId { get; }

        public void Initialize(AnalysisTabResetContext context)
        {
            _ = context;
        }

        public void Reset(AnalysisTabResetContext context)
        {
            _ = context;
        }

        public void ObserveFrame(AnalysisFrame frame)
        {
            _ = frame;
        }

        public void RenderFrame(AnalysisFrame frame, AnalysisTabRenderContext context)
        {
            _ = frame;
            _ = context;
        }
    }
}
