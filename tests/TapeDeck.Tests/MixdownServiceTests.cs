using NAudio.Wave;

namespace TapeDeck.Tests;

public sealed class MixdownServiceTests
{
    [Fact]
    public async Task MixAsync_WithSingleMonoStem_WritesFinalStereoPcmWave()
    {
        using var directory = new TemporaryDirectory();
        var stemPath = Path.Combine(directory.Path, "meeting.system.wav");
        var outputPath = Path.Combine(directory.Path, "meeting.wav");

        using (var writer = new WaveFileWriter(stemPath, new WaveFormat(8000, 16, 1)))
        {
            writer.Write(new byte[8000 * 2], 0, 8000 * 2);
        }

        var service = new MixdownService();
        var results = await service.MixAsync(new MixdownRequest(
            stemPath,
            null,
            outputPath,
            OutputFormat.Wav,
            1.0,
            1.0,
            128_000,
            null,
            false));

        Assert.Single(results);
        using var reader = new WaveFileReader(outputPath);
        Assert.Equal(48000, reader.WaveFormat.SampleRate);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(WaveFormatEncoding.Pcm, reader.WaveFormat.Encoding);
    }

    [Fact]
    public async Task MixAsync_WithSingleMonoStem_WritesFinalM4A()
    {
        using var directory = new TemporaryDirectory();
        var stemPath = Path.Combine(directory.Path, "meeting.system.wav");
        var outputPath = Path.Combine(directory.Path, "meeting.m4a");

        using (var writer = new WaveFileWriter(stemPath, new WaveFormat(8000, 16, 1)))
        {
            writer.Write(new byte[8000 * 2], 0, 8000 * 2);
        }

        var service = new MixdownService();
        var results = await service.MixAsync(new MixdownRequest(
            stemPath,
            null,
            outputPath,
            OutputFormat.M4A,
            1.0,
            1.0,
            128_000,
            null,
            false));

        Assert.Single(results);
        Assert.True(File.Exists(outputPath));
        Assert.True(new FileInfo(outputPath).Length > 0);
        Assert.Equal(outputPath, results[0].Path);
    }

    [Fact]
    public async Task WriteTranscriptSourceWaveAsync_WritesMono16KhzPcmWave()
    {
        using var directory = new TemporaryDirectory();
        var stemPath = Path.Combine(directory.Path, "meeting.system.wav");
        var outputPath = Path.Combine(directory.Path, "meeting.transcript-source.wav");

        using (var writer = new WaveFileWriter(stemPath, new WaveFormat(8000, 16, 1)))
        {
            writer.Write(new byte[8000 * 2], 0, 8000 * 2);
        }

        var service = new MixdownService();
        await service.WriteTranscriptSourceWaveAsync(new MixdownRequest(
            stemPath,
            null,
            outputPath,
            OutputFormat.Wav,
            1.0,
            1.0,
            128_000,
            null,
            false), outputPath);

        using var reader = new WaveFileReader(outputPath);
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(WaveFormatEncoding.Pcm, reader.WaveFormat.Encoding);
    }
}
