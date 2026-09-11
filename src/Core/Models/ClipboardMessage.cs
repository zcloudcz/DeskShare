namespace DeskShare.Core.Models;

/// <summary>
/// Type of clipboard content.
/// </summary>
/// <remarks>
/// For junior developers:
/// The clipboard can hold different types of data (text, images, formatted text, etc.).
/// We use this enum to identify what kind of data is in the clipboard message.
/// This helps us know how to decode and display the data on the receiving end.
/// </remarks>
public enum ClipboardContentType
{
    /// <summary>
    /// Plain text content (like from Notepad).
    /// </summary>
    Text,

    /// <summary>
    /// Image content (PNG format).
    /// We use PNG because it's lossless and universally supported.
    /// </summary>
    Image,

    /// <summary>
    /// HTML formatted text (like from a web browser).
    /// Preserves styling, links, and formatting.
    /// </summary>
    Html,

    /// <summary>
    /// Rich text format (like from Word).
    /// Preserves fonts, colors, and formatting.
    /// </summary>
    Rtf,

    /// <summary>
    /// File list (paths to files).
    /// Note: We only send the file paths, not the actual file contents.
    /// Actual file transfer would require additional implementation.
    /// </summary>
    Files
}

/// <summary>
/// Represents a clipboard synchronization message sent over WebRTC data channel.
/// When you copy something on one computer, this message carries that data
/// to the other computer so it can be pasted there.
/// </summary>
/// <remarks>
/// For junior developers:
/// Think of this as a "package" that carries clipboard data between computers.
/// - When you copy text/image on Client → this message is created and sent to Server
/// - Server receives it and puts the data into its clipboard
/// - Same works in reverse (Server → Client)
///
/// Why do we need sequence numbers?
/// Network messages can arrive out of order or duplicated. The sequence number
/// helps us detect and ignore duplicate or old messages.
///
/// Why base64 for images?
/// WebRTC data channel works best with text (JSON). Base64 converts binary
/// image data to text so it can be sent in JSON format.
/// </remarks>
public sealed class ClipboardMessage
{
    /// <summary>
    /// Type of clipboard content (Text, Image, Html, etc.).
    /// Tells the receiver how to interpret the data in this message.
    /// </summary>
    public ClipboardContentType ContentType { get; set; }

    /// <summary>
    /// Text content (for Text, Html, Rtf types).
    /// This will be null if ContentType is Image or Files.
    /// </summary>
    public string? TextContent { get; set; }

    /// <summary>
    /// Image data as base64 string (for Image type).
    /// Encoded as PNG for best compatibility.
    /// </summary>
    /// <remarks>
    /// For junior developers:
    /// Base64 encoding converts binary image data to ASCII text.
    /// Example: A 100KB PNG image becomes ~133KB base64 string.
    /// Why? Because JSON (which we use for messaging) works best with text.
    /// The receiver decodes this base64 back to a PNG image.
    /// </remarks>
    public string? ImageDataBase64 { get; set; }

    /// <summary>
    /// File paths (for Files type).
    /// Note: Actual file transfer not implemented yet, only paths are sent.
    /// </summary>
    /// <remarks>
    /// For junior developers:
    /// This is just a list of file paths like ["C:\Documents\report.pdf"].
    /// We don't send the actual file contents (yet) - that would require
    /// chunking large files and implementing file transfer protocol.
    /// </remarks>
    public List<string>? FilePaths { get; set; }

    /// <summary>
    /// Timestamp when clipboard was captured (UTC).
    /// Used for logging and debugging to track when data was copied.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Size of content in bytes (for bandwidth monitoring).
    /// Helps us track how much data we're sending over the network.
    /// </summary>
    /// <remarks>
    /// For junior developers:
    /// This is calculated as:
    /// - For text: Number of bytes in UTF-8 encoding
    /// - For images: Length of the base64 string
    /// We use this to monitor bandwidth usage and detect large transfers.
    /// </remarks>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Sequence number to detect duplicate/out-of-order messages.
    /// Increments with each clipboard change: 1, 2, 3, 4...
    /// </summary>
    /// <remarks>
    /// For junior developers:
    /// Imagine you copy 3 things quickly: "Hello" → "World" → "!"
    /// Network might deliver them as: seq=1 ("Hello"), seq=3 ("!"), seq=2 ("World")
    ///
    /// The sequence number helps us:
    /// 1. Ignore duplicates (if we receive seq=2 twice)
    /// 2. Detect out-of-order delivery (seq=3 before seq=2)
    /// 3. Debug synchronization issues
    ///
    /// Note: For clipboard, we typically just use the latest message regardless
    /// of sequence, but the number is helpful for debugging.
    /// </remarks>
    public long SequenceNumber { get; set; }
}
