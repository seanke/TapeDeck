namespace TapeDeck.Tests;

public sealed class FileNameServiceTests
{
    [Fact]
    public void GetDefaultOutputPath_UsesTapeDeckRecordingsFolderAndTimestamp()
    {
        var localDate = new DateTime(2026, 5, 21, 14, 3, 2, DateTimeKind.Unspecified);
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(localDate);
        var path = FileNameService.GetDefaultOutputPath(new DateTimeOffset(localDate, localOffset));

        Assert.EndsWith(Path.Combine("Recordings", "TapeDeck", "2026-05-21_14-03-02.m4a"), path);
    }

    [Fact]
    public void GetDefaultOutputPath_WhenWavFormatIsRequested_UsesWavExtension()
    {
        var localDate = new DateTime(2026, 5, 21, 14, 3, 2, DateTimeKind.Unspecified);
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(localDate);
        var path = FileNameService.GetDefaultOutputPath(new DateTimeOffset(localDate, localOffset), OutputFormat.Wav);

        Assert.EndsWith(Path.Combine("Recordings", "TapeDeck", "2026-05-21_14-03-02.wav"), path);
    }

    [Fact]
    public void CreateFileSet_WhenFinalExistsAndOverwriteIsFalse_AddsCollisionSuffix()
    {
        using var directory = new TemporaryDirectory();
        var original = Path.Combine(directory.Path, "meeting.wav");
        File.WriteAllText(original, "existing");

        var fileSet = FileNameService.CreateFileSet(new RecordingOptions { OutputPath = original, Format = OutputFormat.Wav }, DateTimeOffset.Now);

        Assert.Equal(Path.Combine(directory.Path, "meeting.001.wav"), fileSet.FinalBasePath);
        Assert.Equal(Path.Combine(directory.Path, "meeting.001.system.partial.wav"), fileSet.SystemPartialPath);
        Assert.Equal(Path.Combine(directory.Path, "meeting.001.mic.partial.wav"), fileSet.MicrophonePartialPath);
    }

    [Fact]
    public void CreateFileSet_WhenOverwriteIsTrue_ReusesRequestedPath()
    {
        using var directory = new TemporaryDirectory();
        var original = Path.Combine(directory.Path, "meeting.wav");
        File.WriteAllText(original, "existing");

        var fileSet = FileNameService.CreateFileSet(new RecordingOptions { OutputPath = original, Format = OutputFormat.Wav, Overwrite = true }, DateTimeOffset.Now);

        Assert.Equal(original, fileSet.FinalBasePath);
    }

    [Fact]
    public void CreateFileSet_WhenExtensionIsUnknown_AppendsFormatExtension()
    {
        using var directory = new TemporaryDirectory();
        var original = Path.Combine(directory.Path, "meeting.audio");

        var fileSet = FileNameService.CreateFileSet(new RecordingOptions { OutputPath = original }, DateTimeOffset.Now);

        Assert.Equal(Path.Combine(directory.Path, "meeting.audio.m4a"), fileSet.FinalBasePath);
    }

    [Fact]
    public void GetPartPath_AddsPartNumberBeforeExtension()
    {
        var path = FileNameService.GetPartPath(@"C:\Recordings\meeting.wav", 2);

        Assert.Equal(@"C:\Recordings\meeting.part002.wav", path);
    }

    [Theory]
    [InlineData(@"C:\Recordings\meeting.wav", @"C:\Recordings\meeting.mixed.partial.wav")]
    [InlineData(@"C:\Recordings\meeting.m4a", @"C:\Recordings\meeting.mixed.partial.m4a")]
    public void GetMixedPartialPath_UsesFinalOutputExtension(string finalPath, string expected)
    {
        Assert.Equal(expected, FileNameService.GetMixedPartialPath(finalPath));
    }

    [Fact]
    public void ResolveTranscriptPath_WhenNoPathIsSupplied_UsesFinalAudioBaseName()
    {
        using var directory = new TemporaryDirectory();
        var finalPath = Path.Combine(directory.Path, "meeting.m4a");

        var transcriptPath = FileNameService.ResolveTranscriptPath(null, finalPath, false);

        Assert.Equal(Path.Combine(directory.Path, "meeting.txt"), transcriptPath);
    }

    [Fact]
    public void ResolveTranscriptPath_WhenTranscriptExistsAndOverwriteIsFalse_AddsCollisionSuffix()
    {
        using var directory = new TemporaryDirectory();
        var transcriptPath = Path.Combine(directory.Path, "meeting.txt");
        File.WriteAllText(transcriptPath, "existing");

        var resolved = FileNameService.ResolveTranscriptPath(transcriptPath, Path.Combine(directory.Path, "meeting.m4a"), false);

        Assert.Equal(Path.Combine(directory.Path, "meeting.001.txt"), resolved);
    }

    [Fact]
    public void GetTranscriptSourceWavePath_UsesFinalAudioBaseName()
    {
        var path = FileNameService.GetTranscriptSourceWavePath(@"C:\Recordings\meeting.m4a");

        Assert.Equal(@"C:\Recordings\meeting.transcript-source.wav", path);
    }
}
