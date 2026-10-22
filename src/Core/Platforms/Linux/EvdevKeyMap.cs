namespace DeskShare.Core.Platforms.Linux;

/// <summary>
/// Windows virtual-key codes (what viewers send in <see cref="Models.InputMessage.KeyCode"/>) to Linux evdev key
/// codes (linux/input-event-codes.h), which the RemoteDesktop portal's NotifyKeyboardKeycode expects.
/// </summary>
/// <remarks>
/// evdev codes name physical key positions, so punctuation follows the US layout positions that the browser's
/// keyCode values describe; the compositor's keymap turns them into characters.
/// </remarks>
internal static class EvdevKeyMap
{
    private static readonly Dictionary<int, int> Map = Build();

    /// <summary>The evdev code for <paramref name="virtualKey"/>, or null when the key has no mapping.</summary>
    public static int? FromVirtualKey(int virtualKey) => Map.TryGetValue(virtualKey, out var code) ? code : null;

    private static Dictionary<int, int> Build()
    {
        var map = new Dictionary<int, int>
        {
            [0x08] = 14,  // Backspace
            [0x09] = 15,  // Tab
            [0x0D] = 28,  // Enter
            [0x10] = 42,  // Shift -> left shift
            [0x11] = 29,  // Control -> left ctrl
            [0x12] = 56,  // Alt -> left alt
            [0x13] = 119, // Pause
            [0x14] = 58,  // Caps Lock
            [0x1B] = 1,   // Escape
            [0x20] = 57,  // Space
            [0x21] = 104, // Page Up
            [0x22] = 109, // Page Down
            [0x23] = 107, // End
            [0x24] = 102, // Home
            [0x25] = 105, // Left
            [0x26] = 103, // Up
            [0x27] = 106, // Right
            [0x28] = 108, // Down
            [0x2C] = 99,  // Print Screen (SysRq)
            [0x2D] = 110, // Insert
            [0x2E] = 111, // Delete
            [0x30] = 11,  // 0 (the digit row is 1..9 then 0)
            [0x5B] = 125, // Left Windows -> left meta
            [0x5C] = 126, // Right Windows -> right meta
            [0x5D] = 127, // Applications -> compose/menu
            [0x60] = 82,  // Numpad 0
            [0x61] = 79, [0x62] = 80, [0x63] = 81, // Numpad 1-3
            [0x64] = 75, [0x65] = 76, [0x66] = 77, // Numpad 4-6
            [0x67] = 71, [0x68] = 72, [0x69] = 73, // Numpad 7-9
            [0x6A] = 55,  // Numpad *
            [0x6B] = 78,  // Numpad +
            [0x6C] = 96,  // Separator -> keypad enter
            [0x6D] = 74,  // Numpad -
            [0x6E] = 83,  // Numpad .
            [0x6F] = 98,  // Numpad /
            [0x90] = 69,  // Num Lock
            [0x91] = 70,  // Scroll Lock
            [0xA0] = 42, [0xA1] = 54,  // Left/right shift
            [0xA2] = 29, [0xA3] = 97,  // Left/right ctrl
            [0xA4] = 56, [0xA5] = 100, // Left/right alt
            [0xAD] = 113, // Volume mute
            [0xAE] = 114, // Volume down
            [0xAF] = 115, // Volume up
            [0xB0] = 163, // Next track
            [0xB1] = 165, // Previous track
            [0xB2] = 166, // Stop
            [0xB3] = 164, // Play/pause
            [0xBA] = 39,  // ; :
            [0xBB] = 13,  // = +
            [0xBC] = 51,  // , <
            [0xBD] = 12,  // - _
            [0xBE] = 52,  // . >
            [0xBF] = 53,  // / ?
            [0xC0] = 41,  // ` ~
            [0xDB] = 26,  // [ {
            [0xDC] = 43,  // \ |
            [0xDD] = 27,  // ] }
            [0xDE] = 40,  // ' "
            [0xE2] = 86,  // The extra key left of Z on ISO keyboards
        };

        // Digits 1-9 are consecutive (KEY_1 = 2).
        for (int i = 1; i <= 9; i++) map[0x30 + i] = 1 + i;

        // Letters are numbered by keyboard row, not alphabetically.
        AddRow(map, "QWERTYUIOP", 16);
        AddRow(map, "ASDFGHJKL", 30);
        AddRow(map, "ZXCVBNM", 44);

        // F1-F10 = 59..68, F11/F12 = 87/88, F13-F24 = 183..194.
        for (int i = 0; i < 10; i++) map[0x70 + i] = 59 + i;
        map[0x7A] = 87;
        map[0x7B] = 88;
        for (int i = 0; i < 12; i++) map[0x7C + i] = 183 + i;

        return map;
    }

    private static void AddRow(Dictionary<int, int> map, string letters, int firstCode)
    {
        for (int i = 0; i < letters.Length; i++) map[letters[i]] = firstCode + i; // VK_A..VK_Z equal 'A'..'Z'
    }
}
