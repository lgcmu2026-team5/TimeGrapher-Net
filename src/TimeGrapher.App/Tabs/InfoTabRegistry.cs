using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ScottPlot.Avalonia;
using TimeGrapher.App.Rendering;
using TimeGrapher.App.ViewModels;
using TimeGrapher.Core.Shared;

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
            [InfoTabKind.ScopeSweep] = CreateScopeSweepRegistration,
            [InfoTabKind.Vario] = CreateVarioRegistration,
            [InfoTabKind.BeatErrorDiag] = CreateBeatErrorDiagRegistration,
            [InfoTabKind.MultiFilterScope] = CreateMultiFilterScopeRegistration,
            [InfoTabKind.LongTermPerformance] = CreateLongTermPerfRegistration,
            [InfoTabKind.TestPositions] = CreateTestPositionsRegistration,
            [InfoTabKind.MultiPositionSequence] = CreateMultiPositionSeqRegistration,
            [InfoTabKind.BeatNoiseScope] = CreateBeatNoiseScopeRegistration,
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

    private static InfoTabRegistration CreateScopeSweepRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        var sweepPlot = new AvaPlot();

        // Compact reference line of the most recent measurements under the plot
        // (the plan's "compare the live waveform against the most recent
        // timing test" readings).
        var referenceText = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(8, 2),
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
        };
        Grid.SetRow(sweepPlot, 0);
        Grid.SetRow(referenceText, 1);
        grid.Children.Add(sweepPlot);
        grid.Children.Add(referenceText);

        if (CreateWaitingOverlay(context.ViewModel) is { } overlay)
        {
            Grid.SetRow(overlay, 0);
            grid.Children.Add(overlay);
        }

        // 1x/2x/4x sweep-time selector pinned to the top-right of the plot. The
        // buttons write the shared SweepMultiple view-model property; MainWindow
        // forwards the change to the running analysis worker (the
        // SetSoundBackgroundColor flow). The active multiple renders disabled.
        if (context.ViewModel is { } viewModel)
        {
            int[] multiples = { 1, 2, 4 };
            var buttons = new Button[multiples.Length];

            void UpdateButtonStates()
            {
                for (int i = 0; i < buttons.Length; i++)
                {
                    buttons[i].IsEnabled = multiples[i] != viewModel.SweepMultiple;
                }
            }

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 10, 0),
            };
            for (int i = 0; i < multiples.Length; i++)
            {
                int multiple = multiples[i];
                var button = new Button
                {
                    Content = multiple + "x",
                    Padding = new Thickness(8, 2, 8, 2),
                    FontSize = 11,
                };
                ToolTip.SetTip(button, $"Sweep window = {multiple}x the beat period");
                button.Click += (_, _) =>
                {
                    viewModel.SweepMultiple = multiple;
                    UpdateButtonStates();
                };
                buttons[i] = button;
                buttonRow.Children.Add(button);
            }

            UpdateButtonStates();
            Grid.SetRow(buttonRow, 0);
            grid.Children.Add(buttonRow);
        }

        var renderer = new ScopeSweepRenderer(sweepPlot, referenceText);
        var consumer = new ScopeSweepFrameConsumer(renderer);
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

    private static InfoTabRegistration CreateMultiFilterScopeRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        // Four vertically stacked plots (F0..F3 of the same signal), each under
        // its one-line description, so the filter views compare at a glance. The
        // raw waveform shows before beat sync, so no waiting overlay is added.
        _ = context;
        IReadOnlyList<MultiFilterScopeLane> lanes = MultiFilterScopeLanes.All;
        var plots = new AvaPlot[lanes.Count];
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions(
                string.Join(",", Enumerable.Repeat("Auto,*", lanes.Count))),
        };

        for (int i = 0; i < lanes.Count; i++)
        {
            var description = new TextBlock
            {
                Text = lanes[i].Label + " — " + lanes[i].Description,
                FontSize = 11,
                Opacity = 0.65,
                Margin = new Thickness(8, 3, 8, 0),
            };
            plots[i] = new AvaPlot();
            Grid.SetRow(description, 2 * i);
            Grid.SetRow(plots[i], 2 * i + 1);
            grid.Children.Add(description);
            grid.Children.Add(plots[i]);
        }

        var renderer = new MultiFilterScopeRenderer(plots);
        var consumer = new MultiFilterScopeFrameConsumer(renderer);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    private static InfoTabRegistration CreateLongTermPerfRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        // Three stacked panes (rate / amplitude / beat error over elapsed time)
        // above the overall-average footer and a one-line legend.
        var ratePlot = new AvaPlot();
        var amplitudePlot = new AvaPlot();
        var beatErrorPlot = new AvaPlot();

        var footerText = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(8, 2),
        };
        var legendText = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.65,
            Margin = new Thickness(8, 0, 8, 3),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = "Shaded band = range of typical variation (bucket min–max) · solid = bucket average · " +
                   "dashed = overall average. Resolution coarsens automatically as the run grows (see footer).",
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,*,*,Auto,Auto"),
        };
        Control[] rows = { ratePlot, amplitudePlot, beatErrorPlot, footerText, legendText };
        for (int i = 0; i < rows.Length; i++)
        {
            Grid.SetRow(rows[i], i);
            grid.Children.Add(rows[i]);
        }

        if (CreateWaitingOverlay(context.ViewModel) is { } overlay)
        {
            Grid.SetRow(overlay, 0);
            grid.Children.Add(overlay);
        }

        var renderer = new LongTermPerfRenderer(ratePlot, amplitudePlot, beatErrorPlot, footerText);

        var resetButton = new Button
        {
            Content = "Reset View",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 10, 0),
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11,
        };
        ToolTip.SetTip(resetButton, "Re-enable live auto-scaling on all three graphs");
        resetButton.Click += (_, _) => renderer.ResetView();
        Grid.SetRow(resetButton, 0);
        grid.Children.Add(resetButton);

        var consumer = new LongTermPerfFrameConsumer(renderer);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    private static InfoTabRegistration CreateTestPositionsRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        // One large button per NIHS 95-10 / ISO 3158 test position in a
        // 2-column grid (the manual's horizontal pair CH/CB on the first row).
        // Clicking writes the shared SelectedPositionIndex view-model property;
        // MainWindow forwards the change to the running analysis worker (the
        // SetSoundBackgroundColor flow) and the status-bar "POS …" indicator
        // updates from the same property, so the active position stays visible
        // at all times while measuring. The consumer re-highlights from the
        // position Core stamps into the metrics-history snapshot.
        IReadOnlyList<WatchPosition> positions = WatchPositions.All;
        var buttons = new Button[positions.Count];
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("*,*,*"),
            Margin = new Thickness(8),
        };

        for (int i = 0; i < positions.Count; i++)
        {
            WatchPosition position = positions[i];
            var shortText = new TextBlock
            {
                Text = position.ShortName(),
                FontSize = 34,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var longText = new TextBlock
            {
                Text = position.LongName(),
                FontSize = 13,
                Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var content = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(shortText);
            content.Children.Add(longText);

            var button = new Button
            {
                Content = content,
                Classes = { "PositionButton" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(4),
            };
            ToolTip.SetTip(button, $"Tag new measurements as {position.ShortName()} — {position.LongName()}");
            Grid.SetRow(button, i / 2);
            Grid.SetColumn(button, i % 2);
            buttons[i] = button;
            grid.Children.Add(button);
        }

        var initialPosition = (WatchPosition)(context.ViewModel?.SelectedPositionIndex ?? 0);
        var renderer = new TestPositionsRenderer(buttons, initialPosition);

        for (int i = 0; i < buttons.Length; i++)
        {
            var position = (WatchPosition)i;
            buttons[i].Click += (_, _) =>
            {
                if (context.ViewModel is { } viewModel)
                {
                    viewModel.SelectedPositionIndex = (int)position;
                }

                renderer.SetActivePosition(position);
            };
        }

        var consumer = new TestPositionsFrameConsumer(renderer);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    private static InfoTabRegistration CreateMultiPositionSeqRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        // Sequence results table (POS | RATE | AMP | BEAT ERR | BEATS, one row
        // per measured position, the active position's row highlighted) above
        // the X / D / vertical-vs-horizontal summary block; the accent banner
        // reports the balance-wheel unbalance hint. The renderer fills the
        // table from the cumulative snapshot's PositionSummary list.
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

        var tableGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"),
            Margin = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Top,
        };

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
            Text = "X = mean of all measured positions · D = max−min spread · " +
                   "DVH = vertical minus horizontal rate mean. A rate spread above " +
                   "15 s/d among the hanging positions hints at balance-wheel unbalance.",
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
        };
        Grid.SetRow(alertBanner, 0);
        Grid.SetRow(tableGrid, 1);
        Grid.SetRow(summaryText, 2);
        Grid.SetRow(explanationText, 3);
        grid.Children.Add(alertBanner);
        grid.Children.Add(tableGrid);
        grid.Children.Add(summaryText);
        grid.Children.Add(explanationText);

        if (CreateWaitingOverlay(context.ViewModel) is { } overlay)
        {
            Grid.SetRow(overlay, 1);
            grid.Children.Add(overlay);
        }

        // "Reset Sequence" raises the shared view-model command; MainWindow
        // forwards it to the running analysis worker (the SetSoundBackgroundColor
        // flow), which clears the per-position aggregates for a fresh cycle.
        var resetButton = new Button
        {
            Content = "Reset Sequence",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 10, 0),
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11,
            Command = context.ViewModel?.ResetSequenceCommand,
        };
        ToolTip.SetTip(resetButton, "Clear every position's results and start a new measurement cycle");
        Grid.SetRow(resetButton, 1);
        grid.Children.Add(resetButton);

        var renderer = new MultiPositionSeqRenderer(tableGrid, alertBanner, alertText, summaryText);
        var consumer = new MultiPositionSeqFrameConsumer(renderer);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    private static InfoTabRegistration CreateBeatNoiseScopeRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        // Scope 1: toolbar (range / MIRROR / Σ / lift angle), the enlarged
        // selected-or-latest beat, and the frameless strip lane of the 8 most
        // recent beats. Scope 2: the two averaged lanes above their readout.
        var mainPlot = new AvaPlot();
        var stripPlot = new AvaPlot
        {
            Height = 72,
        };
        var averagePlot = new AvaPlot();

        var liftText = new TextBlock
        {
            Text = "LIFT —",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        var averageText = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(8, 2),
        };

        var renderer = new BeatNoiseScopeRenderer(mainPlot, stripPlot, averagePlot, liftText, averageText);

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(8, 4),
        };

        // 20 / 200 / 400 ms range selector; the active range renders disabled
        // (the Scope Sweep 1x/2x/4x button pattern).
        int[] ranges = { 20, 200, 400 };
        var rangeButtons = new Button[ranges.Length];

        void UpdateRangeButtonStates()
        {
            for (int i = 0; i < rangeButtons.Length; i++)
            {
                rangeButtons[i].IsEnabled = ranges[i] != renderer.RangeMs;
            }
        }

        for (int i = 0; i < ranges.Length; i++)
        {
            int rangeMs = ranges[i];
            var button = new Button
            {
                Content = rangeMs + " ms",
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 11,
            };
            ToolTip.SetTip(button, $"Show the first {rangeMs} ms of the beat window");
            button.Click += (_, _) =>
            {
                renderer.SetRangeMs(rangeMs);
                UpdateRangeButtonStates();
            };
            rangeButtons[i] = button;
            toolbar.Children.Add(button);
        }

        UpdateRangeButtonStates();

        var mirrorToggle = new ToggleButton
        {
            Content = "MIRROR",
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
        };
        ToolTip.SetTip(mirrorToggle, "Mirror the rectified envelope below zero (bipolar waveform look)");
        mirrorToggle.IsCheckedChanged += (_, _) => renderer.SetMirror(mirrorToggle.IsChecked == true);
        toolbar.Children.Add(mirrorToggle);

        // Σ writes the shared SigmaAveraging view-model property; MainWindow
        // forwards the change to the running analysis worker (the
        // SetSweepMultiple flow). Display state comes back via the snapshot.
        var sigmaToggle = new ToggleButton
        {
            Content = "Σ",
            Padding = new Thickness(10, 2, 10, 2),
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
            IsChecked = context.ViewModel?.SigmaAveraging == true,
        };
        ToolTip.SetTip(sigmaToggle, "Average 50 + 50 beat noises into the two Scope 2 traces");
        sigmaToggle.IsCheckedChanged += (_, _) =>
        {
            if (context.ViewModel is { } viewModel)
            {
                viewModel.SigmaAveraging = sigmaToggle.IsChecked == true;
            }
        };
        toolbar.Children.Add(sigmaToggle);
        toolbar.Children.Add(liftText);

        // Strip-lane hit test: the plot is frameless, so the slot follows from
        // the press position's horizontal fraction across the control.
        stripPlot.PointerPressed += (_, e) =>
        {
            if (stripPlot.Bounds.Width > 0)
            {
                renderer.SelectStripAtFraction(e.GetPosition(stripPlot).X / stripPlot.Bounds.Width);
            }
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,2*,Auto,*,Auto"),
        };
        Control[] rows = { toolbar, mainPlot, stripPlot, averagePlot, averageText };
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

        var consumer = new BeatNoiseScopeFrameConsumer(renderer);
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
