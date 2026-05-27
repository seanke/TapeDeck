using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TapeDeck;

/// <summary>
/// Owns both source recorders and the final mixdown workflow for one recording command.
/// </summary>
public sealed class DualSourceRecorder
{
    private readonly DeviceLister deviceLister;
    private readonly MixdownService mixdownService;
    private readonly LocalTranscriptService transcriptService;
    private readonly TextWriter output;
    private readonly TextWriter error;

    /// <summary>
    /// Initializes a new instance of the <see cref="DualSourceRecorder"/> class.
    /// </summary>
    public DualSourceRecorder(DeviceLister deviceLister, MixdownService mixdownService, TextWriter output, TextWriter error)
        : this(deviceLister, mixdownService, new LocalTranscriptService(), output, error)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DualSourceRecorder"/> class.
    /// </summary>
    public DualSourceRecorder(
        DeviceLister deviceLister,
        MixdownService mixdownService,
        LocalTranscriptService transcriptService,
        TextWriter output,
        TextWriter error)
    {
        this.deviceLister = deviceLister;
        this.mixdownService = mixdownService;
        this.transcriptService = transcriptService;
        this.output = output;
        this.error = error;
    }

    /// <summary>
    /// Runs one dual-source recording session.
    /// </summary>
    public async Task<RecordingResult> RecordAsync(RecordingOptions options, CancellationToken cancellationToken)
    {
        var fileSet = FileNameService.CreateFileSet(options, DateTimeOffset.Now);
        var recorders = new List<SourceRecorder>();
        SourceRecorder? systemRecorder = null;
        SourceRecorder? microphoneRecorder = null;
        var sizeWarningPrinted = false;

        if (options.RecordSystem)
        {
            try
            {
                var device = deviceLister.ResolveDevice(DataFlow.Render, options.SystemDeviceSelector);
                systemRecorder = new SystemAudioSourceRecorder(device, fileSet.SystemPartialPath, fileSet.SystemStemPath);
                systemRecorder.Open();
                recorders.Add(systemRecorder);
            }
            catch (Exception ex) when (ex is not AudioDeviceException and not FileOutputException)
            {
                throw new AudioDeviceException("System audio could not be opened. Check Windows playback device selection.", ex);
            }
        }

        if (options.RecordMicrophone)
        {
            try
            {
                var device = deviceLister.ResolveDevice(DataFlow.Capture, options.MicrophoneDeviceSelector);
                microphoneRecorder = new MicrophoneSourceRecorder(device, fileSet.MicrophonePartialPath, fileSet.MicrophoneStemPath);
                microphoneRecorder.Open();
                recorders.Add(microphoneRecorder);
            }
            catch (Exception ex) when (ex is not AudioDeviceException and not FileOutputException)
            {
                throw new AudioDeviceException("Microphone could not be opened. Check Windows microphone privacy settings and device selection.", ex);
            }
        }

        var firstFinalPath = options.SplitMinutes is null
            ? fileSet.FinalBasePath
            : FileNameService.GetPartPath(fileSet.FinalBasePath, 1);

        PrintStart(options, firstFinalPath, systemRecorder, microphoneRecorder);

        var stopwatch = Stopwatch.StartNew();
        Exception? runException = null;
        try
        {
            foreach (var recorder in recorders)
            {
                try
                {
                    recorder.Start(stopwatch);
                }
                catch (Exception ex) when (recorder == microphoneRecorder)
                {
                    throw new AudioDeviceException("Microphone could not be opened. Check Windows microphone privacy settings and device selection.", ex);
                }
                catch (Exception ex) when (recorder == systemRecorder)
                {
                    throw new AudioDeviceException("System audio could not be opened. Check Windows playback device selection.", ex);
                }
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                PrintStatus(stopwatch.Elapsed, systemRecorder, microphoneRecorder);

                if (recorders.Any(recorder => recorder.Failure is not null))
                {
                    break;
                }

                if (options.Duration is not null && stopwatch.Elapsed >= options.Duration.Value)
                {
                    break;
                }

                if (options.Format == OutputFormat.Wav
                    && options.SplitMinutes is null
                    && WavSizeGuard.IsFinalDurationNearLimit(stopwatch.Elapsed))
                {
                    error.WriteLine();
                    error.WriteLine("Final WAV is approaching the safe RIFF WAV size limit. Stopping before an oversized WAV is produced.");
                    sizeWarningPrinted = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C or duration cancellation falls through to the normal finalization path.
        }
        catch (Exception ex)
        {
            runException = ex;
        }
        finally
        {
            stopwatch.Stop();
        }

        output.WriteLine();
        output.WriteLine("Finalizing source recordings...");

        var stopElapsed = stopwatch.Elapsed;
        try
        {
            await Task.WhenAll(recorders.Select(recorder => recorder.StopAndFinalizeAsync(stopElapsed))).ConfigureAwait(false);
        }
        finally
        {
            foreach (var recorder in recorders)
            {
                recorder.Dispose();
            }
        }

        if (runException is not null)
        {
            if (runException is AudioDeviceException or FileOutputException)
            {
                throw runException;
            }

            error.WriteLine($"Recording failed unexpectedly: {runException.Message}");
            PrintStemPaths(recorders);
            return new RecordingResult(TapeDeckExitCode.RecordingFailed, [], stopElapsed, ErrorMessage: runException.Message);
        }

        var failedRecorder = recorders.FirstOrDefault(recorder => recorder.Failure is not null);
        if (failedRecorder is not null)
        {
            var message = $"Recording failed unexpectedly from source '{failedRecorder.DisplayName}': {failedRecorder.Failure!.Message}";
            error.WriteLine(message);
            PrintStemPaths(recorders);
            return new RecordingResult(TapeDeckExitCode.RecordingFailed, [], stopElapsed, ErrorMessage: message);
        }

        var mixdownRequest = new MixdownRequest(
            options.RecordSystem ? fileSet.SystemStemPath : null,
            options.RecordMicrophone ? fileSet.MicrophoneStemPath : null,
            fileSet.FinalBasePath,
            options.Format,
            options.SystemGain,
            options.MicrophoneGain,
            options.AudioBitrate,
            options.SplitMinutes,
            options.Overwrite);

        if (options.IsTranscriptOnly)
        {
            output.WriteLine("Transcribing TXT locally...");
            try
            {
                var textOnlyTranscript = await TranscribeAsync(options, fileSet.FinalBasePath, mixdownRequest).ConfigureAwait(false);
                if (!options.KeepStems)
                {
                    DeleteStemIfExists(fileSet.SystemStemPath);
                    DeleteStemIfExists(fileSet.MicrophoneStemPath);
                }

                output.WriteLine($"Saved: {textOnlyTranscript.Path}");
                output.WriteLine($"Transcript recognizer: {textOnlyTranscript.RecognizerName} ({textOnlyTranscript.CultureName})");
                output.WriteLine($"Duration: {stopElapsed:hh\\:mm\\:ss}");
                output.WriteLine($"Size: {ByteFormat.Format(textOnlyTranscript.SizeBytes)}");

                if (sizeWarningPrinted)
                {
                    output.WriteLine("Warning: recording stopped at the safe WAV size limit.");
                }

                return new RecordingResult(TapeDeckExitCode.Success, [], stopElapsed, textOnlyTranscript);
            }
            catch (Exception ex)
            {
                error.WriteLine($"Local transcription failed: {ex.Message}");
                PrintStemPaths(recorders);
                return new RecordingResult(TapeDeckExitCode.TranscriptionFailed, [], stopElapsed, ErrorMessage: ex.Message);
            }
        }

        output.WriteLine(options.Format == OutputFormat.Wav ? "Mixing final WAV..." : "Mixing final M4A...");
        IReadOnlyList<MixdownResult> results;

        try
        {
            results = await mixdownService.MixAsync(mixdownRequest).ConfigureAwait(false);
        }
        catch (FileOutputException ex)
        {
            error.WriteLine(ex.Message);
            PrintStemPaths(recorders);
            return new RecordingResult(TapeDeckExitCode.FileOutputError, [], stopElapsed, ErrorMessage: ex.Message);
        }
        catch (Exception ex)
        {
            error.WriteLine($"Final mixing failed: {ex.Message}");
            PrintStemPaths(recorders);
            return new RecordingResult(TapeDeckExitCode.FinalMixFailed, [], stopElapsed, ErrorMessage: ex.Message);
        }

        TranscriptResult? transcript = null;
        if (options.Transcribe)
        {
            output.WriteLine("Transcribing TXT locally...");
            try
            {
                transcript = await TranscribeAsync(options, fileSet.FinalBasePath, mixdownRequest).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error.WriteLine($"Local transcription failed: {ex.Message}");
                foreach (var result in results)
                {
                    output.WriteLine($"Saved: {result.Path}");
                }

                output.WriteLine($"Duration: {stopElapsed:hh\\:mm\\:ss}");
                output.WriteLine($"Size: {ByteFormat.Format(results.Sum(result => result.SizeBytes))}");
                PrintStemPaths(recorders);
                return new RecordingResult(TapeDeckExitCode.TranscriptionFailed, results, stopElapsed, ErrorMessage: ex.Message);
            }
        }

        if (!options.KeepStems)
        {
            DeleteStemIfExists(fileSet.SystemStemPath);
            DeleteStemIfExists(fileSet.MicrophoneStemPath);
        }

        foreach (var result in results)
        {
            output.WriteLine($"Saved: {result.Path}");
        }

        if (transcript is not null)
        {
            output.WriteLine($"Transcript: {transcript.Path}");
            output.WriteLine($"Transcript recognizer: {transcript.RecognizerName} ({transcript.CultureName})");
        }

        output.WriteLine($"Duration: {stopElapsed:hh\\:mm\\:ss}");
        output.WriteLine($"Size: {ByteFormat.Format(results.Sum(result => result.SizeBytes))}");

        if (sizeWarningPrinted)
        {
            output.WriteLine("Warning: recording stopped at the safe WAV size limit.");
        }

        return new RecordingResult(TapeDeckExitCode.Success, results, stopElapsed, transcript);
    }

    private async Task<TranscriptResult> TranscribeAsync(RecordingOptions options, string finalBasePath, MixdownRequest mixdownRequest)
    {
        var transcriptPath = FileNameService.ResolveTranscriptPath(options.TranscriptOutputPath, finalBasePath, options.Overwrite);
        var transcriptSourcePath = FileNameService.GetTranscriptSourceWavePath(finalBasePath);
        await mixdownService.WriteTranscriptSourceWaveAsync(mixdownRequest with
        {
            OutputPath = transcriptSourcePath,
            Format = OutputFormat.Wav,
            SplitMinutes = null,
            Overwrite = options.Overwrite
        }, transcriptSourcePath).ConfigureAwait(false);

        try
        {
            return transcriptService.Transcribe(new TranscriptRequest(
                transcriptSourcePath,
                transcriptPath,
                options.TranscriptCultureName,
                options.Overwrite));
        }
        finally
        {
            DeleteTranscriptSourceIfExists(transcriptSourcePath);
        }
    }

    private void PrintStart(RecordingOptions options, string finalPath, SourceRecorder? systemRecorder, SourceRecorder? microphoneRecorder)
    {
        var sourceText = options.RecordSystem && options.RecordMicrophone
            ? "system audio + microphone"
            : options.RecordSystem
                ? "system audio"
                : "microphone";

        output.WriteLine($"Recording {sourceText}...");
        output.WriteLine($"Output: {finalPath}");
        output.WriteLine($"System device: {(systemRecorder is null ? "disabled" : systemRecorder.DisplayName)}");
        output.WriteLine($"Microphone device: {(microphoneRecorder is null ? "disabled" : microphoneRecorder.DisplayName)}");
        output.WriteLine($"System format: {FormatDescription(systemRecorder?.WaveFormat)}");
        output.WriteLine($"Mic format: {FormatDescription(microphoneRecorder?.WaveFormat)}");
        output.WriteLine($"Final format: {FormatFinalOutput(options)}");
        output.WriteLine("Press Ctrl+C to stop.");
    }

    private void PrintStatus(TimeSpan elapsed, SourceRecorder? systemRecorder, SourceRecorder? microphoneRecorder)
    {
        output.Write($"\rElapsed: {elapsed:hh\\:mm\\:ss} | System: {ByteFormat.Format(systemRecorder?.DataBytesWritten ?? 0)} | Mic: {ByteFormat.Format(microphoneRecorder?.DataBytesWritten ?? 0)}   ");
    }

    private static string FormatDescription(WaveFormat? format)
    {
        return format is null
            ? "disabled"
            : $"{format.SampleRate} Hz, {format.Channels} channels, {format.BitsPerSample} bits, {format.Encoding}";
    }

    private static string FormatFinalOutput(RecordingOptions options)
    {
        return options.Format switch
        {
            OutputFormat.Wav => "48000 Hz, stereo, 16-bit PCM WAV",
            OutputFormat.M4A => $"48000 Hz, stereo, AAC M4A, {options.AudioBitrate / 1000} kbps",
            OutputFormat.Txt => "TXT transcript, generated locally from mixed recording audio",
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Format, null)
        };
    }

    private void PrintStemPaths(IEnumerable<SourceRecorder> recorders)
    {
        foreach (var recorder in recorders)
        {
            if (File.Exists(recorder.StemPath))
            {
                error.WriteLine($"Stem preserved: {recorder.StemPath}");
            }
        }
    }

    private void DeleteStemIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Warning: source stem could not be deleted: {path}");
        }
    }

    private void DeleteTranscriptSourceIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Warning: temporary transcript source could not be deleted: {path}");
        }
    }
}

/// <summary>
/// Guards final PCM WAV data against the practical RIFF size limit.
/// </summary>
public static class WavSizeGuard
{
    /// <summary>
    /// Safe per-file PCM data limit, deliberately below the RIFF 4 GiB ceiling.
    /// </summary>
    public const long MaxFinalDataBytes = 3_500L * 1024 * 1024;

    /// <summary>
    /// Gets the final output byte rate for 48 kHz stereo 16-bit PCM.
    /// </summary>
    public const int FinalAverageBytesPerSecond = 48_000 * 2 * 2;

    /// <summary>
    /// Returns true when a final file at the supplied duration would approach the safe size limit.
    /// </summary>
    public static bool IsFinalDurationNearLimit(TimeSpan duration)
    {
        var estimatedBytes = (long)Math.Ceiling(Math.Max(0, duration.TotalSeconds) * FinalAverageBytesPerSecond);
        return estimatedBytes >= MaxFinalDataBytes;
    }

    /// <summary>
    /// Aligns the configured limit down to a valid block boundary.
    /// </summary>
    public static long GetAlignedLimit(int blockAlign)
    {
        return SourceRecorder.AlignBytesToBlock(MaxFinalDataBytes, blockAlign);
    }
}
