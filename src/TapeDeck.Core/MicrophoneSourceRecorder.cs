using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TapeDeck;

/// <summary>
/// Records the selected Windows capture endpoint using WASAPI capture.
/// </summary>
public sealed class MicrophoneSourceRecorder : SourceRecorder
{
    private readonly MMDevice device;

    /// <summary>
    /// Initializes a new instance of the <see cref="MicrophoneSourceRecorder"/> class.
    /// </summary>
    public MicrophoneSourceRecorder(MMDevice device, string partialPath, string stemPath)
        : base(device.FriendlyName, partialPath, stemPath)
    {
        this.device = device;
    }

    /// <inheritdoc />
    protected override IWaveIn CreateCapture()
    {
        return new WasapiCapture(device);
    }
}
