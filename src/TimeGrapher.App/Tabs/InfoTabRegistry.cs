using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ScottPlot.Avalonia;
using TimeGrapher.App.Rendering;
using TimeGrapher.App.ViewModels;

namespace TimeGrapher.App.Tabs;

internal sealed record InfoTabRegistration(
    InfoTabDefinition Definition,
    TabItem TabItem,
    IAnalysisFrameConsumer Consumer);

internal sealed class InfoTabRegistry
{
    private delegate InfoTabRegistration InfoTabFactory(
        InfoTabDefinition definition,
        InfoTabFactoryContext context);

    private sealed class InfoTabFactoryContext
    {
        public required string TextFontFamily { get; init; }
        public MainWindowViewModel? ViewModel { get; init; }
        public Image? SoundImageControl { get; set; }
    }

    private static readonly IReadOnlyDictionary<InfoTabKind, InfoTabFactory> Factories =
        new Dictionary<InfoTabKind, InfoTabFactory>
        {
            [InfoTabKind.RateScope] = CreateRateScopeRegistration,
            [InfoTabKind.SoundPrint] = CreateSoundPrintRegistration,
            [InfoTabKind.TraceDisplay] = CreateTraceDisplayRegistration,
            [InfoTabKind.Vario] = CreateVarioRegistration,
            [InfoTabKind.BeatErrorDiag] = CreateBeatErrorDiagRegistration,
            [InfoTabKind.Placeholder] = CreatePlaceholderRegistration,
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

    public static InfoTabRegistry FromCatalog(TabControl tabControl, string textFontFamily, MainWindowViewModel? viewModel = null)
    {
        tabControl.Items.Clear();
        var registrations = new List<InfoTabRegistration>(InfoTabCatalog.All.Count);
        var context = new InfoTabFactoryContext { TextFontFamily = textFontFamily, ViewModel = viewModel };

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

        // "Waiting for beat sync" overlay sits over the rate-error plot (the scope
        // below already shows the live waveform before sync).
        if (CreateWaitingOverlay(context.ViewModel) is { } overlay)
        {
            Grid.SetRow(overlay, 0);
            grid.Children.Add(overlay);
        }

        var renderer = new RateScopeRenderer(scopePlot, ratePlot, context.TextFontFamily);

        // A per-plot "Reset View" button, pinned to the top-right of its own plot row.
        Button MakeResetButton(int row, Action onClick)
        {
            var button = new Button
            {
                Content = "Reset View",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 10, 0),
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 11,
            };
            ToolTip.SetTip(button, "Reset this graph's view");
            button.Click += (_, _) => onClick();
            Grid.SetRow(button, row);
            return button;
        }

        grid.Children.Add(MakeResetButton(0, renderer.ResetRateView));
        grid.Children.Add(MakeResetButton(1, renderer.ResetScopeView));

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
        if (CreateWaitingOverlay(context.ViewModel) is { } overlay)
        {
            grid.Children.Add(overlay);
        }
        context.SoundImageControl = image;

        var renderer = new SoundPrintRenderer(image);
        var consumer = new SoundPrintFrameConsumer(renderer);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    private static InfoTabRegistration CreateTraceDisplayRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        var ratePlot = new AvaPlot();
        var amplitudePlot = new AvaPlot();

        var alertText = new TextBlock
        {
            Foreground = Avalonia.Media.Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        var alertBanner = new Border
        {
            Padding = new Thickness(8, 3),
            IsVisible = false,
            Child = alertText,
        };
        alertBanner.Bind(
            Border.BackgroundProperty,
            alertBanner.GetResourceObservable("ChromeAccentBrush"));

        var summaryText = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(8, 2),
        };
        var explanationText = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.65,
            Margin = new Thickness(8, 0, 8, 3),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = "Rate: above 0 = gaining, below 0 = losing; flat = stable. " +
                   "Amplitude: shaded band marks the healthy 270–300° range.",
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,*,Auto,Auto"),
        };
        Grid.SetRow(alertBanner, 0);
        Grid.SetRow(ratePlot, 1);
        Grid.SetRow(amplitudePlot, 2);
        Grid.SetRow(summaryText, 3);
        Grid.SetRow(explanationText, 4);
        grid.Children.Add(alertBanner);
        grid.Children.Add(ratePlot);
        grid.Children.Add(amplitudePlot);
        grid.Children.Add(summaryText);
        grid.Children.Add(explanationText);

        if (CreateWaitingOverlay(context.ViewModel) is { } overlay)
        {
            Grid.SetRow(overlay, 1);
            grid.Children.Add(overlay);
        }

        var renderer = new TraceDisplayRenderer(ratePlot, amplitudePlot, alertBanner, alertText, summaryText);

        var resetButton = new Button
        {
            Content = "Reset View",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 10, 0),
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11,
        };
        ToolTip.SetTip(resetButton, "Re-enable live auto-scaling on both graphs");
        resetButton.Click += (_, _) => renderer.ResetView();
        Grid.SetRow(resetButton, 1);
        grid.Children.Add(resetButton);

        var consumer = new TraceDisplayFrameConsumer(renderer);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    private static InfoTabRegistration CreateVarioRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        TextBlock SectionHeader(string text) => new()
        {
            Text = text,
            FontSize = 13,
            Margin = new Thickness(8, 4, 8, 0),
        };

        TextBlock Readout() => new()
        {
            FontSize = 12,
            Margin = new Thickness(8, 0, 8, 4),
        };

        var ratePlot = new AvaPlot();
        var rateReadout = Readout();
        var amplitudePlot = new AvaPlot();
        var amplitudeReadout = Readout();

        var legend = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.65,
            Margin = new Thickness(8, 0, 8, 3),
            Text = "Green band = acceptable range · blue = measured min/max · red = average · thin = current",
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,*,Auto,Auto"),
        };
        Control[] rows =
        {
            SectionHeader("RATE (s/d)"), ratePlot, rateReadout,
            SectionHeader("AMPLITUDE (°)"), amplitudePlot, amplitudeReadout,
            legend,
        };
        for (int i = 0; i < rows.Length; i++)
        {
            Grid.SetRow(rows[i], i);
            grid.Children.Add(rows[i]);
        }

        if (CreateWaitingOverlay(context.ViewModel) is { } overlay)
        {
            Grid.SetRow(overlay, 1);
            grid.Children.Add(overlay);
        }

        var renderer = new VarioRenderer(ratePlot, rateReadout, amplitudePlot, amplitudeReadout);
        var consumer = new VarioFrameConsumer(renderer);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    private static InfoTabRegistration CreateBeatErrorDiagRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        var tracePlot = new AvaPlot();

        var alertText = new TextBlock
        {
            Foreground = Avalonia.Media.Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        var alertBanner = new Border
        {
            Padding = new Thickness(8, 3),
            IsVisible = false,
            Child = alertText,
        };
        alertBanner.Bind(
            Border.BackgroundProperty,
            alertBanner.GetResourceObservable("ChromeAccentBrush"));

        // Numeric panel: label/value cells for the plan readings (rate,
        // amplitude, beat error, BPH) on the top row and the derived
        // DiffTicTac / DiffPeriod / AvgPeriod measures on the bottom row.
        var valueTexts = new TextBlock[BeatErrorReadout.Labels.Length];
        var readoutGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(8, 4, 8, 2),
        };
        for (int i = 0; i < BeatErrorReadout.Labels.Length; i++)
        {
            var label = new TextBlock
            {
                Text = BeatErrorReadout.Labels[i],
                FontSize = 11,
                Opacity = 0.65,
            };
            var value = new TextBlock
            {
                Text = VarioReadout.Missing,
                FontSize = 15,
            };
            valueTexts[i] = value;

            var cell = new StackPanel { Margin = new Thickness(0, 2, 12, 2) };
            cell.Children.Add(label);
            cell.Children.Add(value);
            Grid.SetRow(cell, i / 4);
            Grid.SetColumn(cell, i % 4);
            readoutGrid.Children.Add(cell);
        }

        var explanationText = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.65,
            Margin = new Thickness(8, 0, 8, 3),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = "Tic/toc traces: horizontal = on time; a positive reading slopes the trace upward. " +
                   "Separation between the two traces = beat error; a slope past 45° flags a major fault.",
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
        };
        Grid.SetRow(alertBanner, 0);
        Grid.SetRow(readoutGrid, 1);
        Grid.SetRow(tracePlot, 2);
        Grid.SetRow(explanationText, 3);
        grid.Children.Add(alertBanner);
        grid.Children.Add(readoutGrid);
        grid.Children.Add(tracePlot);
        grid.Children.Add(explanationText);

        if (CreateWaitingOverlay(context.ViewModel) is { } overlay)
        {
            Grid.SetRow(overlay, 2);
            grid.Children.Add(overlay);
        }

        var renderer = new BeatErrorDiagRenderer(tracePlot, alertBanner, alertText, valueTexts);

        var resetButton = new Button
        {
            Content = "Reset View",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 10, 0),
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11,
        };
        ToolTip.SetTip(resetButton, "Reset the trace view to its configured limits");
        resetButton.Click += (_, _) => renderer.ResetView();
        Grid.SetRow(resetButton, 2);
        grid.Children.Add(resetButton);

        var consumer = new BeatErrorDiagFrameConsumer(renderer);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    private static InfoTabRegistration CreatePlaceholderRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        _ = context;
        var label = new TextBlock
        {
            Text = definition.Title + " (준비 중)",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.5,
        };
        var grid = new Grid();
        grid.Children.Add(label);

        var consumer = new PlaceholderFrameConsumer(definition.Id);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    // Centered "waiting for beat sync" label, shown while a run has not yet locked the
    // tick/tock beat. Foreground/font come from the global TextBlock style (themed).
    private static TextBlock? CreateWaitingOverlay(MainWindowViewModel? viewModel)
    {
        if (viewModel == null)
        {
            return null;
        }

        var overlay = new TextBlock
        {
            Text = "Waiting for tick-tock sync…",
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        overlay.Bind(
            Visual.IsVisibleProperty,
            new Binding(nameof(MainWindowViewModel.IsAwaitingBeatSync)) { Source = viewModel });
        return overlay;
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
