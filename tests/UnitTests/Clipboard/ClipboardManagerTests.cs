using DeskShare.Core.Interfaces;
using Xunit;

namespace DeskShare.UnitTests.Clipboard;

/// <summary>
/// Unit tests for IClipboardManager interface contract.
/// Tests the expected behavior without platform-specific dependencies.
/// </summary>
public class ClipboardManagerTests
{
    [Fact]
    public void StartMonitoring_ShouldSetIsMonitoringTrue()
    {
        // Arrange
        var manager = new FakeClipboardManager();

        // Act
        manager.StartMonitoring();

        // Assert
        Assert.True(manager.IsMonitoring);
    }

    [Fact]
    public void StopMonitoring_ShouldSetIsMonitoringFalse()
    {
        // Arrange
        var manager = new FakeClipboardManager();
        manager.StartMonitoring();

        // Act
        manager.StopMonitoring();

        // Assert
        Assert.False(manager.IsMonitoring);
    }

    [Fact]
    public void ClipboardChanged_ShouldFireWhenContentChanges()
    {
        // Arrange
        var manager = new FakeClipboardManager();
        ClipboardChangedEventArgs? receivedEventArgs = null;
        manager.ClipboardChanged += (sender, e) => receivedEventArgs = e;
        manager.StartMonitoring();

        // Act
        manager.SimulateClipboardChange("Hello World");

        // Assert
        Assert.NotNull(receivedEventArgs);
        Assert.Equal("Hello World", receivedEventArgs!.Text);
    }

    [Fact]
    public void ClipboardChanged_ShouldNotFireWhenNotMonitoring()
    {
        // Arrange
        var manager = new FakeClipboardManager();
        ClipboardChangedEventArgs? receivedEventArgs = null;
        manager.ClipboardChanged += (sender, e) => receivedEventArgs = e;

        // Act - Don't start monitoring
        manager.SimulateClipboardChange("Hello World");

        // Assert - Event should not fire when not monitoring
        Assert.Null(receivedEventArgs);
    }

    [Fact]
    public async Task GetTextAsync_ShouldReturnCurrentText()
    {
        // Arrange
        var manager = new FakeClipboardManager();
        await manager.SetTextAsync("Test Text");

        // Act
        var result = await manager.GetTextAsync();

        // Assert
        Assert.Equal("Test Text", result);
    }

    [Fact]
    public async Task SetTextAsync_ShouldUpdateClipboard()
    {
        // Arrange
        var manager = new FakeClipboardManager();

        // Act
        var result = await manager.SetTextAsync("New Text");
        var clipboardText = await manager.GetTextAsync();

        // Assert
        Assert.True(result);
        Assert.Equal("New Text", clipboardText);
    }

    [Fact]
    public void Dispose_ShouldStopMonitoring()
    {
        // Arrange
        var manager = new FakeClipboardManager();
        manager.StartMonitoring();

        // Act
        manager.Dispose();

        // Assert
        Assert.False(manager.IsMonitoring);
    }
}

/// <summary>
/// Fake implementation of IClipboardManager for testing.
/// Simulates clipboard behavior without accessing actual system clipboard.
/// </summary>
internal class FakeClipboardManager : IClipboardManager
{
    private bool _isMonitoring;
    private string? _currentClipboardText;

    public bool IsMonitoring => _isMonitoring;

    public event EventHandler<ClipboardChangedEventArgs>? ClipboardChanged;

    public void StartMonitoring()
    {
        _isMonitoring = true;
    }

    public void StopMonitoring()
    {
        _isMonitoring = false;
    }

    public Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_currentClipboardText);
    }

    public Task<bool> SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        _currentClipboardText = text;
        return Task.FromResult(true);
    }

    public void Dispose()
    {
        StopMonitoring();
    }

    /// <summary>
    /// Test helper: Simulates clipboard content change.
    /// </summary>
    public void SimulateClipboardChange(string text)
    {
        if (!_isMonitoring)
            return;

        _currentClipboardText = text;

        var eventArgs = new ClipboardChangedEventArgs
        {
            Text = text,
            Timestamp = DateTime.UtcNow
        };

        ClipboardChanged?.Invoke(this, eventArgs);
    }
}
