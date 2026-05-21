namespace TapeDeck.Tests;

public sealed class ParsingAndFormattingTests
{
    [Fact]
    public void TryParseDuration_AcceptsPositiveTimeSpan()
    {
        var parsed = CommandLineParser.TryParseDuration("01:30:00", out var duration);

        Assert.True(parsed);
        Assert.Equal(TimeSpan.FromMinutes(90), duration);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("not-a-duration")]
    public void TryParseDuration_RejectsInvalidValues(string value)
    {
        Assert.False(CommandLineParser.TryParseDuration(value, out _));
    }

    [Fact]
    public void TryParseGain_AcceptsNonNegativeFiniteNumber()
    {
        var parsed = CommandLineParser.TryParseGain("1.25", out var gain);

        Assert.True(parsed);
        Assert.Equal(1.25, gain);
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void TryParseGain_RejectsInvalidValues(string value)
    {
        Assert.False(CommandLineParser.TryParseGain(value, out _));
    }

    [Theory]
    [InlineData("m4a", OutputFormat.M4A)]
    [InlineData("mp4a", OutputFormat.M4A)]
    [InlineData("aac", OutputFormat.M4A)]
    [InlineData("wav", OutputFormat.Wav)]
    [InlineData("wave", OutputFormat.Wav)]
    public void TryParseOutputFormat_AcceptsSupportedValues(string value, OutputFormat expected)
    {
        var parsed = CommandLineParser.TryParseOutputFormat(value, out var format);

        Assert.True(parsed);
        Assert.Equal(expected, format);
    }

    [Fact]
    public void TryParseRecordOptions_InferWavFormatFromOutputExtension()
    {
        var parsed = CommandLineParser.TryParseRecordOptions(["--out", @"C:\Recordings\meeting.wav"], out var options, out var error);

        Assert.True(parsed, error);
        Assert.Equal(OutputFormat.Wav, options.Format);
    }

    [Fact]
    public void TryParseRecordOptions_RejectsExplicitFormatExtensionMismatch()
    {
        var parsed = CommandLineParser.TryParseRecordOptions(["--format", "m4a", "--out", @"C:\Recordings\meeting.wav"], out _, out var error);

        Assert.False(parsed);
        Assert.Contains("--out extension", error);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    public void ByteFormat_FormatsBinaryUnits(long bytes, string expected)
    {
        Assert.Equal(expected, ByteFormat.Format(bytes));
    }
}
