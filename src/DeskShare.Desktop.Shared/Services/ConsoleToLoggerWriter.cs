using System.Text;
using Serilog;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>
/// TextWriter that redirects Console.WriteLine output to Serilog.
/// This captures Console output from the Core library when running in-process.
/// Platform-independent: shared between WPF and Avalonia desktop clients.
/// </summary>
public class ConsoleToLoggerWriter : TextWriter
{
    private readonly StringBuilder _lineBuffer = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\n')
        {
            // Log the complete line when newline is encountered
            var line = _lineBuffer.ToString().TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(line))
            {
                Log.Information("[Console] {Message}", line);
            }
            _lineBuffer.Clear();
        }
        else
        {
            _lineBuffer.Append(value);
        }
    }

    public override void WriteLine(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Log.Information("[Console] {Message}", value);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Flush any remaining content in the buffer
            if (_lineBuffer.Length > 0)
            {
                var line = _lineBuffer.ToString().TrimEnd('\r', '\n');
                if (!string.IsNullOrWhiteSpace(line))
                {
                    Log.Information("[Console] {Message}", line);
                }
            }
        }
        base.Dispose(disposing);
    }
}
