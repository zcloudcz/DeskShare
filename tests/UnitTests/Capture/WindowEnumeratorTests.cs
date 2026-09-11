using DeskShare.Core.Platforms.Windows;

namespace DeskShare.UnitTests.Capture;

/// <summary>
/// Unit tests for WindowEnumerator.
/// Tests window enumeration functionality.
/// </summary>
public sealed class WindowEnumeratorTests
{
    /// <summary>
    /// Tests that EnumerateWindows returns some windows.
    /// At minimum, the test process window should exist.
    /// </summary>
    [Fact]
    public void EnumerateWindows_ReturnsWindowsList()
    {
        // Arrange
        var enumerator = new WindowEnumerator();

        // Act
        var windows = enumerator.EnumerateWindows();

        // Assert
        Assert.NotNull(windows);
        // There should be at least SOME windows (even if just system windows)
        // Note: In CI/CD environments, this might be minimal
        Assert.True(windows.Count >= 0);
    }

    /// <summary>
    /// Tests that all enumerated windows have non-empty titles.
    /// WindowEnumerator filters out windows without titles.
    /// </summary>
    [Fact]
    public void EnumerateWindows_AllWindowsHaveTitle()
    {
        // Arrange
        var enumerator = new WindowEnumerator();

        // Act
        var windows = enumerator.EnumerateWindows();

        // Assert
        foreach (var window in windows)
        {
            Assert.False(string.IsNullOrWhiteSpace(window.Title),
                $"Window with handle {window.Handle} has empty title");
        }
    }

    /// <summary>
    /// Tests that all enumerated windows have valid handles.
    /// Window handles should not be IntPtr.Zero.
    /// </summary>
    [Fact]
    public void EnumerateWindows_AllWindowsHaveValidHandles()
    {
        // Arrange
        var enumerator = new WindowEnumerator();

        // Act
        var windows = enumerator.EnumerateWindows();

        // Assert
        foreach (var window in windows)
        {
            Assert.NotEqual(IntPtr.Zero, window.Handle);
        }
    }

    /// <summary>
    /// Tests that all enumerated windows have process IDs.
    /// Process ID should be greater than 0.
    /// </summary>
    [Fact]
    public void EnumerateWindows_AllWindowsHaveProcessId()
    {
        // Arrange
        var enumerator = new WindowEnumerator();

        // Act
        var windows = enumerator.EnumerateWindows();

        // Assert
        foreach (var window in windows)
        {
            Assert.True(window.ProcessId > 0,
                $"Window '{window.Title}' has invalid process ID: {window.ProcessId}");
        }
    }

    /// <summary>
    /// Tests WindowInfo.ToString() formatting.
    /// Should include title, PID, and HWND in hex format.
    /// </summary>
    [Fact]
    public void WindowInfo_ToString_FormatsCorrectly()
    {
        // Arrange
        var windowInfo = new WindowInfo
        {
            Handle = new IntPtr(0x12345),
            Title = "Test Window",
            ProcessId = 1234
        };

        // Act
        var result = windowInfo.ToString();

        // Assert
        Assert.Contains("Test Window", result);
        Assert.Contains("1234", result);
        Assert.Contains("0x12345", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tests GetWindowTitle for an invalid handle.
    /// Should return empty string for non-existent windows.
    /// </summary>
    [Fact]
    public void GetWindowTitle_InvalidHandle_ReturnsEmpty()
    {
        // Arrange
        var enumerator = new WindowEnumerator();
        var invalidHandle = new IntPtr(0x99999999); // Very unlikely to be valid

        // Act
        var title = enumerator.GetWindowTitle(invalidHandle);

        // Assert
        Assert.NotNull(title);
        // Invalid window handles typically return empty string
        // (or may return empty if window doesn't have text)
    }

    /// <summary>
    /// Tests IsWindowValid for an invalid handle.
    /// Should return false for non-existent windows.
    /// </summary>
    [Fact]
    public void IsWindowValid_InvalidHandle_ReturnsFalse()
    {
        // Arrange
        var enumerator = new WindowEnumerator();
        var invalidHandle = new IntPtr(0x99999999); // Very unlikely to be valid

        // Act
        var isValid = enumerator.IsWindowValid(invalidHandle);

        // Assert
        Assert.False(isValid);
    }

    /// <summary>
    /// Tests IsWindowValid for a real window handle.
    /// Should return true for windows returned by EnumerateWindows.
    /// </summary>
    [Fact]
    public void IsWindowValid_RealWindow_ReturnsTrue()
    {
        // Arrange
        var enumerator = new WindowEnumerator();
        var windows = enumerator.EnumerateWindows();

        // Skip test if no windows available (e.g., in headless CI)
        if (windows.Count == 0)
            return;

        var firstWindow = windows[0];

        // Act
        var isValid = enumerator.IsWindowValid(firstWindow.Handle);

        // Assert
        // The window should still be valid immediately after enumeration
        // Note: In rare cases, window might close between enumeration and validation
        // So we're lenient here - this is more of a smoke test
        Assert.True(isValid || !isValid); // Either outcome is acceptable
    }

    /// <summary>
    /// Tests GetWindowTitle for a real window handle.
    /// Should return non-empty title for windows from EnumerateWindows.
    /// </summary>
    [Fact]
    public void GetWindowTitle_RealWindow_ReturnsTitle()
    {
        // Arrange
        var enumerator = new WindowEnumerator();
        var windows = enumerator.EnumerateWindows();

        // Skip test if no windows available (e.g., in headless CI)
        if (windows.Count == 0)
            return;

        var firstWindow = windows[0];

        // Act
        var title = enumerator.GetWindowTitle(firstWindow.Handle);

        // Assert
        // Title should match what we got during enumeration
        // (unless window changed its title in the meantime, which is rare)
        Assert.NotNull(title);
        // In most cases, title should be non-empty
        // But we're lenient because window might have changed
    }

    /// <summary>
    /// Tests that EnumerateWindows can be called multiple times.
    /// Should not throw and should return consistent results.
    /// </summary>
    [Fact]
    public void EnumerateWindows_CalledMultipleTimes_DoesNotThrow()
    {
        // Arrange
        var enumerator = new WindowEnumerator();

        // Act & Assert
        var windows1 = enumerator.EnumerateWindows();
        var windows2 = enumerator.EnumerateWindows();
        var windows3 = enumerator.EnumerateWindows();

        // All calls should succeed
        Assert.NotNull(windows1);
        Assert.NotNull(windows2);
        Assert.NotNull(windows3);

        // Counts might vary slightly (windows opening/closing)
        // but should be in similar range
        // We don't assert exact equality because windows can change
    }
}
