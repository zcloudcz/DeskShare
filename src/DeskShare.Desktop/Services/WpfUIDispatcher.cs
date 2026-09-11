using System.Windows;
using DeskShare.Core.Interfaces;

namespace DeskShare.Desktop.Services;

/// <summary>
/// WPF implementation of IUIDispatcher.
/// Wraps WPF's Application.Current.Dispatcher.BeginInvoke to schedule work on the UI thread.
/// Used by shared services like InMemoryLogSink that need to update UI-bound collections.
/// </summary>
public class WpfUIDispatcher : IUIDispatcher
{
    /// <summary>
    /// Schedules the given action to run on WPF's UI thread.
    /// </summary>
    public void InvokeAsync(Action action)
    {
        Application.Current?.Dispatcher.BeginInvoke(action);
    }
}
