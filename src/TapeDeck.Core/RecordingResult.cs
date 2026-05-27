namespace TapeDeck;

/// <summary>
/// Describes the outcome of a recording session.
/// </summary>
public sealed record RecordingResult(
    TapeDeckExitCode ExitCode,
    IReadOnlyList<MixdownResult> Outputs,
    TimeSpan Duration,
    TranscriptResult? Transcript = null,
    string? ErrorMessage = null)
{
    /// <summary>
    /// Gets a value indicating whether the session completed successfully.
    /// </summary>
    public bool Succeeded => ExitCode == TapeDeckExitCode.Success;
}
