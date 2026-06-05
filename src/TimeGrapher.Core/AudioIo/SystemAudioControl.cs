using System;
using System.Collections.Generic;
using System.Globalization;
using NAudio.CoreAudioApi;

namespace TimeGrapher.Core.AudioIo;

/// <summary>
/// System (endpoint) audio control. Port of WindowsAudio.cpp's WindowsSetSoundParameters /
/// WindowsListSoundCardsAndElements, using NAudio's CoreAudioApi
/// (<see cref="MMDeviceEnumerator"/> / <see cref="AudioEndpointVolume"/>) instead of raw COM.
///
/// Best-effort, like the original: any failure is swallowed silently. The original also
/// walked device-topology AGC controls to disable AGC; NAudio does not expose
/// IAudioAutoGainControl, so only the endpoint master volume is set here.
/// </summary>
public static class SystemAudioControl
{
    /// <summary>
    /// Finds a capture endpoint matching both name filters (mic name + device/adapter
    /// disambiguator) and sets its master volume to <paramref name="volumePercent"/>%.
    /// Mirrors WindowsSetSoundParameters' two-name matching. Failures are ignored.
    /// </summary>
    public static void SetSoundParameters(string endpointName, string micName, int volumePercent)
    {
        try
        {
            // NOTE: original arg order is WindowsSetSoundParameters(endpoint_name, mic_name, ...)
            // and internally micName=mic_name (first filter), deviceName=endpoint_name (second filter).
            string micNameFilter = micName;
            string deviceNameFilter = endpointName;

            using var enumerator = new MMDeviceEnumerator();
            MMDevice? matched = FindEndpointByTwoNames(enumerator, DataFlow.Capture, micNameFilter, deviceNameFilter);
            if (matched == null)
                return; // not found or ambiguous -> best-effort, ignore

            using (matched)
            {
                SetEndpointVolumePercent(matched, volumePercent);
            }
        }
        catch
        {
            // best-effort: swallow
        }
    }

    /// <summary>Diagnostic dump of active capture endpoints (WindowsListSoundCardsAndElements).</summary>
    public static void ListSoundCardsAndElements()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            int count = collection.Count;

            Console.Error.WriteLine();
            Console.Error.WriteLine("==== Capture/Microphone devices: " + count + " active ====");
            Console.Error.WriteLine();

            for (int i = 0; i < count; ++i)
            {
                MMDevice device = collection[i];
                using (device)
                {
                    Console.Error.WriteLine("[" + i + "]");
                    PrintAudioDeviceInfo(device);

                    float percent;
                    if (TryGetEndpointVolumePercent(device, out percent))
                    {
                        Console.Error.WriteLine("  Endpoint volume        : " +
                            percent.ToString("F1", CultureInfo.InvariantCulture) + "%");
                    }
                    Console.Error.WriteLine();
                }
            }
        }
        catch
        {
            // best-effort: swallow
        }
    }

    // ── Matching helpers (ported from WindowsAudio.cpp) ────────────────────────

    // NAudio device identity strings used in place of the COM property reads:
    //   FriendlyName       -> endpointFriendlyName ("Microphone (USB PnP Sound Device)")
    //   DeviceFriendlyName -> deviceName / adapter description ("USB PnP Sound Device")
    //   ID                 -> endpointId

    private static bool ContainsIgnoreCase(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle))
            return false;
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool EqualsIgnoreCase(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static int MatchScore(string value, string filter, int exactScore, int containsScore)
    {
        if (string.IsNullOrEmpty(filter) || string.IsNullOrEmpty(value))
            return 0;

        // All filters are substring filters; exact matches just score higher so the
        // most specific match wins when several share a substring.
        if (!ContainsIgnoreCase(value, filter))
            return 0;
        if (EqualsIgnoreCase(value, filter))
            return exactScore;
        return containsScore;
    }

    private static bool TwoNameMatchesDevice(string endpointFriendlyName, string deviceName, string endpointId,
                                             string micNameFilter, string deviceNameFilter)
    {
        bool micFilterMatches =
            ContainsIgnoreCase(endpointFriendlyName, micNameFilter) ||
            ContainsIgnoreCase(deviceName, micNameFilter);

        bool deviceFilterMatches =
            ContainsIgnoreCase(deviceName, deviceNameFilter) ||
            ContainsIgnoreCase(endpointFriendlyName, deviceNameFilter) ||
            ContainsIgnoreCase(endpointId, deviceNameFilter);

        return micFilterMatches && deviceFilterMatches;
    }

    private static int TwoNameMatchScore(string endpointFriendlyName, string deviceName, string endpointId,
                                         string micNameFilter, string deviceNameFilter)
    {
        int micScore = 0;
        micScore = Math.Max(micScore, MatchScore(endpointFriendlyName, micNameFilter, 500, 50));
        micScore = Math.Max(micScore, MatchScore(deviceName, micNameFilter, 400, 40));

        int deviceScore = 0;
        // Original scored both deviceName and adapterFriendlyName at 300/30; NAudio has no
        // distinct adapter-name field, so the DeviceFriendlyName covers that 300/30 slot once.
        deviceScore = Math.Max(deviceScore, MatchScore(deviceName, deviceNameFilter, 300, 30));
        deviceScore = Math.Max(deviceScore, MatchScore(endpointFriendlyName, deviceNameFilter, 100, 10));
        deviceScore = Math.Max(deviceScore, MatchScore(endpointId, deviceNameFilter, 1000, 10));

        return micScore + deviceScore;
    }

    private sealed class Candidate
    {
        public MMDevice Device = null!;
        public string EndpointFriendlyName = "";
        public string DeviceName = "";
        public string EndpointId = "";
        public int Score;
    }

    private static MMDevice? FindEndpointByTwoNames(MMDeviceEnumerator enumerator, DataFlow flow,
                                                    string micNameFilter, string deviceNameFilter)
    {
        var collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        int count = collection.Count;

        // Snapshot identity strings for every active endpoint once.
        var all = new List<Candidate>(count);
        for (int i = 0; i < count; ++i)
        {
            MMDevice device = collection[i];
            all.Add(new Candidate
            {
                Device = device,
                EndpointFriendlyName = SafeFriendlyName(device),
                DeviceName = SafeDeviceFriendlyName(device),
                EndpointId = SafeId(device)
            });
        }

        MMDevice? selected = null;

        // FIRST PASS: exact endpoint-friendly-name match for micNameFilter, with the
        // second filter matching the same endpoint as a substring.
        var exactMatches = new List<Candidate>();
        foreach (var c in all)
        {
            if (EqualsIgnoreCase(c.EndpointFriendlyName, micNameFilter))
            {
                bool deviceFilterMatches =
                    ContainsIgnoreCase(c.DeviceName, deviceNameFilter) ||
                    ContainsIgnoreCase(c.EndpointFriendlyName, deviceNameFilter) ||
                    ContainsIgnoreCase(c.EndpointId, deviceNameFilter);

                if (deviceFilterMatches)
                {
                    c.Score = 10000 + TwoNameMatchScore(c.EndpointFriendlyName, c.DeviceName, c.EndpointId,
                                                        micNameFilter, deviceNameFilter);
                    exactMatches.Add(c);
                }
            }
        }

        if (exactMatches.Count == 1)
        {
            selected = exactMatches[0].Device;
        }
        else if (exactMatches.Count == 0)
        {
            // SECOND PASS: two-substring matching.
            var matches = new List<Candidate>();
            foreach (var c in all)
            {
                if (TwoNameMatchesDevice(c.EndpointFriendlyName, c.DeviceName, c.EndpointId,
                                         micNameFilter, deviceNameFilter))
                {
                    c.Score = TwoNameMatchScore(c.EndpointFriendlyName, c.DeviceName, c.EndpointId,
                                                micNameFilter, deviceNameFilter);
                    matches.Add(c);
                }
            }

            if (matches.Count > 0)
            {
                matches.Sort((a, b) => b.Score.CompareTo(a.Score));
                // Ambiguous if the top two tie -> best-effort: leave unselected.
                if (!(matches.Count > 1 && matches[0].Score == matches[1].Score))
                    selected = matches[0].Device;
            }
        }
        // exactMatches.Count > 1 -> ambiguous, leave unselected (best-effort).

        // Dispose every device except the selected one.
        foreach (var c in all)
        {
            if (!ReferenceEquals(c.Device, selected))
                c.Device.Dispose();
        }

        return selected;
    }

    private static void SetEndpointVolumePercent(MMDevice device, float percent)
    {
        if (percent < 0.0f) percent = 0.0f;
        if (percent > 100.0f) percent = 100.0f;
        float scalar = percent / 100.0f;
        device.AudioEndpointVolume.MasterVolumeLevelScalar = scalar;
    }

    private static bool TryGetEndpointVolumePercent(MMDevice device, out float percent)
    {
        percent = 0.0f;
        try
        {
            percent = device.AudioEndpointVolume.MasterVolumeLevelScalar * 100.0f;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void PrintAudioDeviceInfo(MMDevice device)
    {
        Console.Error.WriteLine("  Endpoint friendly name : " + SafeFriendlyName(device));
        Console.Error.WriteLine("  Device name            : " + SafeDeviceFriendlyName(device));
        Console.Error.WriteLine("  Flow                   : Capture/Microphone");
        Console.Error.WriteLine("  Endpoint ID            : " + SafeId(device));
    }

    private static string SafeFriendlyName(MMDevice d)
    {
        try { return d.FriendlyName ?? ""; } catch { return ""; }
    }

    private static string SafeDeviceFriendlyName(MMDevice d)
    {
        try { return d.DeviceFriendlyName ?? ""; } catch { return ""; }
    }

    private static string SafeId(MMDevice d)
    {
        try { return d.ID ?? ""; } catch { return ""; }
    }
}
