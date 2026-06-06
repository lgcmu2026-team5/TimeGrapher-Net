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
        Assert.NotNull(registry.SoundImageControl);
        Assert.All(InfoTabCatalog.All, definition =>
            Assert.Contains(registry.Registrations, registration => registration.Definition.Id == definition.Id));
        Assert.All(InfoTabCatalog.All, definition =>
            Assert.True(registry.CreateRouter().HasConsumer(definition.Id)));
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
