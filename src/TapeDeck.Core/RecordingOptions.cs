using System.Globalization;

namespace TapeDeck;

/// <summary>
/// Captures the command-line options used for one recording session.
/// </summary>
public sealed record RecordingOptions
{
    /// <summary>
    /// Gets the final container and codec format.
    /// </summary>
    public OutputFormat Format { get; init; } = OutputFormat.M4A;

    /// <summary>
    /// Gets the optional final output path supplied by the caller.
    /// </summary>
    public string? OutputPath { get; init; }

    /// <summary>
    /// Gets a value indicating whether the Windows render endpoint should be recorded.
    /// </summary>
    public bool RecordSystem { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the Windows capture endpoint should be recorded.
    /// </summary>
    public bool RecordMicrophone { get; init; } = true;

    /// <summary>
    /// Gets the optional system device ID or exact friendly name.
    /// </summary>
    public string? SystemDeviceSelector { get; init; }

    /// <summary>
    /// Gets the optional microphone device ID or exact friendly name.
    /// </summary>
    public string? MicrophoneDeviceSelector { get; init; }

    /// <summary>
    /// Gets the optional maximum recording duration.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Gets the optional final WAV split interval in minutes.
    /// </summary>
    public int? SplitMinutes { get; init; }

    /// <summary>
    /// Gets a value indicating whether finalized source stems should be retained.
    /// </summary>
    public bool KeepStems { get; init; }

    /// <summary>
    /// Gets a value indicating whether existing final output paths may be overwritten.
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// Gets the linear gain applied to the system-audio source during mixdown.
    /// </summary>
    public double SystemGain { get; init; } = 1.0;

    /// <summary>
    /// Gets the linear gain applied to the microphone source during mixdown.
    /// </summary>
    public double MicrophoneGain { get; init; } = 1.0;

    /// <summary>
    /// Gets the target bitrate used for M4A/AAC output.
    /// </summary>
    public int AudioBitrate { get; init; } = 128_000;

    /// <summary>
    /// Gets a value indicating whether a local text transcript should be written after recording.
    /// </summary>
    public bool Transcribe { get; init; }

    /// <summary>
    /// Gets the optional transcript output path supplied by the caller.
    /// </summary>
    public string? TranscriptOutputPath { get; init; }

    /// <summary>
    /// Gets the optional installed Windows speech recognizer culture name.
    /// </summary>
    public string? TranscriptCultureName { get; init; }
}

/// <summary>
/// Final output formats supported by TapeDeck.
/// </summary>
public enum OutputFormat
{
    /// <summary>48 kHz stereo 16-bit PCM RIFF WAV.</summary>
    Wav,

    /// <summary>AAC audio in an M4A/MP4 container.</summary>
    M4A
}

/// <summary>
/// Process exit codes used by the command-line entry point.
/// </summary>
public enum TapeDeckExitCode
{
    /// <summary>The command completed successfully.</summary>
    Success = 0,

    /// <summary>The command line was invalid.</summary>
    InvalidArguments = 1,

    /// <summary>An audio endpoint could not be found or opened.</summary>
    AudioDeviceError = 2,

    /// <summary>An output file path could not be created or written.</summary>
    FileOutputError = 3,

    /// <summary>Recording stopped because capture failed unexpectedly.</summary>
    RecordingFailed = 4,

    /// <summary>Final WAV mixing failed after source stems were preserved.</summary>
    FinalMixFailed = 5,

    /// <summary>Local transcript generation failed after the final audio was saved.</summary>
    TranscriptionFailed = 6
}

/// <summary>
/// Parses TapeDeck command-line arguments without pulling in a CLI framework.
/// </summary>
public static class CommandLineParser
{
    /// <summary>
    /// Attempts to parse the record command options.
    /// </summary>
    public static bool TryParseRecordOptions(IReadOnlyList<string> args, out RecordingOptions options, out string error)
    {
        options = new RecordingOptions();
        error = string.Empty;
        var formatWasSpecified = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--out":
                    if (!TryReadValue(args, ref i, arg, out var output, out error))
                    {
                        return false;
                    }

                    options = options with { OutputPath = output };
                    break;

                case "--format":
                    if (!TryReadValue(args, ref i, arg, out var formatValue, out error))
                    {
                        return false;
                    }

                    if (!TryParseOutputFormat(formatValue, out var format))
                    {
                        error = $"Invalid --format value '{formatValue}'. Use m4a or wav.";
                        return false;
                    }

                    options = options with { Format = format };
                    formatWasSpecified = true;
                    break;

                case "--no-mic":
                    options = options with { RecordMicrophone = false };
                    break;

                case "--no-system":
                    options = options with { RecordSystem = false };
                    break;

                case "--system-device":
                    if (!TryReadValue(args, ref i, arg, out var systemDevice, out error))
                    {
                        return false;
                    }

                    options = options with { SystemDeviceSelector = systemDevice };
                    break;

                case "--mic-device":
                    if (!TryReadValue(args, ref i, arg, out var micDevice, out error))
                    {
                        return false;
                    }

                    options = options with { MicrophoneDeviceSelector = micDevice };
                    break;

                case "--duration":
                    if (!TryReadValue(args, ref i, arg, out var durationValue, out error))
                    {
                        return false;
                    }

                    if (!TryParseDuration(durationValue, out var duration))
                    {
                        error = $"Invalid --duration value '{durationValue}'. Use a TimeSpan such as 01:30:00.";
                        return false;
                    }

                    options = options with { Duration = duration };
                    break;

                case "--split-minutes":
                    if (!TryReadValue(args, ref i, arg, out var splitValue, out error))
                    {
                        return false;
                    }

                    if (!int.TryParse(splitValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var splitMinutes) || splitMinutes <= 0)
                    {
                        error = $"Invalid --split-minutes value '{splitValue}'. Use a positive whole number.";
                        return false;
                    }

                    options = options with { SplitMinutes = splitMinutes };
                    break;

                case "--keep-stems":
                    options = options with { KeepStems = true };
                    break;

                case "--overwrite":
                    options = options with { Overwrite = true };
                    break;

                case "--system-gain":
                    if (!TryReadValue(args, ref i, arg, out var systemGainValue, out error))
                    {
                        return false;
                    }

                    if (!TryParseGain(systemGainValue, out var systemGain))
                    {
                        error = $"Invalid --system-gain value '{systemGainValue}'. Use a non-negative number.";
                        return false;
                    }

                    options = options with { SystemGain = systemGain };
                    break;

                case "--mic-gain":
                    if (!TryReadValue(args, ref i, arg, out var micGainValue, out error))
                    {
                        return false;
                    }

                    if (!TryParseGain(micGainValue, out var micGain))
                    {
                        error = $"Invalid --mic-gain value '{micGainValue}'. Use a non-negative number.";
                        return false;
                    }

                    options = options with { MicrophoneGain = micGain };
                    break;

                case "--bitrate":
                    if (!TryReadValue(args, ref i, arg, out var bitrateValue, out error))
                    {
                        return false;
                    }

                    if (!int.TryParse(bitrateValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitrate) || bitrate <= 0)
                    {
                        error = $"Invalid --bitrate value '{bitrateValue}'. Use a positive bitrate in bits per second.";
                        return false;
                    }

                    options = options with { AudioBitrate = bitrate };
                    break;

                case "--transcript":
                    options = options with { Transcribe = true };
                    break;

                case "--transcript-out":
                    if (!TryReadValue(args, ref i, arg, out var transcriptOutput, out error))
                    {
                        return false;
                    }

                    options = options with { Transcribe = true, TranscriptOutputPath = transcriptOutput };
                    break;

                case "--transcript-culture":
                    if (!TryReadValue(args, ref i, arg, out var transcriptCulture, out error))
                    {
                        return false;
                    }

                    if (!TryParseCultureName(transcriptCulture))
                    {
                        error = $"Invalid --transcript-culture value '{transcriptCulture}'. Use a culture name such as en-US.";
                        return false;
                    }

                    options = options with { Transcribe = true, TranscriptCultureName = transcriptCulture };
                    break;

                default:
                    error = $"Unknown option '{arg}'.";
                    return false;
            }
        }

        if (!options.RecordSystem && !options.RecordMicrophone)
        {
            error = "At least one source must be enabled.";
            return false;
        }

        if (options.Transcribe && options.SplitMinutes is not null)
        {
            error = "--transcript cannot currently be combined with --split-minutes.";
            return false;
        }

        if (!formatWasSpecified && TryInferOutputFormat(options.OutputPath, out var inferredFormat))
        {
            options = options with { Format = inferredFormat };
        }

        if (formatWasSpecified && !OutputPathMatchesFormat(options.OutputPath, options.Format))
        {
            error = $"The --out extension does not match --format {GetFormatName(options.Format)}.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses a duration in the standard TimeSpan format used by the CLI.
    /// </summary>
    public static bool TryParseDuration(string value, out TimeSpan duration)
    {
        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration)
            && duration > TimeSpan.Zero;
    }

    /// <summary>
    /// Parses a non-negative linear source gain.
    /// </summary>
    public static bool TryParseGain(string value, out double gain)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out gain)
            && double.IsFinite(gain)
            && gain >= 0;
    }

    /// <summary>
    /// Returns true when the value is a valid culture name.
    /// </summary>
    public static bool TryParseCultureName(string value)
    {
        try
        {
            _ = CultureInfo.GetCultureInfo(value);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses the final output format. Common aliases are accepted for command-line convenience.
    /// </summary>
    public static bool TryParseOutputFormat(string value, out OutputFormat format)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "wav":
            case "wave":
                format = OutputFormat.Wav;
                return true;
            case "m4a":
            case "mp4a":
            case "aac":
                format = OutputFormat.M4A;
                return true;
            default:
                format = default;
                return false;
        }
    }

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, string option, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;

        if (index + 1 >= args.Count)
        {
            error = $"Missing value for {option}.";
            return false;
        }

        value = args[++index];
        if (value.StartsWith("--", StringComparison.Ordinal))
        {
            error = $"Missing value for {option}.";
            return false;
        }

        return true;
    }

    private static bool TryInferOutputFormat(string? outputPath, out OutputFormat format)
    {
        format = default;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return false;
        }

        var extension = Path.GetExtension(outputPath);
        if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
        {
            format = OutputFormat.Wav;
            return true;
        }

        if (string.Equals(extension, ".m4a", StringComparison.OrdinalIgnoreCase))
        {
            format = OutputFormat.M4A;
            return true;
        }

        return false;
    }

    private static bool OutputPathMatchesFormat(string? outputPath, OutputFormat format)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return true;
        }

        var extension = Path.GetExtension(outputPath);
        if (string.IsNullOrEmpty(extension))
        {
            return true;
        }

        return string.Equals(extension, GetExtension(format), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetExtension(OutputFormat format)
    {
        return format == OutputFormat.Wav ? ".wav" : ".m4a";
    }

    private static string GetFormatName(OutputFormat format)
    {
        return format == OutputFormat.Wav ? "wav" : "m4a";
    }
}

/// <summary>
/// Coordinates repeated stop requests so cleanup is started only once.
/// </summary>
public sealed class StopRequestGate
{
    private int requested;

    /// <summary>
    /// Requests a stop and returns true only for the first caller.
    /// </summary>
    public bool TryRequestStop()
    {
        return Interlocked.Exchange(ref requested, 1) == 0;
    }
}
