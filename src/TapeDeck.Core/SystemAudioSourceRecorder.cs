using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TapeDeck;

/// <summary>
/// Records the selected Windows render endpoint using WASAPI loopback capture.
/// </summary>
public sealed class SystemAudioSourceRecorder : SourceRecorder
{
    private readonly MMDevice device;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemAudioSourceRecorder"/> class.
    /// </summary>
    public SystemAudioSourceRecorder(MMDevice device, string partialPath, string stemPath)
        : base(device.FriendlyName, partialPath, stemPath)
    {
        this.device = device;
    }

    /// <inheritdoc />
    protected override IWaveIn CreateCapture()
    {
        return new WasapiLoopbackCapture(device);
    }
}
