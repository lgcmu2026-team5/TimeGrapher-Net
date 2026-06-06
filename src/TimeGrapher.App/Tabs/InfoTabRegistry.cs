using Avalonia.Controls;
using Avalonia.Media;
using ScottPlot.Avalonia;
using TimeGrapher.App.Rendering;

namespace TimeGrapher.App.Tabs;

public sealed record InfoTabRegistration(
    InfoTabDefinition Definition,
    TabItem TabItem,
    IAnalysisFrameConsumer Consumer);

public sealed class InfoTabRegistry
{
    private delegate InfoTabRegistration InfoTabFactory(
        InfoTabDefinition definition,
        InfoTabFactoryContext context);

    private sealed class InfoTabFactoryContext
    {
        public required string TextFontFamily { get; init; }
        public Image? SoundImageControl { get; set; }
    }

    private static readonly IReadOnlyDictionary<InfoTabKind, InfoTabFactory> Factories =
        new Dictionary<InfoTabKind, InfoTabFactory>
        {
            [InfoTabKind.RateScope] = CreateRateScopeRegistration,
            [InfoTabKind.SoundPrint] = CreateSoundPrintRegistration,
        };

    private readonly IReadOnlyList<InfoTabRegistration> _registrations;
    private readonly IAnalysisFrameConsumer[] _consumers;

    private InfoTabRegistry(IReadOnlyList<InfoTabRegistration> registrations, Image? soundImageControl)
    {
        _registrations = registrations;
        _consumers = registrations.Select(registration => registration.Consumer).ToArray();
        SoundImageControl = soundImageControl;
    }

    public IReadOnlyList<InfoTabRegistration> Registrations => _registrations;
    public IReadOnlyList<IAnalysisFrameConsumer> Consumers => _consumers;
    public Image? SoundImageControl { get; }

    public static InfoTabRegistry FromCatalog(TabControl tabControl, string textFontFamily)
    {
        tabControl.Items.Clear();
        var registrations = new List<InfoTabRegistration>(InfoTabCatalog.All.Count);
        var context = new InfoTabFactoryContext { TextFontFamily = textFontFamily };

        foreach (InfoTabDefinition definition in InfoTabCatalog.All)
        {
            InfoTabRegistration registration = CreateRegistration(definition, context);
            tabControl.Items.Add(registration.TabItem);
            registrations.Add(registration);
        }

        if (tabControl.SelectedIndex < 0 && tabControl.ItemCount > 0)
        {
            tabControl.SelectedIndex = 0;
        }

        return new InfoTabRegistry(registrations, context.SoundImageControl);
    }

    public static InfoTabRegistry FromTabControl(TabControl tabControl, IEnumerable<IAnalysisFrameConsumer> consumers)
    {
        var consumerByTab = consumers.ToDictionary(consumer => consumer.TabId, StringComparer.Ordinal);
        var tabById = new Dictionary<string, TabItem>(StringComparer.Ordinal);

        foreach (TabItem tab in tabControl.Items.OfType<TabItem>())
        {
            if (tab.Tag is not string tabId || string.IsNullOrWhiteSpace(tabId))
            {
                throw new InvalidOperationException("Every info tab must declare a non-empty Tag.");
            }

            if (!InfoTabCatalog.TryGet(tabId, out _))
            {
                throw new InvalidOperationException($"Info tab '{tabId}' is not registered in InfoTabCatalog.");
            }

            if (!tabById.TryAdd(tabId, tab))
            {
                throw new InvalidOperationException($"Duplicate info tab id '{tabId}'.");
            }
        }

        var registrations = new List<InfoTabRegistration>(InfoTabCatalog.All.Count);
        foreach (InfoTabDefinition definition in InfoTabCatalog.All)
        {
            if (!tabById.TryGetValue(definition.Id, out TabItem? tab))
            {
                throw new InvalidOperationException($"InfoTabCatalog tab '{definition.Id}' has no matching TabItem.");
            }

            if (!consumerByTab.TryGetValue(definition.Id, out IAnalysisFrameConsumer? consumer))
            {
                throw new InvalidOperationException($"Info tab '{definition.Id}' has no analysis frame consumer.");
            }

            registrations.Add(new InfoTabRegistration(definition, tab, consumer));
        }

        return new InfoTabRegistry(registrations, null);
    }

    public AnalysisFrameRouter CreateRouter()
    {
        return new AnalysisFrameRouter(_registrations.Select(registration => registration.Consumer));
    }

    private static InfoTabRegistration CreateRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        if (!Factories.TryGetValue(definition.Kind, out InfoTabFactory? factory))
        {
            throw new InvalidOperationException($"Unsupported info tab kind '{definition.Kind}'.");
        }

        return factory(definition, context);
    }

    private static InfoTabRegistration CreateRateScopeRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        var ratePlot = new AvaPlot();
        var scopePlot = new AvaPlot();
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,*"),
        };
        Grid.SetRow(ratePlot, 0);
        Grid.SetRow(scopePlot, 1);
        grid.Children.Add(ratePlot);
        grid.Children.Add(scopePlot);

        var renderer = new RateScopeRenderer(scopePlot, ratePlot, context.TextFontFamily);
        var consumer = new RateScopeFrameConsumer(renderer);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    private static InfoTabRegistration CreateSoundPrintRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        var image = new Image
        {
            Stretch = Stretch.Fill,
        };
        var grid = new Grid();
        grid.Children.Add(image);
        context.SoundImageControl = image;

        var renderer = new SoundPrintRenderer(image);
        var consumer = new SoundPrintFrameConsumer(renderer);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    private static TabItem CreateTabItem(InfoTabDefinition definition, Control content)
    {
        return new TabItem
        {
            Header = definition.Title,
            Tag = definition.Id,
            Content = content,
        };
    }
}
