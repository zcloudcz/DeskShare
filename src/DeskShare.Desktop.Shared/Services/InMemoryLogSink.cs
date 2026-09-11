using System.Collections.ObjectModel;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using DeskShare.Core.Interfaces;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>
/// Serilog sink that writes log events to an in-memory collection for real-time UI display.
///
/// Uses IUIDispatcher abstraction to dispatch to UI thread without depending on
/// any specific UI framework. Each platform provides its own IUIDispatcher:
/// - WPF: WpfUIDispatcher (wraps Application.Current.Dispatcher.BeginInvoke)
/// - Avalonia: AvaloniaUIDispatcher (wraps Dispatcher.UIThread.InvokeAsync)
/// </summary>
public class InMemoryLogSink : ILogEventSink
{
    private readonly MessageTemplateTextFormatter _formatter;
    private readonly ObservableCollection<string> _logMessages;
    private readonly int _maxMessages;
    private readonly IUIDispatcher _dispatcher;

    /// <summary>
    /// Creates a new InMemoryLogSink.
    /// </summary>
    /// <param name="logMessages">The observable collection to write log messages to.</param>
    /// <param name="dispatcher">Platform-specific UI dispatcher for thread-safe collection updates.</param>
    /// <param name="maxMessages">Maximum number of messages to keep (prevents memory leaks).</param>
    public InMemoryLogSink(ObservableCollection<string> logMessages, IUIDispatcher dispatcher, int maxMessages = 500)
    {
        _logMessages = logMessages;
        _dispatcher = dispatcher;
        _maxMessages = maxMessages;
        _formatter = new MessageTemplateTextFormatter(
            "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
    }

    /// <summary>
    /// Called by Serilog for each log event. Formats the message and adds it to the collection.
    /// Must dispatch to UI thread because ObservableCollection is bound to UI controls.
    /// </summary>
    public void Emit(LogEvent logEvent)
    {
        if (logEvent == null) return;

        var stringWriter = new StringWriter();
        _formatter.Format(logEvent, stringWriter);
        var message = stringWriter.ToString().TrimEnd();

        // Use injected dispatcher instead of platform-specific Dispatcher calls
        _dispatcher.InvokeAsync(() =>
        {
            _logMessages.Add(message);

            // Keep only the last N messages to prevent memory issues
            while (_logMessages.Count > _maxMessages)
            {
                _logMessages.RemoveAt(0);
            }
        });
    }
}
