using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TapeDeck;

/// <summary>
/// Converts, mixes, normalizes, and writes final audio files.
/// </summary>
public sealed class MixdownService
{
    private const int OutputSampleRate = 48_000;
    private const int OutputChannels = 2;
    private const int OutputBitsPerSample = 16;
    private const int FloatReadBlockSamples = 16_384;
    private const int ByteReadBlockSize = 64 * 1024;
    private static readonly WaveFormat FinalWaveFormat = new(OutputSampleRate, OutputBitsPerSample, OutputChannels);

    /// <summary>
    /// Performs a block-based two-pass mixdown.
    /// </summary>
    public Task<IReadOnlyList<MixdownResult>> MixAsync(MixdownRequest request)
    {
        if (request.SystemStemPath is null && request.MicrophoneStemPath is null)
        {
            throw new InvalidOperationException("At least one source stem is required for mixdown.");
        }

        var peak = FindPeak(request);
        var normalizationGain = CalculateNormalizationGain(peak);
        var results = WriteFinalFiles(request, normalizationGain);
        return Task.FromResult<IReadOnlyList<MixdownResult>>(results);
    }

    /// <summary>
    /// Calculates the post-mix normalization gain required to keep peaks below 0.98.
    /// </summary>
    public static double CalculateNormalizationGain(double peakAbsoluteSample)
    {
        return double.IsFinite(peakAbsoluteSample) && peakAbsoluteSample > 0.98
            ? 0.98 / peakAbsoluteSample
            : 1.0;
    }

    private static double FindPeak(MixdownRequest request)
    {
        using var context = MixProviderContext.Create(request);
        var buffer = new float[FloatReadBlockSamples];
        var peak = 0.0;
        int read;

        while ((read = context.Provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                peak = Math.Max(peak, Math.Abs(buffer[i]));
            }
        }

        return peak;
    }

    private static IReadOnlyList<MixdownResult> WriteFinalFiles(MixdownRequest request, double normalizationGain)
    {
        using var context = MixProviderContext.Create(request);
        ISampleProvider sampleProvider = context.Provider;
        if (normalizationGain != 1.0)
        {
            sampleProvider = new GainSampleProvider(sampleProvider, normalizationGain);
        }

        var waveProvider = new SampleToWaveProvider16(sampleProvider);
        return request.Format == OutputFormat.Wav
            ? WriteWaveFiles(request, waveProvider)
            : WriteM4AFiles(request, waveProvider);
    }

    private static IReadOnlyList<MixdownResult> WriteWaveFiles(MixdownRequest request, IWaveProvider waveProvider)
    {
        var buffer = new byte[ByteReadBlockSize];
        using var writer = new SplitWaveWriter(request);

        int read;
        while ((read = waveProvider.Read(buffer, 0, buffer.Length)) > 0)
        {
            writer.Write(buffer, 0, read);
        }

        return writer.Complete();
    }

    private static IReadOnlyList<MixdownResult> WriteM4AFiles(MixdownRequest request, IWaveProvider waveProvider)
    {
        using var writer = new SplitM4AWriter(request, waveProvider);
        return writer.Complete();
    }

    private sealed class MixProviderContext : IDisposable
    {
        private readonly List<IDisposable> disposables;

        private MixProviderContext(ISampleProvider provider, List<IDisposable> disposables)
        {
            Provider = provider;
            this.disposables = disposables;
        }

        public ISampleProvider Provider { get; }

        public static MixProviderContext Create(MixdownRequest request)
        {
            var providers = new List<ISampleProvider>();
            var disposables = new List<IDisposable>();

            if (request.SystemStemPath is not null)
            {
                providers.Add(CreateSourceProvider(request.SystemStemPath, request.SystemGain, disposables));
            }

            if (request.MicrophoneStemPath is not null)
            {
                providers.Add(CreateSourceProvider(request.MicrophoneStemPath, request.MicrophoneGain, disposables));
            }

            if (providers.Count == 0)
            {
                throw new InvalidOperationException("At least one source provider is required.");
            }

            var mixer = new MixingSampleProvider(providers)
            {
                ReadFully = false
            };

            return new MixProviderContext(mixer, disposables);
        }

        public void Dispose()
        {
            foreach (var disposable in disposables)
            {
                disposable.Dispose();
            }
        }

        private static ISampleProvider CreateSourceProvider(string path, double gain, List<IDisposable> disposables)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Source stem file was not found.", path);
            }

            var reader = new WaveFileReader(path);
            disposables.Add(reader);

            ISampleProvider provider = new WaveStreamSampleProvider(reader);
            if (provider.WaveFormat.SampleRate != OutputSampleRate)
            {
                provider = new WdlResamplingSampleProvider(provider, OutputSampleRate);
            }

            provider = StereoSampleProvider.EnsureStereo(provider);
            if (gain != 1.0)
            {
                provider = new GainSampleProvider(provider, gain);
            }

            return provider;
        }
    }

    private sealed class SplitWaveWriter : IDisposable
    {
        private readonly MixdownRequest request;
        private readonly long partDataLimitBytes;
        private readonly List<MixdownResult> results = [];
        private WaveFileWriter? writer;
        private string? currentFinalPath;
        private string? currentPartialPath;
        private long currentDataBytes;
        private int partNumber;
        private bool completed;

        public SplitWaveWriter(MixdownRequest request)
        {
            this.request = request;
            partDataLimitBytes = CalculatePartDataLimit(request.SplitMinutes);
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            var remaining = count;
            var currentOffset = offset;
            while (remaining > 0)
            {
                EnsureWriter();
                var remainingForPart = partDataLimitBytes - currentDataBytes;
                if (remainingForPart <= 0)
                {
                    if (request.SplitMinutes is null)
                    {
                        throw new WavSizeLimitException("Final WAV would exceed the safe RIFF WAV size limit. Use --split-minutes for long recordings.");
                    }

                    CloseCurrentPart();
                    continue;
                }

                var writeBytes = (int)Math.Min(remaining, remainingForPart);
                writeBytes = (int)SourceRecorder.AlignBytesToBlock(writeBytes, FinalWaveFormat.BlockAlign);
                if (writeBytes == 0)
                {
                    if (request.SplitMinutes is null)
                    {
                        throw new WavSizeLimitException("Final WAV would exceed the safe RIFF WAV size limit. Use --split-minutes for long recordings.");
                    }

                    CloseCurrentPart();
                    continue;
                }

                writer!.Write(buffer, currentOffset, writeBytes);
                currentDataBytes += writeBytes;
                currentOffset += writeBytes;
                remaining -= writeBytes;

                if (request.SplitMinutes is not null && currentDataBytes >= partDataLimitBytes && remaining > 0)
                {
                    CloseCurrentPart();
                }
            }
        }

        public IReadOnlyList<MixdownResult> Complete()
        {
            EnsureWriter();
            CloseCurrentPart();
            completed = true;
            return results;
        }

        public void Dispose()
        {
            if (writer is not null)
            {
                writer.Dispose();
                writer = null;
            }

            if (!completed && currentPartialPath is not null && File.Exists(currentPartialPath))
            {
                // Keep the mixed partial on failure; it can be useful with the preserved stems.
            }
        }

        private static long CalculatePartDataLimit(int? splitMinutes)
        {
            var sizeLimit = WavSizeGuard.GetAlignedLimit(FinalWaveFormat.BlockAlign);
            if (splitMinutes is null)
            {
                return sizeLimit;
            }

            long splitBytes;
            try
            {
                splitBytes = checked(splitMinutes.Value * 60L * WavSizeGuard.FinalAverageBytesPerSecond);
            }
            catch (OverflowException)
            {
                splitBytes = long.MaxValue;
            }

            return SourceRecorder.AlignBytesToBlock(Math.Min(splitBytes, sizeLimit), FinalWaveFormat.BlockAlign);
        }

        private void EnsureWriter()
        {
            if (writer is not null)
            {
                return;
            }

            partNumber++;
            currentDataBytes = 0;
            currentFinalPath = request.SplitMinutes is null
                ? request.OutputPath
                : FileNameService.GetPartPath(request.OutputPath, partNumber);
            currentPartialPath = FileNameService.GetMixedPartialPath(currentFinalPath);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(currentFinalPath)!);
                if (!request.Overwrite && File.Exists(currentFinalPath))
                {
                    throw new FileOutputException($"The output file already exists: {currentFinalPath}");
                }

                if (File.Exists(currentPartialPath))
                {
                    if (!request.Overwrite)
                    {
                        throw new FileOutputException($"The temporary mixed partial already exists: {currentPartialPath}");
                    }

                    File.Delete(currentPartialPath);
                }

                writer = new WaveFileWriter(currentPartialPath, FinalWaveFormat);
            }
            catch (FileOutputException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new FileOutputException($"The mixed output file could not be opened: {currentPartialPath}", ex);
            }
        }

        private void CloseCurrentPart()
        {
            if (writer is null || currentFinalPath is null || currentPartialPath is null)
            {
                return;
            }

            writer.Dispose();
            writer = null;

            try
            {
                File.Move(currentPartialPath, currentFinalPath, request.Overwrite);
                results.Add(new MixdownResult(currentFinalPath, new FileInfo(currentFinalPath).Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FileOutputException($"The mixed output file could not be finalized: {currentFinalPath}", ex);
            }

            currentFinalPath = null;
            currentPartialPath = null;
        }
    }

    private sealed class SplitM4AWriter : IDisposable
    {
        private readonly MixdownRequest request;
        private readonly IWaveProvider waveProvider;
        private readonly long? partDataLimitBytes;
        private readonly List<MixdownResult> results = [];
        private int partNumber;
        private bool completed;
        private bool sourceEnded;

        public SplitM4AWriter(MixdownRequest request, IWaveProvider waveProvider)
        {
            this.request = request;
            this.waveProvider = waveProvider;
            partDataLimitBytes = request.SplitMinutes is null
                ? null
                : SourceRecorder.AlignBytesToBlock(
                    request.SplitMinutes.Value * 60L * WavSizeGuard.FinalAverageBytesPerSecond,
                    FinalWaveFormat.BlockAlign);
        }

        public IReadOnlyList<MixdownResult> Complete()
        {
            do
            {
                var wrotePart = EncodeNextPart();
                if (!wrotePart)
                {
                    break;
                }
            }
            while (partDataLimitBytes is not null && !sourceEnded);

            completed = true;
            return results;
        }

        public void Dispose()
        {
            if (!completed)
            {
                // Encoded partials are intentionally left in place on failure.
            }
        }

        private bool EncodeNextPart()
        {
            partNumber++;
            var finalPath = request.SplitMinutes is null
                ? request.OutputPath
                : FileNameService.GetPartPath(request.OutputPath, partNumber);
            var partialPath = FileNameService.GetMixedPartialPath(finalPath);
            PrepareOutputPaths(finalPath, partialPath);

            var source = partDataLimitBytes is null
                ? new CountingWaveProvider(waveProvider)
                : new CountingWaveProvider(new ByteLimitedWaveProvider(waveProvider, partDataLimitBytes.Value));

            try
            {
                MediaFoundationEncoder.EncodeToAac(source, partialPath, request.AudioBitrate);
            }
            catch (Exception ex)
            {
                throw new FileOutputException($"The M4A output file could not be encoded: {partialPath}", ex);
            }

            if (source.BytesRead == 0 && results.Count > 0)
            {
                DeleteIfExists(partialPath);
                sourceEnded = true;
                return false;
            }

            sourceEnded = source.HitSourceEnd;

            try
            {
                File.Move(partialPath, finalPath, request.Overwrite);
                results.Add(new MixdownResult(finalPath, new FileInfo(finalPath).Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FileOutputException($"The M4A output file could not be finalized: {finalPath}", ex);
            }

            return partDataLimitBytes is null || source.HitSourceEnd || source.BytesRead > 0;
        }

        private void PrepareOutputPaths(string finalPath, string partialPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                if (!request.Overwrite && File.Exists(finalPath))
                {
                    throw new FileOutputException($"The output file already exists: {finalPath}");
                }

                if (File.Exists(partialPath))
                {
                    if (!request.Overwrite)
                    {
                        throw new FileOutputException($"The temporary mixed partial already exists: {partialPath}");
                    }

                    File.Delete(partialPath);
                }
            }
            catch (FileOutputException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new FileOutputException($"The M4A output file could not be opened: {partialPath}", ex);
            }
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // The caller already has the source stems; a leftover empty partial is harmless.
            }
        }
    }
}

/// <summary>
/// Request data for a final mixdown.
/// </summary>
public sealed record MixdownRequest(
    string? SystemStemPath,
    string? MicrophoneStemPath,
    string OutputPath,
    OutputFormat Format,
    double SystemGain,
    double MicrophoneGain,
    int AudioBitrate,
    int? SplitMinutes,
    bool Overwrite);

/// <summary>
/// Result data for a written final WAV file.
/// </summary>
public sealed record MixdownResult(string Path, long SizeBytes);

/// <summary>
/// Raised when a final WAV would exceed the safe RIFF WAV size limit.
/// </summary>
public sealed class WavSizeLimitException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WavSizeLimitException"/> class.
    /// </summary>
    public WavSizeLimitException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Converts a WAV stream to floating-point samples without loading the whole file.
/// </summary>
public sealed class WaveStreamSampleProvider : ISampleProvider
{
    private readonly WaveStream stream;
    private readonly WaveFormat sourceFormat;
    private readonly int bytesPerSample;
    private byte[] byteBuffer = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="WaveStreamSampleProvider"/> class.
    /// </summary>
    public WaveStreamSampleProvider(WaveStream stream)
    {
        this.stream = stream;
        sourceFormat = stream.WaveFormat is WaveFormatExtensible extensible
            ? extensible.ToStandardWaveFormat()
            : stream.WaveFormat;

        bytesPerSample = sourceFormat.BitsPerSample / 8;
        if (bytesPerSample <= 0)
        {
            throw new NotSupportedException($"Unsupported source bit depth: {sourceFormat.BitsPerSample}.");
        }

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sourceFormat.SampleRate, sourceFormat.Channels);
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat { get; }

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        var samplesToRead = count - (count % sourceFormat.Channels);
        if (samplesToRead <= 0)
        {
            return 0;
        }

        var bytesRequired = samplesToRead * bytesPerSample;
        if (byteBuffer.Length < bytesRequired)
        {
            byteBuffer = new byte[bytesRequired];
        }

        var bytesRead = stream.Read(byteBuffer, 0, bytesRequired);
        bytesRead = (int)SourceRecorder.AlignBytesToBlock(bytesRead, sourceFormat.BlockAlign);
        var samplesRead = bytesRead / bytesPerSample;

        for (var sample = 0; sample < samplesRead; sample++)
        {
            buffer[offset + sample] = ConvertSample(byteBuffer, sample * bytesPerSample);
        }

        return samplesRead;
    }

    private float ConvertSample(byte[] bytes, int offset)
    {
        return sourceFormat.Encoding switch
        {
            WaveFormatEncoding.Pcm => ConvertPcmSample(bytes, offset),
            WaveFormatEncoding.IeeeFloat when sourceFormat.BitsPerSample == 32 => BitConverter.ToSingle(bytes, offset),
            _ => throw new NotSupportedException($"Unsupported source WAV encoding: {sourceFormat.Encoding}.")
        };
    }

    private float ConvertPcmSample(byte[] bytes, int offset)
    {
        return sourceFormat.BitsPerSample switch
        {
            8 => (bytes[offset] - 128) / 128f,
            16 => BitConverter.ToInt16(bytes, offset) / 32768f,
            24 => Convert24BitPcm(bytes, offset) / 8_388_608f,
            32 => BitConverter.ToInt32(bytes, offset) / 2_147_483_648f,
            _ => throw new NotSupportedException($"Unsupported PCM bit depth: {sourceFormat.BitsPerSample}.")
        };
    }

    private static int Convert24BitPcm(byte[] bytes, int offset)
    {
        var value = bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
        if ((value & 0x800000) != 0)
        {
            value |= unchecked((int)0xFF000000);
        }

        return value;
    }
}

/// <summary>
/// Converts mono, stereo, and multi-channel sample providers to stereo.
/// </summary>
public sealed class StereoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly int sourceChannels;
    private float[] sourceBuffer = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="StereoSampleProvider"/> class.
    /// </summary>
    public StereoSampleProvider(ISampleProvider source)
    {
        this.source = source;
        sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// Returns the original provider when it is already stereo; otherwise wraps it.
    /// </summary>
    public static ISampleProvider EnsureStereo(ISampleProvider source)
    {
        return source.WaveFormat.Channels == 2
            ? source
            : new StereoSampleProvider(source);
    }

    /// <summary>
    /// Converts one input frame to a stereo frame.
    /// </summary>
    public static (float Left, float Right) ConvertFrameToStereo(ReadOnlySpan<float> frame)
    {
        if (frame.Length == 0)
        {
            return (0, 0);
        }

        if (frame.Length == 1)
        {
            return (frame[0], frame[0]);
        }

        if (frame.Length == 2)
        {
            return (frame[0], frame[1]);
        }

        var left = 0f;
        var right = 0f;
        var leftCount = 0;
        var rightCount = 0;
        for (var i = 0; i < frame.Length; i++)
        {
            if (i % 2 == 0)
            {
                left += frame[i];
                leftCount++;
            }
            else
            {
                right += frame[i];
                rightCount++;
            }
        }

        return (left / leftCount, right / rightCount);
    }

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        var framesRequested = count / 2;
        if (framesRequested <= 0)
        {
            return 0;
        }

        var sourceSamplesRequested = framesRequested * sourceChannels;
        if (sourceBuffer.Length < sourceSamplesRequested)
        {
            sourceBuffer = new float[sourceSamplesRequested];
        }

        var sourceSamplesRead = source.Read(sourceBuffer, 0, sourceSamplesRequested);
        var framesRead = sourceSamplesRead / sourceChannels;
        for (var frame = 0; frame < framesRead; frame++)
        {
            var sourceOffset = frame * sourceChannels;
            var converted = ConvertFrameToStereo(sourceBuffer.AsSpan(sourceOffset, sourceChannels));
            buffer[offset + (frame * 2)] = converted.Left;
            buffer[offset + (frame * 2) + 1] = converted.Right;
        }

        return framesRead * 2;
    }
}

internal sealed class GainSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly float gain;

    public GainSampleProvider(ISampleProvider source, double gain)
    {
        this.source = source;
        this.gain = (float)gain;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        for (var i = 0; i < read; i++)
        {
            buffer[offset + i] *= gain;
        }

        return read;
    }
}

internal sealed class ByteLimitedWaveProvider : IWaveProvider
{
    private readonly IWaveProvider source;
    private long remainingBytes;

    public ByteLimitedWaveProvider(IWaveProvider source, long byteLimit)
    {
        this.source = source;
        remainingBytes = Math.Max(0, byteLimit);
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public bool HitSourceEnd { get; private set; }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (remainingBytes <= 0)
        {
            return 0;
        }

        var requested = (int)Math.Min(count, remainingBytes);
        requested = (int)SourceRecorder.AlignBytesToBlock(requested, source.WaveFormat.BlockAlign);
        if (requested <= 0)
        {
            return 0;
        }

        var read = source.Read(buffer, offset, requested);
        if (read == 0)
        {
            HitSourceEnd = true;
            return 0;
        }

        remainingBytes -= read;
        return read;
    }
}

internal sealed class CountingWaveProvider : IWaveProvider
{
    private readonly IWaveProvider source;

    public CountingWaveProvider(IWaveProvider source)
    {
        this.source = source;
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public long BytesRead { get; private set; }

    public bool HitSourceEnd { get; private set; }

    public int Read(byte[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (read == 0)
        {
            HitSourceEnd = true;
            return 0;
        }

        BytesRead += read;
        if (source is ByteLimitedWaveProvider limited && limited.HitSourceEnd)
        {
            HitSourceEnd = true;
        }

        return read;
    }
}
