using System.Diagnostics;
using TapeDeck;

namespace TapeDeck.App;

/// <summary>
/// Minimal Windows Forms front end for the TapeDeck recorder.
/// </summary>
public sealed class MainForm : Form
{
    private readonly ComboBox formatComboBox = new();
    private readonly NumericUpDown bitrateInput = new();
    private readonly CheckBox systemAudioCheckBox = new();
    private readonly CheckBox microphoneCheckBox = new();
    private readonly CheckBox transcriptCheckBox = new();
    private readonly Button recordButton = new();
    private readonly Button stopButton = new();
    private readonly Label statusLabel = new();
    private readonly Label elapsedLabel = new();
    private readonly System.Windows.Forms.Timer elapsedTimer = new();
    private readonly Stopwatch elapsedStopwatch = new();

    private CancellationTokenSource? recordingCancellation;
    private Task? recordingTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainForm"/> class.
    /// </summary>
    public MainForm()
    {
        Text = "TapeDeck";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(380, 255);

        BuildLayout();
        WireEvents();
        UpdateFormatControls();
        SetRecordingState(false);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 8
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var formatLabel = new Label
        {
            AutoSize = true,
            Text = "Format",
            Anchor = AnchorStyles.Left
        };

        formatComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        formatComboBox.Items.Add(new FormatItem("M4A", OutputFormat.M4A));
        formatComboBox.Items.Add(new FormatItem("WAV", OutputFormat.Wav));
        formatComboBox.SelectedIndex = 0;
        formatComboBox.Dock = DockStyle.Fill;

        var bitrateLabel = new Label
        {
            AutoSize = true,
            Text = "Bitrate",
            Anchor = AnchorStyles.Left
        };

        bitrateInput.Minimum = 64_000;
        bitrateInput.Maximum = 320_000;
        bitrateInput.Increment = 32_000;
        bitrateInput.Value = 128_000;
        bitrateInput.Dock = DockStyle.Fill;

        systemAudioCheckBox.Text = "System audio";
        systemAudioCheckBox.Checked = true;
        systemAudioCheckBox.AutoSize = true;
        systemAudioCheckBox.Anchor = AnchorStyles.Left;

        microphoneCheckBox.Text = "Microphone";
        microphoneCheckBox.Checked = true;
        microphoneCheckBox.AutoSize = true;
        microphoneCheckBox.Anchor = AnchorStyles.Left;

        transcriptCheckBox.Text = "Transcript";
        transcriptCheckBox.Checked = false;
        transcriptCheckBox.AutoSize = true;
        transcriptCheckBox.Anchor = AnchorStyles.Left;

        recordButton.Text = "Record";
        recordButton.Dock = DockStyle.Fill;
        recordButton.Height = 34;

        stopButton.Text = "Stop";
        stopButton.Dock = DockStyle.Fill;
        stopButton.Height = 34;

        elapsedLabel.Text = "00:00:00";
        elapsedLabel.AutoSize = true;
        elapsedLabel.Anchor = AnchorStyles.Left;

        statusLabel.Text = "Ready";
        statusLabel.AutoEllipsis = true;
        statusLabel.Dock = DockStyle.Fill;

        root.Controls.Add(formatLabel, 0, 0);
        root.Controls.Add(formatComboBox, 1, 0);
        root.Controls.Add(bitrateLabel, 0, 1);
        root.Controls.Add(bitrateInput, 1, 1);
        root.Controls.Add(systemAudioCheckBox, 1, 2);
        root.Controls.Add(microphoneCheckBox, 1, 3);
        root.Controls.Add(transcriptCheckBox, 1, 4);
        root.Controls.Add(recordButton, 0, 5);
        root.Controls.Add(stopButton, 1, 5);
        root.Controls.Add(new Label { AutoSize = true, Text = "Elapsed", Anchor = AnchorStyles.Left }, 0, 6);
        root.Controls.Add(elapsedLabel, 1, 6);
        root.Controls.Add(statusLabel, 0, 7);
        root.SetColumnSpan(statusLabel, 2);

        Controls.Add(root);
    }

    private void WireEvents()
    {
        formatComboBox.SelectedIndexChanged += (_, _) => UpdateFormatControls();
        recordButton.Click += async (_, _) => await StartRecordingAsync().ConfigureAwait(true);
        stopButton.Click += (_, _) => StopRecording();
        elapsedTimer.Interval = 1000;
        elapsedTimer.Tick += (_, _) => elapsedLabel.Text = elapsedStopwatch.Elapsed.ToString(@"hh\:mm\:ss");
        FormClosing += MainForm_FormClosing;
    }

    private async Task StartRecordingAsync()
    {
        if (recordingTask is not null && !recordingTask.IsCompleted)
        {
            return;
        }

        if (!systemAudioCheckBox.Checked && !microphoneCheckBox.Checked)
        {
            statusLabel.Text = "Select at least one source.";
            return;
        }

        var format = SelectedFormat;
        var temporaryPath = CreateTemporaryOutputPath(format);
        var options = new RecordingOptions
        {
            OutputPath = temporaryPath,
            Format = format,
            AudioBitrate = (int)bitrateInput.Value,
            RecordSystem = systemAudioCheckBox.Checked,
            RecordMicrophone = microphoneCheckBox.Checked,
            Overwrite = true,
            Transcribe = transcriptCheckBox.Checked
        };

        recordingCancellation = new CancellationTokenSource();
        elapsedStopwatch.Restart();
        elapsedTimer.Start();
        SetRecordingState(true);
        statusLabel.Text = "Recording...";

        recordingTask = CompleteRecordingAsync(options, recordingCancellation.Token);
        await recordingTask.ConfigureAwait(true);
    }

    private async Task CompleteRecordingAsync(RecordingOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var recorder = new DualSourceRecorder(new DeviceLister(), new MixdownService(), TextWriter.Null, TextWriter.Null);
            var result = await Task.Run(() => recorder.RecordAsync(options, cancellationToken)).ConfigureAwait(true);

            elapsedTimer.Stop();
            elapsedStopwatch.Stop();
            elapsedLabel.Text = result.Duration.ToString(@"hh\:mm\:ss");

            if (!result.Succeeded)
            {
                if (result.Outputs.Count > 0)
                {
                    var saved = PromptAndMoveOutput(result.Outputs[0].Path, options.Format, result.Transcript?.Path);
                    statusLabel.Text = saved.AudioPath is null
                        ? $"Transcript failed. Audio kept at {result.Outputs[0].Path}"
                        : $"Saved audio. Transcript failed: {result.ErrorMessage}";
                }
                else
                {
                    statusLabel.Text = result.ErrorMessage ?? $"Recording failed: {result.ExitCode}";
                }

                return;
            }

            var savedPath = PromptAndMoveOutput(result.Outputs[0].Path, options.Format, result.Transcript?.Path);
            statusLabel.Text = savedPath.AudioPath is null
                ? $"Save canceled. Kept at {result.Outputs[0].Path}"
                : savedPath.TranscriptPath is null
                    ? $"Saved {savedPath.AudioPath}"
                    : $"Saved {savedPath.AudioPath} and transcript";
        }
        catch (AudioDeviceException ex)
        {
            statusLabel.Text = ex.Message;
        }
        catch (FileOutputException ex)
        {
            statusLabel.Text = ex.Message;
        }
        catch (Exception ex)
        {
            statusLabel.Text = $"Recording failed: {ex.Message}";
        }
        finally
        {
            elapsedTimer.Stop();
            elapsedStopwatch.Stop();
            recordingCancellation?.Dispose();
            recordingCancellation = null;
            SetRecordingState(false);
        }
    }

    private void StopRecording()
    {
        if (recordingCancellation is null || recordingCancellation.IsCancellationRequested)
        {
            return;
        }

        statusLabel.Text = "Stopping...";
        recordingCancellation.Cancel();
    }

    private SavedRecordingPath PromptAndMoveOutput(string temporaryPath, OutputFormat format, string? transcriptPath)
    {
        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = format == OutputFormat.Wav ? "wav" : "m4a",
            FileName = Path.GetFileName(FileNameService.GetDefaultOutputPath(DateTimeOffset.Now, format)),
            Filter = format == OutputFormat.Wav
                ? "WAV audio (*.wav)|*.wav"
                : "M4A audio (*.m4a)|*.m4a",
            InitialDirectory = GetDefaultRecordingDirectory(),
            OverwritePrompt = true,
            Title = "Save recording"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return new SavedRecordingPath(null, null);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dialog.FileName)!);
        File.Move(temporaryPath, dialog.FileName, true);
        string? movedTranscriptPath = null;
        if (!string.IsNullOrWhiteSpace(transcriptPath) && File.Exists(transcriptPath))
        {
            movedTranscriptPath = Path.ChangeExtension(dialog.FileName, ".txt");
            File.Move(transcriptPath, movedTranscriptPath, true);
        }

        return new SavedRecordingPath(dialog.FileName, movedTranscriptPath);
    }

    private void UpdateFormatControls()
    {
        bitrateInput.Enabled = SelectedFormat == OutputFormat.M4A;
    }

    private void SetRecordingState(bool recording)
    {
        recordButton.Enabled = !recording;
        stopButton.Enabled = recording;
        formatComboBox.Enabled = !recording;
        bitrateInput.Enabled = !recording && SelectedFormat == OutputFormat.M4A;
        systemAudioCheckBox.Enabled = !recording;
        microphoneCheckBox.Enabled = !recording;
        transcriptCheckBox.Enabled = !recording;
    }

    private OutputFormat SelectedFormat
    {
        get
        {
            return formatComboBox.SelectedItem is FormatItem item
                ? item.Format
                : OutputFormat.M4A;
        }
    }

    private static string CreateTemporaryOutputPath(OutputFormat format)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TapeDeck");
        Directory.CreateDirectory(directory);
        var extension = format == OutputFormat.Wav ? ".wav" : ".m4a";
        return Path.Combine(directory, $"tapedeck-{DateTimeOffset.Now.LocalDateTime:yyyy-MM-dd_HH-mm-ss}{extension}");
    }

    private static string GetDefaultRecordingDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.CurrentDirectory;
        }

        var directory = Path.Combine(userProfile, "Recordings", "TapeDeck");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (recordingTask is null || recordingTask.IsCompleted)
        {
            return;
        }

        e.Cancel = true;
        StopRecording();
        statusLabel.Text = "Stopping before close...";
        await recordingTask.ConfigureAwait(true);
        Close();
    }

    private sealed record FormatItem(string Label, OutputFormat Format)
    {
        public override string ToString()
        {
            return Label;
        }
    }

    private sealed record SavedRecordingPath(string? AudioPath, string? TranscriptPath);
}
