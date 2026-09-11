using Avalonia.Threading;
using DeskShare.Core.Interfaces;

namespace DeskShare.DesktopAvalonia.Services;

/// <summary>
/// Avalonia implementation of IUIDispatcher.
/// Wraps Avalonia's Dispatcher.UIThread.InvokeAsync to schedule work on the UI thread.
/// Used by shared services like InMemoryLogSink that need to update UI-bound collections.
/// </summary>
public class AvaloniaUIDispatcher : IUIDispatcher
{
    /// <summary>
    /// Schedules the given action to run on Avalonia's UI thread.
    /// </summary>
    public void InvokeAsync(Action action)
    {
        Dispatcher.UIThread.InvokeAsync(action);
    }
}
