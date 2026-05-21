using NAudio.CoreAudioApi;

namespace TapeDeck;

/// <summary>
/// Lists and resolves Windows render and capture endpoints.
/// </summary>
public sealed class DeviceLister
{
    /// <summary>
    /// Lists all playback/render devices.
    /// </summary>
    public IReadOnlyList<AudioDeviceInfo> GetPlaybackDevices()
    {
        return GetDevices(DataFlow.Render);
    }

    /// <summary>
    /// Lists all microphone/capture devices.
    /// </summary>
    public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices()
    {
        return GetDevices(DataFlow.Capture);
    }

    /// <summary>
    /// Prints the device list in the CLI format.
    /// </summary>
    public void PrintDevices(TextWriter writer)
    {
        writer.WriteLine("Playback/render devices");
        PrintDeviceSection(writer, GetPlaybackDevices());
        writer.WriteLine();
        writer.WriteLine("Microphone/capture devices");
        PrintDeviceSection(writer, GetCaptureDevices());
    }

    /// <summary>
    /// Gets the default or selected device for a recording source.
    /// </summary>
    public MMDevice ResolveDevice(DataFlow flow, string? selector)
    {
        using var enumerator = new MMDeviceEnumerator();

        if (string.IsNullOrWhiteSpace(selector))
        {
            try
            {
                return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            }
            catch (Exception ex)
            {
                var type = flow == DataFlow.Render ? "playback/render" : "microphone/capture";
                throw new AudioDeviceException($"No default {type} device exists.", ex);
            }
        }

        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var match = devices.FirstOrDefault(device =>
            string.Equals(device.ID, selector, StringComparison.OrdinalIgnoreCase)
            || string.Equals(device.FriendlyName, selector, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            var type = flow == DataFlow.Render ? "playback/render" : "microphone/capture";
            throw new AudioDeviceException($"No {type} device matched '{selector}'. Run 'TapeDeck devices' to list devices.");
        }

        if (match.State != DeviceState.Active)
        {
            throw new AudioDeviceException($"The selected device is not active: {match.FriendlyName} ({match.State}).");
        }

        return match;
    }

    private static IReadOnlyList<AudioDeviceInfo> GetDevices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            defaultId = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID;
        }
        catch
        {
            // A machine can have no default endpoint for a given flow.
        }

        MMDeviceCollection devices;
        try
        {
            devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.All);
        }
        catch
        {
            return [];
        }

        return devices
            .Select(device => new AudioDeviceInfo(
                SafeRead(() => device.ID, "<unavailable>"),
                SafeRead(() => device.FriendlyName, "<unavailable>"),
                SafeRead(() => device.State.ToString(), "<unavailable>"),
                string.Equals(SafeRead(() => device.ID, string.Empty), defaultId, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string SafeRead(Func<string> read, string fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }

    private static void PrintDeviceSection(TextWriter writer, IReadOnlyList<AudioDeviceInfo> devices)
    {
        if (devices.Count == 0)
        {
            writer.WriteLine("  none");
            return;
        }

        foreach (var device in devices)
        {
            writer.WriteLine($"  ID: {device.StableId}");
            writer.WriteLine($"  Name: {device.FriendlyName}");
            writer.WriteLine($"  State: {device.State}");
            writer.WriteLine($"  Default: {(device.IsDefault ? "yes" : "no")}");
            writer.WriteLine();
        }
    }
}

/// <summary>
/// Immutable display information for one Windows audio endpoint.
/// </summary>
public sealed record AudioDeviceInfo(string StableId, string FriendlyName, string State, bool IsDefault);

/// <summary>
/// Represents an audio endpoint error that should map to exit code 2.
/// </summary>
public sealed class AudioDeviceException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AudioDeviceException"/> class.
    /// </summary>
    public AudioDeviceException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioDeviceException"/> class with an inner exception.
    /// </summary>
    public AudioDeviceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
