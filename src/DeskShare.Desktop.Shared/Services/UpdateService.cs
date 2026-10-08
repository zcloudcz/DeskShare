using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>
/// Velopack auto-update shared by the WPF and Avalonia apps. Downloads a newer GitHub release in the
/// background and installs it when the app exits, so an update never interrupts a running session.
/// Velopack picks the asset for the current OS/arch channel (win, osx-arm64, linux-x64) by itself.
/// </summary>
public static class UpdateService
{
    private const string RepoUrl = "https://github.com/zcloudcz/DeskShare";

    /// <summary>Fire-and-forget; only active for installed builds (not portable or dev runs).</summary>
    public static async Task CheckAndStageAsync(ILogger logger)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (!manager.IsInstalled) return;

            var update = await manager.CheckForUpdatesAsync();
            if (update == null) return;

            logger.LogInformation("Update {Version} available, downloading", update.TargetFullRelease.Version);
            await manager.DownloadUpdatesAsync(update);
            manager.WaitExitThenApplyUpdates(update);
            logger.LogInformation("Update {Version} will be applied on exit", update.TargetFullRelease.Version);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update check failed");
        }
    }
}
