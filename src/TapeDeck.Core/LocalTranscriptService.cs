using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Text;
using NAudio.Wave;

namespace TapeDeck;

/// <summary>
/// Creates plain-text transcripts using installed Windows speech recognizers.
/// </summary>
public sealed class LocalTranscriptService
{
    /// <summary>
    /// Checks whether the installed Windows speech recognizer needed for TXT output is present and usable.
    /// </summary>
    public TranscriptRequirementsResult CheckRequirements(string? cultureName)
    {
        string? probePath = null;
        try
        {
            var recognizerInfo = ResolveRecognizer(cultureName);
            probePath = CreateRequirementsProbeWaveFile();
            using var recognizer = new SpeechRecognitionEngine(recognizerInfo);
            var grammar = new DictationGrammar();
            recognizer.LoadGrammar(grammar);
            recognizer.SetInputToWaveFile(probePath);
            _ = recognizer.Recognize();

            return TranscriptRequirementsResult.Available(
                recognizerInfo.Culture.Name,
                recognizerInfo.Name);
        }
        catch (TranscriptionException ex)
        {
            return TranscriptRequirementsResult.Unavailable(ex.Message);
        }
        catch (CultureNotFoundException ex)
        {
            return TranscriptRequirementsResult.Unavailable($"Invalid transcript culture '{cultureName}': {ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or COMException or UnauthorizedAccessException or NotSupportedException or IOException or ArgumentException)
        {
            return TranscriptRequirementsResult.Unavailable("Local transcription could not be opened. Windows speech recognition is not available for the selected language.");
        }
        finally
        {
            DeleteProbeWaveFile(probePath);
        }
    }

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

    private static string CreateRequirementsProbeWaveFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TapeDeck");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"transcript-probe-{Guid.NewGuid():N}.wav");
        var format = new WaveFormat(16_000, 16, 1);
        var silence = new byte[format.AverageBytesPerSecond / 4];
        using var writer = new WaveFileWriter(path, format);
        writer.Write(silence, 0, silence.Length);
        return path;
    }

    private static void DeleteProbeWaveFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A leftover temp probe is harmless and should not hide the actual requirement result.
        }
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
/// Describes whether local TXT transcript requirements are available.
/// </summary>
public sealed record TranscriptRequirementsResult(
    bool IsAvailable,
    string Message,
    string Instructions,
    string? CultureName = null,
    string? RecognizerName = null)
{
    /// <summary>
    /// Gets the user-facing setup instructions for local TXT transcript support.
    /// </summary>
    public const string SetupInstructions = "Install a Windows speech recognition language pack, then restart TapeDeck. In Windows Settings, open Time & language > Language & region, choose the language options for your preferred language, and install Speech recognition. Also check Time & language > Speech for the matching speech language.";

    /// <summary>
    /// Creates an available requirements result.
    /// </summary>
    public static TranscriptRequirementsResult Available(string cultureName, string recognizerName)
    {
        return new TranscriptRequirementsResult(
            true,
            $"TXT transcription ready: {recognizerName} ({cultureName}).",
            string.Empty,
            cultureName,
            recognizerName);
    }

    /// <summary>
    /// Creates an unavailable requirements result with setup instructions.
    /// </summary>
    public static TranscriptRequirementsResult Unavailable(string message)
    {
        return new TranscriptRequirementsResult(false, message, SetupInstructions);
    }

    /// <summary>
    /// Formats the warning and setup instructions for console or app status output.
    /// </summary>
    public string ToDisplayText()
    {
        return IsAvailable || string.IsNullOrWhiteSpace(Instructions)
            ? Message
            : $"{Message} {Instructions}";
    }
}

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
