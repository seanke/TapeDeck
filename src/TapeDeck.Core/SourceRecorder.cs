using System.Diagnostics;
using NAudio.Wave;

namespace TapeDeck;

/// <summary>
/// Base class for a single WASAPI source that records incrementally to a partial WAV stem.
/// </summary>
public abstract class SourceRecorder : IDisposable
{
    private const int ZeroBufferLength = 64 * 1024;
    private readonly object writerLock = new();
    private readonly byte[] zeroBuffer = new byte[ZeroBufferLength];
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IWaveIn? capture;
    private WaveFileWriter? writer;
    private Stopwatch? stopwatch;
    private long dataBytesWritten;
    private int stopRequested;
    private int finalized;
    private bool started;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceRecorder"/> class.
    /// </summary>
    protected SourceRecorder(string displayName, string partialPath, string stemPath)
    {
        DisplayName = displayName;
        PartialPath = partialPath;
        StemPath = stemPath;
    }

    /// <summary>
    /// Gets the source display name.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the temporary partial WAV path.
    /// </summary>
    public string PartialPath { get; }

    /// <summary>
    /// Gets the finalized source stem path.
    /// </summary>
    public string StemPath { get; }

    /// <summary>
    /// Gets the native source capture format.
    /// </summary>
    public WaveFormat? WaveFormat { get; private set; }

    /// <summary>
    /// Gets the number of source bytes written to the stem.
    /// </summary>
    public long DataBytesWritten
    {
        get
        {
            lock (writerLock)
            {
                return dataBytesWritten;
            }
        }
    }

    /// <summary>
    /// Gets any exception reported by the capture source or source writer.
    /// </summary>
    public Exception? Failure { get; private set; }

    /// <summary>
    /// Creates the concrete NAudio capture object for this source.
    /// </summary>
    protected abstract IWaveIn CreateCapture();

    /// <summary>
    /// Opens the capture source and creates the partial WAV writer.
    /// </summary>
    public void Open()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PartialPath)!);
            capture = CreateCapture();
            WaveFormat = capture.WaveFormat;
            writer = new WaveFileWriter(PartialPath, WaveFormat);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            capture?.Dispose();
            capture = null;
            throw new FileOutputException($"The source partial file could not be opened: {PartialPath}", ex);
        }
    }

    /// <summary>
    /// Starts capture using a shared wall-clock stopwatch.
    /// </summary>
    public void Start(Stopwatch recordingStopwatch)
    {
        if (capture is null || writer is null)
        {
            throw new InvalidOperationException("The source recorder must be opened before it can start.");
        }

        stopwatch = recordingStopwatch;
        capture.StartRecording();
        started = true;
    }

    /// <summary>
    /// Requests the capture source to stop. Multiple calls are safe.
    /// </summary>
    public void RequestStop()
    {
        if (Interlocked.Exchange(ref stopRequested, 1) != 0)
        {
            return;
        }

        if (capture is null || !started)
        {
            stopped.TrySetResult();
            return;
        }

        try
        {
            capture.StopRecording();
        }
        catch (Exception ex)
        {
            Failure ??= ex;
            stopped.TrySetResult();
        }
    }

    /// <summary>
    /// Stops capture, fills final silence to the shared stop time, and renames the partial stem.
    /// </summary>
    public async Task StopAndFinalizeAsync(TimeSpan stopElapsed)
    {
        RequestStop();

        try
        {
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            Failure ??= ex;
        }

        if (Interlocked.Exchange(ref finalized, 1) != 0)
        {
            return;
        }

        lock (writerLock)
        {
            if (writer is not null)
            {
                FillSilenceTo(stopElapsed);
                writer.Dispose();
                writer = null;
            }
        }

        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
            capture = null;
        }

        if (File.Exists(PartialPath))
        {
            try
            {
                File.Move(PartialPath, StemPath, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FileOutputException($"The source stem could not be finalized: {StemPath}", ex);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
            capture = null;
        }

        lock (writerLock)
        {
            writer?.Dispose();
            writer = null;
        }
    }

    /// <summary>
    /// Rounds a byte position down to the nearest valid block boundary.
    /// </summary>
    public static long AlignBytesToBlock(long bytes, int blockAlign)
    {
        if (blockAlign <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockAlign), "Block alignment must be positive.");
        }

        return Math.Max(0, bytes - (bytes % blockAlign));
    }

    /// <summary>
    /// Calculates the aligned byte position expected at the supplied elapsed time.
    /// </summary>
    public static long CalculateExpectedBytes(TimeSpan elapsed, int averageBytesPerSecond, int blockAlign)
    {
        if (averageBytesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(averageBytesPerSecond), "Average bytes per second must be positive.");
        }

        var bytes = (long)Math.Floor(Math.Max(0, elapsed.TotalSeconds) * averageBytesPerSecond);
        return AlignBytesToBlock(bytes, blockAlign);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (writer is null || WaveFormat is null || stopwatch is null)
        {
            return;
        }

        try
        {
            lock (writerLock)
            {
                if (writer is null)
                {
                    return;
                }

                var averageBytesPerSecond = WaveFormat.AverageBytesPerSecond;
                var blockAlign = WaveFormat.BlockAlign;
                var bufferDurationSeconds = e.BytesRecorded / (double)averageBytesPerSecond;
                var bufferStartSeconds = stopwatch.Elapsed.TotalSeconds - bufferDurationSeconds;
                var expectedBytesBeforeBuffer = CalculateExpectedBytes(
                    TimeSpan.FromSeconds(bufferStartSeconds),
                    averageBytesPerSecond,
                    blockAlign);

                if (expectedBytesBeforeBuffer > dataBytesWritten)
                {
                    WriteSilence(expectedBytesBeforeBuffer - dataBytesWritten, blockAlign);
                }

                writer.Write(e.Buffer, 0, e.BytesRecorded);
                dataBytesWritten += e.BytesRecorded;
            }
        }
        catch (Exception ex)
        {
            Failure ??= ex;
            RequestStop();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            Failure ??= e.Exception;
        }

        stopped.TrySetResult();
    }

    private void FillSilenceTo(TimeSpan stopElapsed)
    {
        if (writer is null || WaveFormat is null)
        {
            return;
        }

        var expectedBytesAtStop = CalculateExpectedBytes(
            stopElapsed,
            WaveFormat.AverageBytesPerSecond,
            WaveFormat.BlockAlign);

        if (expectedBytesAtStop > dataBytesWritten)
        {
            WriteSilence(expectedBytesAtStop - dataBytesWritten, WaveFormat.BlockAlign);
        }
    }

    private void WriteSilence(long bytesToWrite, int blockAlign)
    {
        while (bytesToWrite > 0)
        {
            var chunk = (int)Math.Min(bytesToWrite, zeroBuffer.Length);
            chunk = (int)AlignBytesToBlock(chunk, blockAlign);
            if (chunk == 0)
            {
                chunk = blockAlign;
            }

            writer!.Write(zeroBuffer, 0, chunk);
            dataBytesWritten += chunk;
            bytesToWrite -= chunk;
        }
    }
}
