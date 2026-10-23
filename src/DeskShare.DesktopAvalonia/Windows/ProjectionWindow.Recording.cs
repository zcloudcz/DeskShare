using System.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeskShare.Desktop.Shared.Services.Recording;
using Microsoft.Extensions.Logging;

namespace DeskShare.DesktopAvalonia.Windows;

/// <summary>
/// The "Record" button of the viewer: encodes the decoded frames to a WebM file through <see cref="SessionRecorder"/> and tells the
/// host ("recording_state") so its banner says "(recording)". The notice is honesty towards the person at the host, not a
/// protection: a viewer decodes the video itself, so recording cannot be prevented.
/// A partial class file of its own so the window's main file only gets a few one-line hooks.
/// </summary>
public partial class ProjectionWindow
{
    private volatile SessionRecorder? _recorder;   // read by the decode thread in OnFrameReceived
    private DateTime _recordingStartedUtc;
    private DispatcherTimer? _recTimer;
    private string? _lastRecordingFolder;

    /// <summary>Called from the decode thread for every frame. Never blocks (the recorder drops frames when it is behind).</summary>
    private void OfferFrameToRecorder(byte[] bgra, int width, int height) => _recorder?.TryAddFrame(bgra, width, height);

    private async void Record_Click(object? sender, RoutedEventArgs e)
    {
        if (_recorder != null)
        {
            await StopRecordingAsync();
            return;
        }

        // Tell the host first: when that is not possible (not connected/verified), do not record behind its back.
        if (!_clientManager.SendRecordingState(true))
        {
            RecordButton.IsChecked = false;
            ShowRecordingMessage("Could not tell the host about the recording, so it was not started.");
            return;
        }

        try
        {
            _lastRecordingFolder = RecordingFiles.DefaultFolder();
            var recorder = new SessionRecorder(_lastRecordingFolder, _serverId, _logger);
            recorder.Failed += ex => Dispatcher.UIThread.Post(async () =>
            {
                if (!ReferenceEquals(_recorder, recorder)) return;
                await StopRecordingAsync();
                ShowRecordingMessage("Recording stopped: " + ex.Message);
            });
            recorder.Start();
            _recorder = recorder;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not start the recording");
            _clientManager.SendRecordingState(false);
            RecordButton.IsChecked = false;
            ShowRecordingMessage("Could not start the recording: " + ex.Message);
            return;
        }

        _recordingStartedUtc = DateTime.UtcNow;
        RecordingSavedBar.IsVisible = false;
        RecordButton.Content = "Stop recording";
        RecIndicator.IsVisible = true;
        UpdateRecIndicator();
        _recTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _recTimer.Tick += (_, _) => UpdateRecIndicator();
        _recTimer.Start();
    }

    private void UpdateRecIndicator()
    {
        var s = (int)(DateTime.UtcNow - _recordingStartedUtc).TotalSeconds;
        RecIndicator.Text = $"● REC {s / 60:00}:{s % 60:00}";
    }

    /// <summary>Stops and saves. Safe to call anytime (disconnect, a new connection, closing): does nothing when not recording.</summary>
    private async Task StopRecordingAsync()
    {
        var recorder = Interlocked.Exchange(ref _recorder, null);
        if (recorder == null) return;

        _recTimer?.Stop();
        _recTimer = null;
        RecIndicator.IsVisible = false;
        RecordButton.IsChecked = false;
        RecordButton.Content = "Record";
        _clientManager.SendRecordingState(false); // a no-op when the connection is already gone: the host clears the flag itself

        // Stop() waits for the encoder thread to write the rest of the queue and the file trailer: not on the UI thread.
        var files = await Task.Run(recorder.Stop);
        recorder.Dispose();
        if (files.Count == 0) return;

        _lastRecordingFolder = Path.GetDirectoryName(files[0]);
        ShowRecordingMessage(
            files.Count == 1 ? $"Saved to {files[0]}" : $"Saved {files.Count} files to {_lastRecordingFolder} (the screen size changed during the recording)");
    }

    private void ShowRecordingMessage(string text)
    {
        RecordingSavedText.Text = text;
        RecordingSavedBar.IsVisible = true;
    }

    private void OpenRecordingFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastRecordingFolder == null) return;
        try
        {
            // UseShellExecute opens the folder in Explorer; the same call the main window uses for received files.
            Process.Start(new ProcessStartInfo(_lastRecordingFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the recordings folder");
        }
    }

    private void CloseRecordingSaved_Click(object? sender, RoutedEventArgs e) => RecordingSavedBar.IsVisible = false;
}
