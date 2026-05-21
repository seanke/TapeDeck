namespace TapeDeck.Tests;

public sealed class AudioHelperTests
{
    [Fact]
    public void AlignBytesToBlock_RoundsDownToBlockBoundary()
    {
        Assert.Equal(1000, SourceRecorder.AlignBytesToBlock(1003, 4));
    }

    [Fact]
    public void CalculateExpectedBytes_UsesElapsedTimeAndBlockAlignment()
    {
        var bytes = SourceRecorder.CalculateExpectedBytes(TimeSpan.FromSeconds(1.25), 192000, 4);

        Assert.Equal(240000, bytes);
    }

    [Fact]
    public void WavSizeGuard_ReportsDurationNearSafeLimit()
    {
        var tooLong = TimeSpan.FromSeconds((WavSizeGuard.MaxFinalDataBytes / WavSizeGuard.FinalAverageBytesPerSecond) + 1);
        var shortEnough = TimeSpan.FromMinutes(1);

        Assert.True(WavSizeGuard.IsFinalDurationNearLimit(tooLong));
        Assert.False(WavSizeGuard.IsFinalDurationNearLimit(shortEnough));
        Assert.Equal(0, WavSizeGuard.GetAlignedLimit(4) % 4);
    }

    [Fact]
    public void StereoConversion_DuplicatesMono()
    {
        var converted = StereoSampleProvider.ConvertFrameToStereo([0.25f]);

        Assert.Equal(0.25f, converted.Left);
        Assert.Equal(0.25f, converted.Right);
    }

    [Fact]
    public void StereoConversion_LeavesStereoUnchanged()
    {
        var converted = StereoSampleProvider.ConvertFrameToStereo([0.25f, -0.5f]);

        Assert.Equal(0.25f, converted.Left);
        Assert.Equal(-0.5f, converted.Right);
    }

    [Fact]
    public void StereoConversion_DownmixesMoreThanTwoChannels()
    {
        var converted = StereoSampleProvider.ConvertFrameToStereo([1.0f, 0.5f, 0.0f, -0.5f]);

        Assert.Equal(0.5f, converted.Left);
        Assert.Equal(0.0f, converted.Right);
    }

    [Theory]
    [InlineData(0.5, 1.0)]
    [InlineData(0.98, 1.0)]
    [InlineData(2.0, 0.49)]
    public void CalculateNormalizationGain_ReducesPeaksAboveThreshold(double peak, double expected)
    {
        Assert.Equal(expected, MixdownService.CalculateNormalizationGain(peak), precision: 6);
    }

    [Fact]
    public void StopRequestGate_AllowsOnlyOneStopRequest()
    {
        var gate = new StopRequestGate();

        Assert.True(gate.TryRequestStop());
        Assert.False(gate.TryRequestStop());
    }
}
