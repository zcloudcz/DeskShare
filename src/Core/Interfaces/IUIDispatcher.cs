namespace DeskShare.Core.Interfaces;

/// <summary>
/// Thin abstraction over platform-specific UI dispatchers.
/// WPF implements this with Application.Current.Dispatcher.BeginInvoke,
/// Avalonia implements this with Dispatcher.UIThread.InvokeAsync.
///
/// This allows shared services (like InMemoryLogSink) to dispatch work
/// to the UI thread without depending on any specific UI framework.
/// </summary>
public interface IUIDispatcher
{
    /// <summary>
    /// Schedules the given action to run on the UI thread asynchronously.
    /// </summary>
    /// <param name="action">The action to execute on the UI thread.</param>
    void InvokeAsync(Action action);
}
