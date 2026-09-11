using DeskShare.Core.Quality;

namespace DeskShare.UnitTests.Quality;

/// <summary>
/// Unit tests for AdaptiveBitrateController.
/// Tests adaptive quality switching based on network conditions.
/// </summary>
public sealed class AdaptiveBitrateControllerTests
{
    /// <summary>
    /// Tests that controller initializes with Medium quality by default.
    /// This is the sensible default for unknown network conditions.
    /// </summary>
    [Fact]
    public void Constructor_InitializesWithMediumQuality()
    {
        // Arrange & Act
        var controller = new AdaptiveBitrateController();

        // Assert
        Assert.Equal(QualityLevel.Medium, controller.CurrentQuality);
    }

    /// <summary>
    /// Tests that high packet loss triggers downgrade to Low quality.
    /// When packet loss exceeds 5%, quality should drop to Low.
    /// </summary>
    [Fact]
    public void UpdateMetrics_HighPacketLoss_DowngradesToLow()
    {
        // Arrange
        var controller = new AdaptiveBitrateController();
        var metrics = new NetworkMetrics
        {
            PacketLossPercent = 6.0, // High packet loss (>5%)
            RttMilliseconds = 50      // Good RTT
        };

        // Act - Update metrics 10 times to fill history
        bool qualityChanged = false;
        for (int i = 0; i < 10; i++)
        {
            if (controller.UpdateMetrics(metrics))
            {
                qualityChanged = true;
            }
            Thread.Sleep(600); // Wait to avoid hysteresis
        }

        // Assert
        Assert.True(qualityChanged);
        Assert.Equal(QualityLevel.Low, controller.CurrentQuality);
    }

    /// <summary>
    /// Tests that high RTT triggers downgrade to Low quality.
    /// When RTT exceeds 200ms, quality should drop to Low.
    /// </summary>
    [Fact]
    public void UpdateMetrics_HighRtt_DowngradesToLow()
    {
        // Arrange
        var controller = new AdaptiveBitrateController();
        var metrics = new NetworkMetrics
        {
            PacketLossPercent = 0.5, // Good packet loss
            RttMilliseconds = 250     // High RTT (>200ms)
        };

        // Act - Update metrics 10 times to fill history
        bool qualityChanged = false;
        for (int i = 0; i < 10; i++)
        {
            if (controller.UpdateMetrics(metrics))
            {
                qualityChanged = true;
            }
            Thread.Sleep(600); // Wait to avoid hysteresis
        }

        // Assert
        Assert.True(qualityChanged);
        Assert.Equal(QualityLevel.Low, controller.CurrentQuality);
    }

    /// <summary>
    /// Tests that medium packet loss maintains Medium quality.
    /// Packet loss between 2-5% should keep Medium quality.
    /// </summary>
    [Fact]
    public void UpdateMetrics_MediumPacketLoss_StaysAtMedium()
    {
        // Arrange
        var controller = new AdaptiveBitrateController();
        var metrics = new NetworkMetrics
        {
            PacketLossPercent = 3.0, // Medium packet loss (2-5%)
            RttMilliseconds = 50      // Good RTT
        };

        // Act - Update metrics 10 times
        for (int i = 0; i < 10; i++)
        {
            controller.UpdateMetrics(metrics);
            Thread.Sleep(600);
        }

        // Assert
        Assert.Equal(QualityLevel.Medium, controller.CurrentQuality);
    }

    /// <summary>
    /// Tests that excellent conditions upgrade to High quality.
    /// Low packet loss (<2%) and low RTT (<100ms) should upgrade to High.
    /// </summary>
    [Fact]
    public void UpdateMetrics_ExcellentConditions_UpgradesToHigh()
    {
        // Arrange
        var controller = new AdaptiveBitrateController();
        var metrics = new NetworkMetrics
        {
            PacketLossPercent = 0.5, // Excellent packet loss
            RttMilliseconds = 30      // Excellent RTT
        };

        // Act - Update metrics 10 times
        bool qualityChanged = false;
        for (int i = 0; i < 10; i++)
        {
            if (controller.UpdateMetrics(metrics))
            {
                qualityChanged = true;
            }
            Thread.Sleep(600);
        }

        // Assert
        Assert.True(qualityChanged);
        Assert.Equal(QualityLevel.High, controller.CurrentQuality);
    }

    /// <summary>
    /// Tests hysteresis: quality doesn't change too frequently.
    /// Multiple rapid updates should not cause rapid quality switching.
    /// </summary>
    [Fact]
    public void UpdateMetrics_RapidUpdates_RespectsHysteresis()
    {
        // Arrange
        var controller = new AdaptiveBitrateController();
        var goodMetrics = new NetworkMetrics
        {
            PacketLossPercent = 0.5,
            RttMilliseconds = 30
        };

        // Act - Update 5 times rapidly (no delay)
        int changeCount = 0;
        for (int i = 0; i < 5; i++)
        {
            if (controller.UpdateMetrics(goodMetrics))
            {
                changeCount++;
            }
        }

        // Assert - Should change at most once (hysteresis prevents rapid changes)
        Assert.True(changeCount <= 1);
    }

    /// <summary>
    /// Tests that UpdateMetrics with null throws ArgumentNullException.
    /// Proper input validation is important.
    /// </summary>
    [Fact]
    public void UpdateMetrics_NullMetrics_ThrowsArgumentNullException()
    {
        // Arrange
        var controller = new AdaptiveBitrateController();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => controller.UpdateMetrics(null!));
    }

    /// <summary>
    /// Tests manual quality setting.
    /// User should be able to override automatic quality control.
    /// </summary>
    [Fact]
    public void SetManualQuality_ChangesQualityImmediately()
    {
        // Arrange
        var controller = new AdaptiveBitrateController();
        Assert.Equal(QualityLevel.Medium, controller.CurrentQuality);

        // Act
        controller.SetManualQuality(QualityLevel.Low);

        // Assert
        Assert.Equal(QualityLevel.Low, controller.CurrentQuality);
    }

    /// <summary>
    /// Tests that SetManualQuality raises QualityChanged event.
    /// Event subscribers should be notified of quality changes.
    /// </summary>
    [Fact]
    public void SetManualQuality_RaisesQualityChangedEvent()
    {
        // Arrange
        var controller = new AdaptiveBitrateController();
        QualityChangedEventArgs? eventArgs = null;
        controller.QualityChanged += (sender, args) => eventArgs = args;

        // Act
        controller.SetManualQuality(QualityLevel.High);

        // Assert
        Assert.NotNull(eventArgs);
        Assert.Equal(QualityLevel.Medium, eventArgs.OldQuality);
        Assert.Equal(QualityLevel.High, eventArgs.NewQuality);
    }

    /// <summary>
    /// Tests GetCurrentSettings returns correct settings for Low quality.
    /// Should return 720p @ 15fps for Low.
    /// </summary>
    [Fact]
    public void GetCurrentSettings_LowQuality_ReturnsCorrectSettings()
    {
        // Arrange
        var controller = new AdaptiveBitrateController();
        controller.SetManualQuality(QualityLevel.Low);

        // Act
        var settings = controller.GetCurrentSettings();

        // Assert
        Assert.Equal(1280, settings.Width);
        Assert.Equal(720, settings.Height);
        Assert.Equal(15, settings.FrameRate);
        Assert.NotEmpty(settings.Description);
    }

    /// <summary>
    /// Tests GetCurrentSettings returns correct settings for Medium quality.
    /// Should return 1080p @ 30fps for Medium.
    /// </summary>
    [Fact]
    public void GetCurrentSettings_MediumQuality_ReturnsCorrectSettings()
    {
        // Arrange
        var controller = new AdaptiveBitrateController();
        // Default is Medium

        // Act
        var settings = controller.GetCurrentSettings();

        // Assert
        Assert.Equal(1920, settings.Width);
        Assert.Equal(1080, settings.Height);
        Assert.Equal(30, settings.FrameRate);
        Assert.NotEmpty(settings.Description);
    }

    /// <summary>
    /// Tests GetCurrentSettings returns correct settings for High quality.
    /// Should return 1080p @ 60fps for High.
    /// </summary>
    [Fact]
    public void GetCurrentSettings_HighQuality_ReturnsCorrectSettings()
    {
        // Arrange
        var controller = new AdaptiveBitrateController();
        controller.SetManualQuality(QualityLevel.High);

        // Act
        var settings = controller.GetCurrentSettings();

        // Assert
        Assert.Equal(1920, settings.Width);
        Assert.Equal(1080, settings.Height);
        Assert.Equal(60, settings.FrameRate);
        Assert.NotEmpty(settings.Description);
    }

    /// <summary>
    /// Tests Reset clears state and returns to Medium quality.
    /// Reset should clear metrics history and quality level.
    /// </summary>
    [Fact]
    public void Reset_ClearsStateAndReturnsToMedium()
    {
        // Arrange
        var controller = new AdaptiveBitrateController();
        controller.SetManualQuality(QualityLevel.Low);
        Assert.Equal(QualityLevel.Low, controller.CurrentQuality);

        // Act
        controller.Reset();

        // Assert
        Assert.Equal(QualityLevel.Medium, controller.CurrentQuality);
    }

    /// <summary>
    /// Tests that metrics history smooths decisions.
    /// Single bad metric shouldn't immediately change quality.
    /// </summary>
    [Fact]
    public void UpdateMetrics_SingleBadMetric_DoesNotImmediatelyDowngrade()
    {
        // Arrange
        var controller = new AdaptiveBitrateController();

        // First, fill history with good metrics
        var goodMetrics = new NetworkMetrics
        {
            PacketLossPercent = 0.5,
            RttMilliseconds = 30
        };

        for (int i = 0; i < 10; i++)
        {
            controller.UpdateMetrics(goodMetrics);
            Thread.Sleep(600);
        }

        // Now send one bad metric
        var badMetrics = new NetworkMetrics
        {
            PacketLossPercent = 10.0, // Very bad
            RttMilliseconds = 500      // Very bad
        };

        // Act
        controller.UpdateMetrics(badMetrics);

        // Assert - Should still be at High (history averages it out)
        Assert.Equal(QualityLevel.High, controller.CurrentQuality);
    }

    /// <summary>
    /// Tests QualityChanged event is raised when quality changes.
    /// Event should provide old and new quality levels.
    /// </summary>
    [Fact]
    public void UpdateMetrics_QualityChanges_RaisesEvent()
    {
        // Arrange
        var controller = new AdaptiveBitrateController();
        QualityChangedEventArgs? eventArgs = null;
        controller.QualityChanged += (sender, args) => eventArgs = args;

        var badMetrics = new NetworkMetrics
        {
            PacketLossPercent = 10.0,
            RttMilliseconds = 500
        };

        // Act - Send bad metrics to trigger downgrade
        for (int i = 0; i < 10; i++)
        {
            controller.UpdateMetrics(badMetrics);
            Thread.Sleep(600);
        }

        // Assert
        Assert.NotNull(eventArgs);
        Assert.Equal(QualityLevel.Medium, eventArgs.OldQuality);
        Assert.Equal(QualityLevel.Low, eventArgs.NewQuality);
    }
}
