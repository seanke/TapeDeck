namespace TapeDeck;

/// <summary>
/// TapeDeck command-line entry point.
/// </summary>
public static class Program
{
    /// <summary>
    /// Runs the TapeDeck CLI.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return (int)await RunAsync(args).ConfigureAwait(false);
        }
        catch (AudioDeviceException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return (int)TapeDeckExitCode.AudioDeviceError;
        }
        catch (FileOutputException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return (int)TapeDeckExitCode.FileOutputError;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Recording failed unexpectedly: {ex.Message}");
            return (int)TapeDeckExitCode.RecordingFailed;
        }
    }

    private static async Task<TapeDeckExitCode> RunAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintRootHelp(Console.Out);
            return TapeDeckExitCode.Success;
        }

        var command = args[0];
        var commandArgs = args.Skip(1).ToArray();
        if (string.Equals(command, "devices", StringComparison.OrdinalIgnoreCase))
        {
            if (commandArgs.Length > 0 && IsHelp(commandArgs[0]))
            {
                PrintDevicesHelp(Console.Out);
                return TapeDeckExitCode.Success;
            }

            if (commandArgs.Length > 0)
            {
                Console.Error.WriteLine($"Unknown option '{commandArgs[0]}'.");
                PrintDevicesHelp(Console.Error);
                return TapeDeckExitCode.InvalidArguments;
            }

            new DeviceLister().PrintDevices(Console.Out);
            return TapeDeckExitCode.Success;
        }

        if (string.Equals(command, "record", StringComparison.OrdinalIgnoreCase))
        {
            if (commandArgs.Length > 0 && IsHelp(commandArgs[0]))
            {
                PrintRecordHelp(Console.Out);
                return TapeDeckExitCode.Success;
            }

            if (!CommandLineParser.TryParseRecordOptions(commandArgs, out var options, out var error))
            {
                Console.Error.WriteLine(error);
                PrintRecordHelp(Console.Error);
                return TapeDeckExitCode.InvalidArguments;
            }

            using var cancellationTokenSource = new CancellationTokenSource();
            var stopGate = new StopRequestGate();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                if (stopGate.TryRequestStop())
                {
                    cancellationTokenSource.Cancel();
                }
            };

            Console.CancelKeyPress += cancelHandler;
            try
            {
                var recorder = new DualSourceRecorder(new DeviceLister(), new MixdownService(), Console.Out, Console.Error);
                var result = await recorder.RecordAsync(options, cancellationTokenSource.Token).ConfigureAwait(false);
                return result.ExitCode;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintRootHelp(Console.Error);
        return TapeDeckExitCode.InvalidArguments;
    }

    private static bool IsHelp(string value)
    {
        return string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "/?", StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintRootHelp(TextWriter writer)
    {
        writer.WriteLine("TapeDeck");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  TapeDeck record [options]");
        writer.WriteLine("  TapeDeck devices");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        writer.WriteLine("  record   Record system playback and/or microphone audio to M4A, WAV, or TXT transcript output.");
        writer.WriteLine("  devices  List playback/render and microphone/capture devices.");
    }

    private static void PrintRecordHelp(TextWriter writer)
    {
        writer.WriteLine("TapeDeck record");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  TapeDeck record [options]");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --out <path>                Final output path.");
        writer.WriteLine("  --format <m4a|wav|txt>      Final output format. Aliases: mp4a, wave, text. Default: m4a.");
        writer.WriteLine("  --bitrate <bps>             M4A/AAC bitrate in bits per second. Default: 128000.");
        writer.WriteLine("  --no-mic                    Disable microphone capture.");
        writer.WriteLine("  --no-system                 Disable system playback capture.");
        writer.WriteLine("  --system-device <selector>  Playback/render device ID or exact friendly name.");
        writer.WriteLine("  --mic-device <selector>     Microphone/capture device ID or exact friendly name.");
        writer.WriteLine("  --duration <hh:mm:ss>       Maximum recording duration.");
        writer.WriteLine("  --split-minutes <minutes>   Split final output into part files.");
        writer.WriteLine("  --keep-stems                Keep finalized source stem WAV files.");
        writer.WriteLine("  --overwrite                 Overwrite existing final output paths.");
        writer.WriteLine("  --system-gain <number>      Linear gain for system audio. Default: 1.0.");
        writer.WriteLine("  --mic-gain <number>         Linear gain for microphone audio. Default: 1.0.");
        writer.WriteLine("  --transcript                Also write a local Windows speech-recognition TXT sidecar.");
        writer.WriteLine("  --transcript-out <path>     Transcript output path. Defaults beside the audio file.");
        writer.WriteLine("  --transcript-culture <name> Installed recognizer culture such as en-US.");
    }

    private static void PrintDevicesHelp(TextWriter writer)
    {
        writer.WriteLine("TapeDeck devices");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  TapeDeck devices");
        writer.WriteLine();
        writer.WriteLine("Lists playback/render and microphone/capture devices with stable IDs, friendly names, state, and default status.");
    }
}
