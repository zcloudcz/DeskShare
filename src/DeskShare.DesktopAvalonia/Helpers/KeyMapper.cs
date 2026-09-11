using Avalonia.Input;

namespace DeskShare.DesktopAvalonia.Helpers;

/// <summary>
/// Maps Avalonia Key enum values to Windows Virtual Key codes (VK_*).
///
/// In WPF, this was done with: KeyInterop.VirtualKeyFromKey(key)
/// Avalonia doesn't have KeyInterop, so we need this manual mapping.
///
/// The remote server expects Win32 virtual key codes in InputMessage.KeyCode,
/// because the InputController on the server uses Win32 SendInput API.
/// </summary>
public static class KeyMapper
{
    /// <summary>
    /// Mapping dictionary from Avalonia Key enum to Win32 VK_* codes.
    /// Reference: https://learn.microsoft.com/en-us/windows/win32/inputdev/virtual-key-codes
    /// </summary>
    private static readonly Dictionary<Key, int> KeyToVirtualKey = new()
    {
        // Mouse buttons (not typically used for keyboard, but included for completeness)
        // VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04

        // Control keys
        { Key.Back, 0x08 },           // VK_BACK (Backspace)
        { Key.Tab, 0x09 },            // VK_TAB
        { Key.Return, 0x0D },         // VK_RETURN (Enter)
        { Key.LeftShift, 0x10 },      // VK_SHIFT
        { Key.RightShift, 0x10 },     // VK_SHIFT
        { Key.LeftCtrl, 0x11 },       // VK_CONTROL
        { Key.RightCtrl, 0x11 },      // VK_CONTROL
        { Key.LeftAlt, 0x12 },        // VK_MENU (Alt)
        { Key.RightAlt, 0x12 },       // VK_MENU (Alt)
        { Key.Pause, 0x13 },          // VK_PAUSE
        { Key.CapsLock, 0x14 },       // VK_CAPITAL
        { Key.Escape, 0x1B },         // VK_ESCAPE
        { Key.Space, 0x20 },          // VK_SPACE
        { Key.PageUp, 0x21 },         // VK_PRIOR
        { Key.PageDown, 0x22 },       // VK_NEXT
        { Key.End, 0x23 },            // VK_END
        { Key.Home, 0x24 },           // VK_HOME
        { Key.Left, 0x25 },           // VK_LEFT
        { Key.Up, 0x26 },             // VK_UP
        { Key.Right, 0x27 },          // VK_RIGHT
        { Key.Down, 0x28 },           // VK_DOWN
        { Key.PrintScreen, 0x2C },    // VK_SNAPSHOT
        { Key.Insert, 0x2D },         // VK_INSERT
        { Key.Delete, 0x2E },         // VK_DELETE

        // Number keys (0-9)
        { Key.D0, 0x30 },
        { Key.D1, 0x31 },
        { Key.D2, 0x32 },
        { Key.D3, 0x33 },
        { Key.D4, 0x34 },
        { Key.D5, 0x35 },
        { Key.D6, 0x36 },
        { Key.D7, 0x37 },
        { Key.D8, 0x38 },
        { Key.D9, 0x39 },

        // Letter keys (A-Z) - VK codes match ASCII values
        { Key.A, 0x41 },
        { Key.B, 0x42 },
        { Key.C, 0x43 },
        { Key.D, 0x44 },
        { Key.E, 0x45 },
        { Key.F, 0x46 },
        { Key.G, 0x47 },
        { Key.H, 0x48 },
        { Key.I, 0x49 },
        { Key.J, 0x4A },
        { Key.K, 0x4B },
        { Key.L, 0x4C },
        { Key.M, 0x4D },
        { Key.N, 0x4E },
        { Key.O, 0x4F },
        { Key.P, 0x50 },
        { Key.Q, 0x51 },
        { Key.R, 0x52 },
        { Key.S, 0x53 },
        { Key.T, 0x54 },
        { Key.U, 0x55 },
        { Key.V, 0x56 },
        { Key.W, 0x57 },
        { Key.X, 0x58 },
        { Key.Y, 0x59 },
        { Key.Z, 0x5A },

        // Windows key
        { Key.LWin, 0x5B },           // VK_LWIN
        { Key.RWin, 0x5C },           // VK_RWIN

        // Numpad keys
        { Key.NumPad0, 0x60 },        // VK_NUMPAD0
        { Key.NumPad1, 0x61 },
        { Key.NumPad2, 0x62 },
        { Key.NumPad3, 0x63 },
        { Key.NumPad4, 0x64 },
        { Key.NumPad5, 0x65 },
        { Key.NumPad6, 0x66 },
        { Key.NumPad7, 0x67 },
        { Key.NumPad8, 0x68 },
        { Key.NumPad9, 0x69 },        // VK_NUMPAD9
        { Key.Multiply, 0x6A },       // VK_MULTIPLY (numpad *)
        { Key.Add, 0x6B },            // VK_ADD (numpad +)
        { Key.Subtract, 0x6D },       // VK_SUBTRACT (numpad -)
        { Key.Decimal, 0x6E },        // VK_DECIMAL (numpad .)
        { Key.Divide, 0x6F },         // VK_DIVIDE (numpad /)

        // Function keys (F1-F12)
        { Key.F1, 0x70 },
        { Key.F2, 0x71 },
        { Key.F3, 0x72 },
        { Key.F4, 0x73 },
        { Key.F5, 0x74 },
        { Key.F6, 0x75 },
        { Key.F7, 0x76 },
        { Key.F8, 0x77 },
        { Key.F9, 0x78 },
        { Key.F10, 0x79 },
        { Key.F11, 0x7A },
        { Key.F12, 0x7B },

        // Lock keys
        { Key.NumLock, 0x90 },        // VK_NUMLOCK
        { Key.Scroll, 0x91 },         // VK_SCROLL

        // OEM keys (punctuation, brackets, etc.)
        { Key.OemSemicolon, 0xBA },   // VK_OEM_1 ( ;: )
        { Key.OemPlus, 0xBB },        // VK_OEM_PLUS ( =+ )
        { Key.OemComma, 0xBC },       // VK_OEM_COMMA ( ,< )
        { Key.OemMinus, 0xBD },       // VK_OEM_MINUS ( -_ )
        { Key.OemPeriod, 0xBE },      // VK_OEM_PERIOD ( .> )
        { Key.OemQuestion, 0xBF },    // VK_OEM_2 ( /? )
        { Key.OemTilde, 0xC0 },       // VK_OEM_3 ( `~ )
        { Key.OemOpenBrackets, 0xDB },// VK_OEM_4 ( [{ )
        { Key.OemPipe, 0xDC },        // VK_OEM_5 ( \| )
        { Key.OemCloseBrackets, 0xDD },// VK_OEM_6 ( ]} )
        { Key.OemQuotes, 0xDE },      // VK_OEM_7 ( '" )
    };

    /// <summary>
    /// Converts an Avalonia Key to a Win32 virtual key code.
    /// Returns 0 if the key is not mapped (unknown key).
    /// </summary>
    /// <param name="key">The Avalonia key to convert.</param>
    /// <returns>Win32 VK_* code, or 0 if not mapped.</returns>
    public static int ToVirtualKey(Key key)
    {
        return KeyToVirtualKey.TryGetValue(key, out var vk) ? vk : 0;
    }
}
