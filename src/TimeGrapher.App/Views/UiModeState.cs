using Avalonia.Controls;

namespace TimeGrapher.App.Views;

internal static class UiModeState
{
    public static void ApplyStarting(Button startButton, Button stopButton, IReadOnlyList<Control> lockedControls)
    {
        SetControlsEnabled(lockedControls, false);
        startButton.IsEnabled = false;
        stopButton.IsEnabled = false;
    }

    public static void ApplyRunning(Button startButton, Button stopButton, IReadOnlyList<Control> lockedControls)
    {
        SetControlsEnabled(lockedControls, false);
        startButton.IsEnabled = false;
        stopButton.IsEnabled = true;
    }

    public static void ApplyStopping(Button startButton, Button stopButton, IReadOnlyList<Control> lockedControls)
    {
        SetControlsEnabled(lockedControls, false);
        startButton.IsEnabled = false;
        stopButton.IsEnabled = false;
    }

    public static void ApplyStopped(
        Button startButton,
        Button stopButton,
        IReadOnlyList<Control> lockedControls,
        ComboBox sampleRates,
        bool enableSampleRates)
    {
        SetControlsEnabled(lockedControls, true);
        sampleRates.IsEnabled = enableSampleRates;
        startButton.IsEnabled = true;
        stopButton.IsEnabled = false;
    }

    private static void SetControlsEnabled(IReadOnlyList<Control> controls, bool enabled)
    {
        foreach (Control control in controls)
        {
            control.IsEnabled = enabled;
        }
    }
}
