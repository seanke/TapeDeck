namespace TapeDeck;

/// <summary>
/// Describes all output and temporary files used by one recording session.
/// </summary>
public sealed record RecordingFileSet(
    string FinalBasePath,
    string SystemPartialPath,
    string MicrophonePartialPath,
    string SystemStemPath,
    string MicrophoneStemPath);

/// <summary>
/// Creates output, sidecar, and collision-safe file names.
/// </summary>
public static class FileNameService
{
    /// <summary>
    /// Gets the default output path under the user's TapeDeck recordings folder.
    /// </summary>
    public static string GetDefaultOutputPath(DateTimeOffset now, OutputFormat format = OutputFormat.M4A)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.CurrentDirectory;
        }

        return Path.Combine(
            userProfile,
            "Recordings",
            "TapeDeck",
            $"{now.LocalDateTime:yyyy-MM-dd_HH-mm-ss}{GetExtension(format)}");
    }

    /// <summary>
    /// Creates a complete output file set for a recording session.
    /// </summary>
    public static RecordingFileSet CreateFileSet(RecordingOptions options, DateTimeOffset now)
    {
        var requestedOutput = string.IsNullOrWhiteSpace(options.OutputPath)
            ? GetDefaultOutputPath(now, options.Format)
            : options.OutputPath;

        var finalBasePath = ResolveFinalPath(requestedOutput, options.Format, options.Overwrite, options.SplitMinutes is not null);
        var baseWithoutExtension = Path.Combine(
            Path.GetDirectoryName(finalBasePath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(finalBasePath));

        return new RecordingFileSet(
            finalBasePath,
            $"{baseWithoutExtension}.system.partial.wav",
            $"{baseWithoutExtension}.mic.partial.wav",
            $"{baseWithoutExtension}.system.wav",
            $"{baseWithoutExtension}.mic.wav");
    }

    /// <summary>
    /// Gets the final output path for a split part.
    /// </summary>
    public static string GetPartPath(string finalBasePath, int partNumber)
    {
        var directory = Path.GetDirectoryName(finalBasePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(finalBasePath);
        var extension = Path.GetExtension(finalBasePath);
        return Path.Combine(directory, $"{name}.part{partNumber:000}{extension}");
    }

    /// <summary>
    /// Gets the temporary mixed partial path for a final output path.
    /// </summary>
    public static string GetMixedPartialPath(string finalPath)
    {
        var directory = Path.GetDirectoryName(finalPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(finalPath);
        var extension = Path.GetExtension(finalPath);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".m4a";
        }

        return Path.Combine(directory, $"{name}.mixed.partial{extension}");
    }

    /// <summary>
    /// Resolves the final transcript text path, applying the same collision behavior as audio outputs.
    /// </summary>
    public static string ResolveTranscriptPath(string? requestedTranscriptPath, string finalBasePath, bool overwrite)
    {
        var requestedPath = string.IsNullOrWhiteSpace(requestedTranscriptPath)
            ? Path.ChangeExtension(finalBasePath, ".txt")
            : requestedTranscriptPath;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(requestedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new FileOutputException($"The transcript path is invalid: {requestedPath}", ex);
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            fullPath += ".txt";
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new FileOutputException($"The transcript path is invalid: {requestedPath}");
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new FileOutputException($"The transcript output directory could not be created: {directory}", ex);
        }

        if (overwrite)
        {
            return fullPath;
        }

        var candidate = fullPath;
        var suffix = 1;
        while (File.Exists(candidate) || File.Exists(GetTranscriptPartialPath(candidate)))
        {
            candidate = AddCollisionSuffix(fullPath, suffix++);
        }

        return candidate;
    }

    /// <summary>
    /// Gets the temporary partial transcript path for a final transcript path.
    /// </summary>
    public static string GetTranscriptPartialPath(string transcriptPath)
    {
        var directory = Path.GetDirectoryName(transcriptPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(transcriptPath);
        return Path.Combine(directory, $"{name}.partial.txt");
    }

    /// <summary>
    /// Gets the temporary mixed WAV path used as local speech recognition input.
    /// </summary>
    public static string GetTranscriptSourceWavePath(string finalBasePath)
    {
        var directory = Path.GetDirectoryName(finalBasePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(finalBasePath);
        return Path.Combine(directory, $"{name}.transcript-source.wav");
    }

    /// <summary>
    /// Gets the partial path for the temporary mixed transcript-source WAV.
    /// </summary>
    public static string GetTranscriptSourcePartialWavePath(string transcriptSourcePath)
    {
        var directory = Path.GetDirectoryName(transcriptSourcePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(transcriptSourcePath);
        return Path.Combine(directory, $"{name}.partial.wav");
    }

    private static string ResolveFinalPath(string path, OutputFormat format, bool overwrite, bool split)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new FileOutputException($"The output path is invalid: {path}", ex);
        }

        var expectedExtension = GetExtension(format);
        if (!string.Equals(Path.GetExtension(fullPath), expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            fullPath += expectedExtension;
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new FileOutputException($"The output path is invalid: {path}");
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new FileOutputException($"The output directory could not be created: {directory}", ex);
        }

        if (overwrite)
        {
            return fullPath;
        }

        var candidate = fullPath;
        var suffix = 1;
        while (AnyOutputPathExists(candidate, split))
        {
            candidate = AddCollisionSuffix(fullPath, suffix++);
        }

        return candidate;
    }

    private static bool AnyOutputPathExists(string finalPath, bool split)
    {
        if (split)
        {
            if (File.Exists(GetPartPath(finalPath, 1)) || File.Exists(GetMixedPartialPath(GetPartPath(finalPath, 1))))
            {
                return true;
            }
        }
        else if (File.Exists(finalPath) || File.Exists(GetMixedPartialPath(finalPath)))
        {
            return true;
        }

        var baseWithoutExtension = Path.Combine(
            Path.GetDirectoryName(finalPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(finalPath));

        return File.Exists($"{baseWithoutExtension}.system.partial.wav")
            || File.Exists($"{baseWithoutExtension}.mic.partial.wav")
            || File.Exists($"{baseWithoutExtension}.system.wav")
            || File.Exists($"{baseWithoutExtension}.mic.wav");
    }

    private static string AddCollisionSuffix(string path, int suffix)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return Path.Combine(directory, $"{name}.{suffix:000}{extension}");
    }

    private static string GetExtension(OutputFormat format)
    {
        return format == OutputFormat.Wav ? ".wav" : ".m4a";
    }
}

/// <summary>
/// Represents a file or path error that should be shown as a clean CLI failure.
/// </summary>
public sealed class FileOutputException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FileOutputException"/> class.
    /// </summary>
    public FileOutputException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileOutputException"/> class with an inner exception.
    /// </summary>
    public FileOutputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
