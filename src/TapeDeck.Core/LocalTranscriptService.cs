using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Text;

namespace TapeDeck;

/// <summary>
/// Creates plain-text transcripts using installed Windows speech recognizers.
/// </summary>
public sealed class LocalTranscriptService
{
    /// <summary>
    /// Transcribes a WAV file into a TXT file without calling an online transcription service.
    /// </summary>
    public TranscriptResult Transcribe(TranscriptRequest request)
    {
        if (!File.Exists(request.AudioPath))
        {
            throw new FileOutputException($"The transcript source WAV was not found: {request.AudioPath}");
        }

        var partialPath = FileNameService.GetTranscriptPartialPath(request.OutputPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
            if (!request.Overwrite && File.Exists(request.OutputPath))
            {
                throw new FileOutputException($"The transcript output file already exists: {request.OutputPath}");
            }

            if (File.Exists(partialPath))
            {
                if (!request.Overwrite)
                {
                    throw new FileOutputException($"The transcript partial file already exists: {partialPath}");
                }

                File.Delete(partialPath);
            }

            var recognizerInfo = ResolveRecognizer(request.CultureName);
            using var recognizer = new SpeechRecognitionEngine(recognizerInfo);
            var grammar = new DictationGrammar();
            recognizer.LoadGrammar(grammar);
            recognizer.SetInputToWaveFile(request.AudioPath);

            using (var writer = new StreamWriter(partialPath, false, Encoding.UTF8))
            {
                while (recognizer.Recognize() is { } result)
                {
                    if (!string.IsNullOrWhiteSpace(result.Text))
                    {
                        writer.WriteLine(result.Text);
                    }
                }
            }

            File.Move(partialPath, request.OutputPath, request.Overwrite);
            return new TranscriptResult(
                request.OutputPath,
                new FileInfo(request.OutputPath).Length,
                recognizerInfo.Culture.Name,
                recognizerInfo.Name);
        }
        catch (FileOutputException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new FileOutputException($"The transcript file could not be written: {request.OutputPath}", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or COMException)
        {
            throw new TranscriptionException("Local transcription failed. Check that Windows speech recognition is installed for the selected language.", ex);
        }
    }

    private static RecognizerInfo ResolveRecognizer(string? cultureName)
    {
        var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
        if (recognizers.Count == 0)
        {
            throw new TranscriptionException("No local Windows speech recognizers are installed.");
        }

        if (!string.IsNullOrWhiteSpace(cultureName))
        {
            var requestedCulture = CultureInfo.GetCultureInfo(cultureName);
            foreach (var recognizer in recognizers)
            {
                if (recognizer.Culture.Equals(requestedCulture))
                {
                    return recognizer;
                }
            }

            throw new TranscriptionException($"No local Windows speech recognizer is installed for culture '{requestedCulture.Name}'.");
        }

        var currentUiCulture = CultureInfo.CurrentUICulture;
        foreach (var recognizer in recognizers)
        {
            if (recognizer.Culture.Equals(currentUiCulture))
            {
                return recognizer;
            }
        }

        var currentCulture = CultureInfo.CurrentCulture;
        foreach (var recognizer in recognizers)
        {
            if (recognizer.Culture.Equals(currentCulture))
            {
                return recognizer;
            }
        }

        return recognizers[0];
    }
}

/// <summary>
/// Request data for local transcript generation.
/// </summary>
public sealed record TranscriptRequest(
    string AudioPath,
    string OutputPath,
    string? CultureName,
    bool Overwrite);

/// <summary>
/// Result data for a written transcript file.
/// </summary>
public sealed record TranscriptResult(
    string Path,
    long SizeBytes,
    string CultureName,
    string RecognizerName);

/// <summary>
/// Raised when local transcript generation fails.
/// </summary>
public sealed class TranscriptionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TranscriptionException"/> class.
    /// </summary>
    public TranscriptionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscriptionException"/> class with an inner exception.
    /// </summary>
    public TranscriptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
