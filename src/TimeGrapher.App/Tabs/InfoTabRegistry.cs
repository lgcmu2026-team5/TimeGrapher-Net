using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
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
            [InfoTabKind.BeatNoiseScope] = CreateBeatNoiseScopeRegistration,
            [InfoTabKind.EscapementAnalyzer] = CreateEscapementAnalyzerRegistration,
            [InfoTabKind.WaveformCompare] = CreateWaveformCompareRegistration,
            [InfoTabKind.Spectrogram] = CreateSpectrogramRegistration,
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

    public AnalysisFrameRouter CreateRouter()
    {
        return new AnalysisFrameRouter(_registrations.Select(registration => registration.Consumer));
    }

    /// <summary>
    /// Small overlay-chrome button (the shared styling of the per-plot
    /// "Reset View" buttons and toolbar selectors). Position it at the call
    /// site (alignment / margin / grid row).
    /// </summary>
    private static Button CreateOverlayButton(string content, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11,
        };
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Pins an overlay button to the top-right corner of a plot grid row.</summary>
    private static Button CreatePinnedResetViewButton(string tooltip, int row, Action onClick)
    {
        Button button = CreateOverlayButton("Reset View", tooltip, onClick);
        button.HorizontalAlignment = HorizontalAlignment.Right;
        button.VerticalAlignment = VerticalAlignment.Top;
        button.Margin = new Thickness(0, 6, 10, 0);
        Grid.SetRow(button, row);
        return button;
    }

    /// <summary>
    /// Accent alert banner shared by the alerting tabs (hidden until the
    /// renderer sets a message); the background binds to the theme accent so
    /// it recolors with the chrome.
    /// </summary>
    private static Border CreateAlertBanner(out TextBlock alertText)
    {
        var text = new TextBlock
        {
            Foreground = Avalonia.Media.Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        var banner = new Border
        {
            Padding = new Thickness(8, 3),
            IsVisible = false,
            Child = text,
        };
        banner.Bind(
            Border.BackgroundProperty,
            banner.GetResourceObservable("ChromeAccentBrush"));
        alertText = text;
        return banner;
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

        grid.Children.Add(CreatePinnedResetViewButton("Reset this graph's view", row: 0, renderer.ResetRateView));
        grid.Children.Add(CreatePinnedResetViewButton("Reset this graph's view", row: 1, renderer.ResetScopeView));

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

        Border alertBanner = CreateAlertBanner(out TextBlock alertText);


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

        grid.Children.Add(CreatePinnedResetViewButton("Re-enable live auto-scaling on both graphs", row: 1, renderer.ResetView));

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
        // Reset View sits top-left so it never collides with the 1x/2x/4x
        // selector pinned top-right.
        Button resetView = CreateOverlayButton(
            "Reset View", "Re-enable live auto-fitting of the sweep window", renderer.ResetView);
        resetView.HorizontalAlignment = HorizontalAlignment.Left;
        resetView.VerticalAlignment = VerticalAlignment.Top;
        resetView.Margin = new Thickness(10, 6, 0, 0);
        Grid.SetRow(resetView, 0);
        grid.Children.Add(resetView);
        var consumer = new ScopeSweepFrameConsumer(renderer);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    private static InfoTabRegistration CreateVarioRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        var font = new FontFamily(context.TextFontFamily);

        Grid GaugeHeader(string text, string bandText)
        {
            var header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
                Margin = new Thickness(8, 2, 8, 0),
            };
            var title = new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var bandBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x72, 0xB2)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x72, 0xB2)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(10, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = bandText,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(title, 0);
            Grid.SetColumn(bandBadge, 1);
            header.Children.Add(title);
            header.Children.Add(bandBadge);
            return header;
        }

        var ratePlot = new AvaPlot();
        var amplitudePlot = new AvaPlot();

        // --- SUMMARY bar: verdicts and elapsed; exact numbers stay in the table ---
        var rateStatus = new TextBlock { FontSize = 20, FontWeight = FontWeight.Bold };
        var ampStatus = new TextBlock { FontSize = 20, FontWeight = FontWeight.Bold };
        var elapsedValue = new TextBlock { FontSize = 22, FontWeight = FontWeight.Bold, FontFamily = font };

        StackPanel SummaryColumn(string caption, TextBlock status)
        {
            var sp = new StackPanel { Margin = new Thickness(12, 4, 12, 4) };
            sp.Children.Add(new TextBlock { Text = caption, FontSize = 11, Opacity = 0.68, FontWeight = FontWeight.SemiBold });
            sp.Children.Add(status);
            return sp;
        }

        var elapsedColumn = new StackPanel { Margin = new Thickness(12, 4, 12, 4) };
        elapsedColumn.Children.Add(new TextBlock { Text = "ELAPSED", FontSize = 11, Opacity = 0.68, FontWeight = FontWeight.SemiBold });
        elapsedColumn.Children.Add(elapsedValue);

        var criteriaButton = new Button
        {
            Content = "Criteria ▾",
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 8, 0),
            Flyout = new Flyout { Content = BuildVarioCriteria() },
        };

        var summaryColumns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto,Auto") };
        Control[] summaryCells =
        {
            SummaryColumn("RATE", rateStatus),
            SummaryColumn("AMPLITUDE", ampStatus),
            elapsedColumn,
            criteriaButton,
        };
        for (int c = 0; c < summaryCells.Length; c++)
        {
            Grid.SetColumn(summaryCells[c], c);
            summaryColumns.Children.Add(summaryCells[c]);
        }

        var overallText = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 4, 10, 4),
        };
        var overallBox = new Border
        {
            Child = overallText,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(12, 6, 12, 0),
            IsVisible = false,
        };

        var summaryStack = new StackPanel();
        summaryStack.Children.Add(overallBox);
        summaryStack.Children.Add(summaryColumns);
        var summaryCard = new Border
        {
            Child = summaryStack,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xC9)),
            Background = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(16, 8, 16, 4),
            Padding = new Thickness(0, 0, 0, 6),
        };

        // --- Numeric table: exact numbers live here; gauges show position only ---
        string[] columnHeaders = { "Min", "Max", "Spread (Max−Min)", "Average", "Std dev (σ)", "Current" };
        uint[] columnColors = { 0xFF2D7DD2, 0xFF2D7DD2, 0x00000000, 0xFFC0392B, 0x00000000, 0x00000000 };

        TextBlock[] BuildCells()
        {
            var cells = new TextBlock[VarioRenderer.CellCount];
            for (int i = 0; i < cells.Length; i++)
            {
                var cell = new TextBlock
                {
                    Text = VarioReadout.Missing,
                    FontFamily = font,
                    FontSize = 13,
                    Margin = new Thickness(0, 1, 16, 1),
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                if (columnColors[i] != 0u)
                {
                    cell.Foreground = new SolidColorBrush(Color.FromUInt32(columnColors[i]));
                }

                cells[i] = cell;
            }

            return cells;
        }

        TextBlock[] rateCells = BuildCells();
        TextBlock[] amplitudeCells = BuildCells();

        var table = new Grid
        {
            Margin = new Thickness(16, 2, 16, 2),
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
        };
        for (int c = 0; c < columnHeaders.Length; c++)
        {
            var header = new TextBlock
            {
                Text = columnHeaders[c],
                FontSize = 11,
                Opacity = 0.82,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 1, 16, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, c + 1);
            table.Children.Add(header);
        }

        void AddTableRow(string label, TextBlock[] cells, int row)
        {
            var rowLabel = new TextBlock { Text = label, FontSize = 13, Margin = new Thickness(0, 1, 16, 1) };
            Grid.SetRow(rowLabel, row);
            Grid.SetColumn(rowLabel, 0);
            table.Children.Add(rowLabel);
            for (int c = 0; c < cells.Length; c++)
            {
                Grid.SetRow(cells[c], row);
                Grid.SetColumn(cells[c], c + 1);
                table.Children.Add(cells[c]);
            }
        }

        AddTableRow("Rate (s/d)", rateCells, 1);
        AddTableRow("Amplitude (°)", amplitudeCells, 2);

        // --- Legend (colour words match the gauge markers) ---
        var legend = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.95,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(16, 0, 16, 6),
            TextWrapping = TextWrapping.Wrap,
        };
        Run Swatch(string text, Color color) => new(text) { Foreground = new SolidColorBrush(color), FontWeight = FontWeight.Bold };
        legend.Inlines = new InlineCollection
        {
            Swatch("Pale green band + blue edge", Color.FromRgb(0x00, 0x72, 0xB2)),
            new Run(" = acceptable range     "),
            Swatch("Blue solid", Color.FromRgb(0x2D, 0x7D, 0xD2)),
            new Run(" = measured min/max     "),
            Swatch("Red solid", Color.FromRgb(0xC0, 0x39, 0x2B)),
            new Run(" = average     "),
            Swatch("Black dashed", Color.FromRgb(0x20, 0x20, 0x20)),
            new Run(" = current"),
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,*,Auto,Auto"),
        };
        Control[] rows =
        {
            summaryCard,
            GaugeHeader("RATE (s/d)", "Accept band -10 to +10 s/d"), ratePlot,
            GaugeHeader("AMPLITUDE (°)", "Accept band 270 to 300°"), amplitudePlot,
            table, legend,
        };
        for (int i = 0; i < rows.Length; i++)
        {
            Grid.SetRow(rows[i], i);
            grid.Children.Add(rows[i]);
        }

        if (CreateWaitingOverlay(context.ViewModel) is { } overlay)
        {
            Grid.SetRow(overlay, 2);
            grid.Children.Add(overlay);
        }

        var summary = new VarioSummaryControls(
            rateStatus, ampStatus, elapsedValue, overallBox, overallText);
        var renderer = new VarioRenderer(
            ratePlot, amplitudePlot, summary, new VarioTableControls(rateCells, amplitudeCells), context.TextFontFamily);
        var consumer = new VarioFrameConsumer(renderer);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    /// <summary>
    /// The Vario "Criteria" flyout: the verdict thresholds, built from the same
    /// constants the evaluator uses so the popup cannot drift from the live rules.
    /// </summary>
    private static Control BuildVarioCriteria()
    {
        double band = VarioGaugePolicy.RateAcceptMaxSPerDay;
        double ampMin = VarioGaugePolicy.AmplitudeAcceptMinDeg;
        double ampMax = VarioGaugePolicy.AmplitudeAcceptMaxDeg;
        double service = VarioVerdict.AmplitudeServiceDeg;
        double sigma = VarioVerdict.RateUnstableSigma;

        Color Good = Color.FromRgb(0x00, 0x72, 0xB2);
        Color Warn = Color.FromRgb(0xB0, 0x6A, 0x00);
        Color Bad = Color.FromRgb(0xC0, 0x30, 0x30);

        TextBlock Title(string t) => new() { Text = t, FontWeight = FontWeight.Bold, FontSize = 13, Margin = new Thickness(0, 6, 0, 2) };
        TextBlock Rule(string t, Color c) => new()
        {
            Text = t,
            FontSize = 12,
            Foreground = new SolidColorBrush(c),
            Margin = new Thickness(0, 1, 0, 1),
            MaxWidth = 340,
            TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel { Margin = new Thickness(12), MaxWidth = 360 };
        panel.Children.Add(new TextBlock { Text = "Assessment criteria", FontWeight = FontWeight.Bold, FontSize = 14 });
        panel.Children.Add(new TextBlock
        {
            Text = $"Shown after {VarioVerdict.MinSamples} beats, classified from the average, for the current watch position.",
            FontSize = 11,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
            Margin = new Thickness(0, 2, 0, 0),
        });
        panel.Children.Add(Title("Rate (s/d)"));
        panel.Children.Add(Rule($"OK: stable in range, average within ±{band:0} s/d and σ ≤ {sigma:0}", Good));
        panel.Children.Add(Rule($"Watch: in range but unstable, σ > {sigma:0}", Warn));
        panel.Children.Add(Rule($"Alert: fast or slow, average beyond ±{band:0} s/d", Bad));
        panel.Children.Add(Title("Amplitude (°)"));
        panel.Children.Add(Rule($"OK: healthy, average {ampMin:0}–{ampMax:0}°", Good));
        panel.Children.Add(Rule($"Watch: slightly low/high, {service:0}–{ampMin:0}° or above {ampMax:0}°", Warn));
        panel.Children.Add(Rule($"Alert: low service range, average below {service:0}°", Bad));
        return panel;
    }

    private static InfoTabRegistration CreateBeatErrorDiagRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        var tracePlot = new AvaPlot();

        Border alertBanner = CreateAlertBanner(out TextBlock alertText);


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

        grid.Children.Add(CreatePinnedResetViewButton("Reset the trace view to its configured limits", row: 2, renderer.ResetView));

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
        grid.Children.Add(CreatePinnedResetViewButton(
            "Re-enable live windowing on all four lanes", row: 1, renderer.ResetView));
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

        grid.Children.Add(CreatePinnedResetViewButton("Re-enable live auto-scaling on all three graphs", row: 0, renderer.ResetView));

        var consumer = new LongTermPerfFrameConsumer(renderer);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    private static InfoTabRegistration CreateTestPositionsRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        // Positions combines the NIHS 95-10 / ISO 3158 selection buttons with
        // the per-position measurement table. The left button strip writes the
        // shared SelectedPositionIndex view-model property; MainWindow forwards
        // the change to the running analysis worker and the status-bar "POS …"
        // indicator updates from the same property. Both renderers read the
        // cumulative metrics-history snapshot, so one tab shows what Core is
        // tagging and the measured values for those positions together.
        IReadOnlyList<WatchPosition> positions = WatchPositions.All;
        var buttons = new Button[positions.Count];
        var buttonGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*"),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("*", positions.Count))),
            Margin = new Thickness(8, 8, 2, 8),
            MinWidth = 76,
            MaxWidth = 92,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        for (int i = 0; i < positions.Count; i++)
        {
            WatchPosition position = positions[i];
            var shortText = new TextBlock
            {
                Text = position.ShortName(),
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };

            var button = new Button
            {
                Content = shortText,
                Classes = { "PositionButton" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(2),
                Padding = new Thickness(4, 3),
            };
            ToolTip.SetTip(button, $"Tag new measurements as {position.ShortName()} - {position.LongName()}");
            Grid.SetRow(button, i);
            buttons[i] = button;
            buttonGrid.Children.Add(button);
        }

        var initialPosition = (WatchPosition)(context.ViewModel?.SelectedPositionIndex ?? 0);
        var positionRenderer = new TestPositionsRenderer(buttons, initialPosition);

        for (int i = 0; i < buttons.Length; i++)
        {
            var position = (WatchPosition)i;
            buttons[i].Click += (_, _) =>
            {
                if (context.ViewModel is { } viewModel)
                {
                    viewModel.SelectedPositionIndex = (int)position;
                }

                positionRenderer.RequestPosition(position);
            };
        }

        // Sequence results table (POS | RATE | AMP | BEAT ERR | BEATS, one row
        // per measured position, the active position's row highlighted) above
        // the X / D / vertical-vs-horizontal summary block; the accent banner
        // reports the balance-wheel unbalance hint. The renderer fills the
        // table from the cumulative snapshot's PositionSummary list.
        Border alertBanner = CreateAlertBanner(out TextBlock alertText);

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
            ColumnDefinitions = new ColumnDefinitions("96,*"),
            RowDefinitions = new RowDefinitions("*"),
        };

        var sequenceGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(4, 4, 8, 4),
        };
        Grid.SetRow(alertBanner, 0);
        Grid.SetRow(tableGrid, 1);
        Grid.SetRow(summaryText, 2);
        Grid.SetRow(explanationText, 3);
        sequenceGrid.Children.Add(alertBanner);
        sequenceGrid.Children.Add(tableGrid);
        sequenceGrid.Children.Add(summaryText);
        sequenceGrid.Children.Add(explanationText);

        if (CreateWaitingOverlay(context.ViewModel) is { } overlay)
        {
            Grid.SetRow(overlay, 1);
            sequenceGrid.Children.Add(overlay);
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
        sequenceGrid.Children.Add(resetButton);

        Grid.SetColumn(buttonGrid, 0);
        Grid.SetColumn(sequenceGrid, 1);
        grid.Children.Add(buttonGrid);
        grid.Children.Add(sequenceGrid);

        var sequenceRenderer = new MultiPositionSeqRenderer(tableGrid, alertBanner, alertText, summaryText);
        var consumer = new TestPositionsFrameConsumer(positionRenderer, sequenceRenderer);
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

    private static InfoTabRegistration CreateEscapementAnalyzerRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        // One large plot of the latest beat's envelope with the A / C marker
        // lines and millisecond labels, above the numeric repeatability panel:
        // label/value cells (the BeatErrorDiag pattern) for the current A→C
        // readings per reference, the onset-vs-peak delta, the windowed
        // mean±sigma of both references and the more-repeatable verdict.
        var markerPlot = new AvaPlot();

        var valueTexts = new TextBlock[EscapementReadout.Labels.Length];
        var readoutGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(8, 4, 8, 2),
        };
        for (int i = 0; i < EscapementReadout.Labels.Length; i++)
        {
            var label = new TextBlock
            {
                Text = EscapementReadout.Labels[i],
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
            Grid.SetRow(cell, i / 3);
            Grid.SetColumn(cell, i % 3);
            readoutGrid.Children.Add(cell);
        }

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
        };
        Grid.SetRow(markerPlot, 0);
        Grid.SetRow(readoutGrid, 1);
        grid.Children.Add(markerPlot);
        grid.Children.Add(readoutGrid);

        if (CreateWaitingOverlay(context.ViewModel) is { } overlay)
        {
            Grid.SetRow(overlay, 0);
            grid.Children.Add(overlay);
        }

        var renderer = new EscapementAnalyzerRenderer(markerPlot, valueTexts, context.TextFontFamily);
        var consumer = new EscapementAnalyzerFrameConsumer(renderer);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    private static InfoTabRegistration CreateWaveformCompareRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        // Header numeric line (rate / beat error / BPH) above one plot that
        // stacks the recent beats in A-aligned, peak-normalized lanes, and a
        // one-line legend for the guide markers below.
        var headerText = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(8, 4, 8, 2),
        };
        var lanePlot = new AvaPlot();
        var explanationText = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.65,
            Margin = new Thickness(8, 0, 8, 3),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = "Each lane is one recent beat (oldest at the bottom), normalized to its own " +
                   "peak and aligned at the A event (x = 0). Green guide = A · red guide = mean " +
                   "C peak of the shown beats; beats whose C strays from the guide reveal " +
                   "spacing inconsistency.",
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };
        Grid.SetRow(headerText, 0);
        Grid.SetRow(lanePlot, 1);
        Grid.SetRow(explanationText, 2);
        grid.Children.Add(headerText);
        grid.Children.Add(lanePlot);
        grid.Children.Add(explanationText);

        if (CreateWaitingOverlay(context.ViewModel) is { } overlay)
        {
            Grid.SetRow(overlay, 1);
            grid.Children.Add(overlay);
        }

        var renderer = new WaveformCompareRenderer(lanePlot, headerText, context.TextFontFamily);
        var consumer = new WaveformCompareFrameConsumer(renderer);
        return new InfoTabRegistration(definition, CreateTabItem(definition, grid), consumer);
    }

    private static InfoTabRegistration CreateSpectrogramRegistration(
        InfoTabDefinition definition,
        InfoTabFactoryContext context)
    {
        // Core-built STFT image (x = time, y = frequency, color = dB intensity)
        // with the frequency-axis labels on the left and the dB color legend
        // below. The spectrogram shows raw signal energy before beat sync, so no
        // waiting overlay is added (the Multi-Filter Scope reasoning).
        _ = context;
        var image = new Image
        {
            Stretch = Stretch.Fill,
        };

        TextBlock Label(string text) => new()
        {
            Text = text,
            FontSize = 11,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Display band markers for the projector's 0..12 kHz rows, low at the bottom.
        var axisGrid = new Grid
        {
            Margin = new Thickness(8, 2, 4, 2),
        };
        TextBlock AxisLabel(string text, VerticalAlignment alignment)
        {
            TextBlock label = Label(text);
            label.HorizontalAlignment = HorizontalAlignment.Right;
            label.VerticalAlignment = alignment;
            return label;
        }
        axisGrid.Children.Add(AxisLabel("12 kHz", VerticalAlignment.Top));
        axisGrid.Children.Add(AxisLabel("6 kHz", VerticalAlignment.Center));
        axisGrid.Children.Add(AxisLabel("0 Hz", VerticalAlignment.Bottom));

        var legendImage = new Image
        {
            Stretch = Stretch.Fill,
            Width = 160,
            Height = 10,
        };
        var legendRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(8, 4),
        };
        legendRow.Children.Add(Label("-80 dB"));
        legendRow.Children.Add(legendImage);
        legendRow.Children.Add(Label("0 dB"));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("*,Auto"),
        };
        Grid.SetRow(axisGrid, 0);
        Grid.SetColumn(axisGrid, 0);
        Grid.SetRow(image, 0);
        Grid.SetColumn(image, 1);
        Grid.SetRow(legendRow, 1);
        Grid.SetColumn(legendRow, 1);
        grid.Children.Add(axisGrid);
        grid.Children.Add(image);
        grid.Children.Add(legendRow);

        var renderer = new SpectrogramRenderer(image, legendImage);
        var consumer = new SpectrogramFrameConsumer(renderer);
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
