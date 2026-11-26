namespace BrutalEngine.Platform;

/// <summary>A physical key, identified by its position on the keyboard rather than by what the layout prints on it.</summary>
/// <remarks>
/// <para>
/// The values from 4 to 231 are the usage ids of the USB HID keyboard/keypad page (0x07). The values from 257 up are
/// SDL 3's own, for keys outside that page: the mode switch key, media and application-control keys, and phone keys.
/// Every member has SDL 3's name and value, so a backend translates an SDL scancode with a range check and a cast.
/// </para>
/// <para>
/// A value without a member can still arrive, from a key that this version doesn't name; treat it as an unknown key,
/// not as an error. The values are fixed: new members are only ever added, never renumbered or removed.
/// </para>
/// </remarks>
public enum Scancode : ushort
{
    /// <summary>No key, or a key that the backend can't identify (value 0).</summary>
    Unknown = 0,

    /// <summary>The A key (USB HID 0x04).</summary>
    A = 4,
    /// <summary>The B key (USB HID 0x05).</summary>
    B = 5,
    /// <summary>The C key (USB HID 0x06).</summary>
    C = 6,
    /// <summary>The D key (USB HID 0x07).</summary>
    D = 7,
    /// <summary>The E key (USB HID 0x08).</summary>
    E = 8,
    /// <summary>The F key (USB HID 0x09).</summary>
    F = 9,
    /// <summary>The G key (USB HID 0x0A).</summary>
    G = 10,
    /// <summary>The H key (USB HID 0x0B).</summary>
    H = 11,
    /// <summary>The I key (USB HID 0x0C).</summary>
    I = 12,
    /// <summary>The J key (USB HID 0x0D).</summary>
    J = 13,
    /// <summary>The K key (USB HID 0x0E).</summary>
    K = 14,
    /// <summary>The L key (USB HID 0x0F).</summary>
    L = 15,
    /// <summary>The M key (USB HID 0x10).</summary>
    M = 16,
    /// <summary>The N key (USB HID 0x11).</summary>
    N = 17,
    /// <summary>The O key (USB HID 0x12).</summary>
    O = 18,
    /// <summary>The P key (USB HID 0x13).</summary>
    P = 19,
    /// <summary>The Q key (USB HID 0x14).</summary>
    Q = 20,
    /// <summary>The R key (USB HID 0x15).</summary>
    R = 21,
    /// <summary>The S key (USB HID 0x16).</summary>
    S = 22,
    /// <summary>The T key (USB HID 0x17).</summary>
    T = 23,
    /// <summary>The U key (USB HID 0x18).</summary>
    U = 24,
    /// <summary>The V key (USB HID 0x19).</summary>
    V = 25,
    /// <summary>The W key (USB HID 0x1A).</summary>
    W = 26,
    /// <summary>The X key (USB HID 0x1B).</summary>
    X = 27,
    /// <summary>The Y key (USB HID 0x1C).</summary>
    Y = 28,
    /// <summary>The Z key (USB HID 0x1D).</summary>
    Z = 29,

    /// <summary>The 1 key of the top row (USB HID 0x1E).</summary>
    N1 = 30,
    /// <summary>The 2 key of the top row (USB HID 0x1F).</summary>
    N2 = 31,
    /// <summary>The 3 key of the top row (USB HID 0x20).</summary>
    N3 = 32,
    /// <summary>The 4 key of the top row (USB HID 0x21).</summary>
    N4 = 33,
    /// <summary>The 5 key of the top row (USB HID 0x22).</summary>
    N5 = 34,
    /// <summary>The 6 key of the top row (USB HID 0x23).</summary>
    N6 = 35,
    /// <summary>The 7 key of the top row (USB HID 0x24).</summary>
    N7 = 36,
    /// <summary>The 8 key of the top row (USB HID 0x25).</summary>
    N8 = 37,
    /// <summary>The 9 key of the top row (USB HID 0x26).</summary>
    N9 = 38,
    /// <summary>The 0 key of the top row (USB HID 0x27).</summary>
    N0 = 39,

    /// <summary>The Return (Enter) key (USB HID 0x28).</summary>
    Return = 40,
    /// <summary>The Escape key (USB HID 0x29).</summary>
    Escape = 41,
    /// <summary>The Backspace key (USB HID 0x2A).</summary>
    Backspace = 42,
    /// <summary>The Tab key (USB HID 0x2B).</summary>
    Tab = 43,
    /// <summary>The space bar (USB HID 0x2C).</summary>
    Space = 44,
    /// <summary>The minus and underscore key (USB HID 0x2D).</summary>
    Minus = 45,
    /// <summary>The equals and plus key (USB HID 0x2E).</summary>
    Equals = 46,
    /// <summary>The left bracket and left brace key (USB HID 0x2F).</summary>
    Leftbracket = 47,
    /// <summary>The right bracket and right brace key (USB HID 0x30).</summary>
    Rightbracket = 48,
    /// <summary>The backslash and vertical bar key (USB HID 0x31).</summary>
    Backslash = 49,
    /// <summary>The non-US hash and tilde key, found on ISO keyboards (USB HID 0x32).</summary>
    Nonushash = 50,
    /// <summary>The semicolon and colon key (USB HID 0x33).</summary>
    Semicolon = 51,
    /// <summary>The apostrophe and quotation mark key (USB HID 0x34).</summary>
    Apostrophe = 52,
    /// <summary>The grave accent and tilde key (USB HID 0x35).</summary>
    Grave = 53,
    /// <summary>The comma and less-than key (USB HID 0x36).</summary>
    Comma = 54,
    /// <summary>The period and greater-than key (USB HID 0x37).</summary>
    Period = 55,
    /// <summary>The slash and question mark key (USB HID 0x38).</summary>
    Slash = 56,
    /// <summary>The Caps Lock key (USB HID 0x39).</summary>
    Capslock = 57,

    /// <summary>The F1 key (USB HID 0x3A).</summary>
    F1 = 58,
    /// <summary>The F2 key (USB HID 0x3B).</summary>
    F2 = 59,
    /// <summary>The F3 key (USB HID 0x3C).</summary>
    F3 = 60,
    /// <summary>The F4 key (USB HID 0x3D).</summary>
    F4 = 61,
    /// <summary>The F5 key (USB HID 0x3E).</summary>
    F5 = 62,
    /// <summary>The F6 key (USB HID 0x3F).</summary>
    F6 = 63,
    /// <summary>The F7 key (USB HID 0x40).</summary>
    F7 = 64,
    /// <summary>The F8 key (USB HID 0x41).</summary>
    F8 = 65,
    /// <summary>The F9 key (USB HID 0x42).</summary>
    F9 = 66,
    /// <summary>The F10 key (USB HID 0x43).</summary>
    F10 = 67,
    /// <summary>The F11 key (USB HID 0x44).</summary>
    F11 = 68,
    /// <summary>The F12 key (USB HID 0x45).</summary>
    F12 = 69,

    /// <summary>The Print Screen key (USB HID 0x46).</summary>
    Printscreen = 70,
    /// <summary>The Scroll Lock key (USB HID 0x47).</summary>
    Scrolllock = 71,
    /// <summary>The Pause key (USB HID 0x48).</summary>
    Pause = 72,
    /// <summary>The Insert key (USB HID 0x49).</summary>
    Insert = 73,
    /// <summary>The Home key (USB HID 0x4A).</summary>
    Home = 74,
    /// <summary>The Page Up key (USB HID 0x4B).</summary>
    Pageup = 75,
    /// <summary>The Delete (forward delete) key (USB HID 0x4C).</summary>
    Delete = 76,
    /// <summary>The End key (USB HID 0x4D).</summary>
    End = 77,
    /// <summary>The Page Down key (USB HID 0x4E).</summary>
    Pagedown = 78,
    /// <summary>The right arrow key (USB HID 0x4F).</summary>
    Right = 79,
    /// <summary>The left arrow key (USB HID 0x50).</summary>
    Left = 80,
    /// <summary>The down arrow key (USB HID 0x51).</summary>
    Down = 81,
    /// <summary>The up arrow key (USB HID 0x52).</summary>
    Up = 82,

    /// <summary>The keypad Num Lock and Clear key (USB HID 0x53).</summary>
    Numlockclear = 83,
    /// <summary>The keypad divide key (USB HID 0x54).</summary>
    KpDivide = 84,
    /// <summary>The keypad multiply key (USB HID 0x55).</summary>
    KpMultiply = 85,
    /// <summary>The keypad minus key (USB HID 0x56).</summary>
    KpMinus = 86,
    /// <summary>The keypad plus key (USB HID 0x57).</summary>
    KpPlus = 87,
    /// <summary>The keypad Enter key (USB HID 0x58).</summary>
    KpEnter = 88,
    /// <summary>The keypad 1 key (USB HID 0x59).</summary>
    Kp1 = 89,
    /// <summary>The keypad 2 key (USB HID 0x5A).</summary>
    Kp2 = 90,
    /// <summary>The keypad 3 key (USB HID 0x5B).</summary>
    Kp3 = 91,
    /// <summary>The keypad 4 key (USB HID 0x5C).</summary>
    Kp4 = 92,
    /// <summary>The keypad 5 key (USB HID 0x5D).</summary>
    Kp5 = 93,
    /// <summary>The keypad 6 key (USB HID 0x5E).</summary>
    Kp6 = 94,
    /// <summary>The keypad 7 key (USB HID 0x5F).</summary>
    Kp7 = 95,
    /// <summary>The keypad 8 key (USB HID 0x60).</summary>
    Kp8 = 96,
    /// <summary>The keypad 9 key (USB HID 0x61).</summary>
    Kp9 = 97,
    /// <summary>The keypad 0 key (USB HID 0x62).</summary>
    Kp0 = 98,
    /// <summary>The keypad period and Delete key (USB HID 0x63).</summary>
    KpPeriod = 99,

    /// <summary>The non-US backslash and vertical bar key, found on ISO keyboards (USB HID 0x64).</summary>
    Nonusbackslash = 100,
    /// <summary>The Application (context menu) key (USB HID 0x65).</summary>
    Application = 101,
    /// <summary>The Power key (USB HID 0x66).</summary>
    Power = 102,
    /// <summary>The keypad equals key (USB HID 0x67).</summary>
    KpEquals = 103,
    /// <summary>The F13 key (USB HID 0x68).</summary>
    F13 = 104,
    /// <summary>The F14 key (USB HID 0x69).</summary>
    F14 = 105,
    /// <summary>The F15 key (USB HID 0x6A).</summary>
    F15 = 106,
    /// <summary>The F16 key (USB HID 0x6B).</summary>
    F16 = 107,
    /// <summary>The F17 key (USB HID 0x6C).</summary>
    F17 = 108,
    /// <summary>The F18 key (USB HID 0x6D).</summary>
    F18 = 109,
    /// <summary>The F19 key (USB HID 0x6E).</summary>
    F19 = 110,
    /// <summary>The F20 key (USB HID 0x6F).</summary>
    F20 = 111,
    /// <summary>The F21 key (USB HID 0x70).</summary>
    F21 = 112,
    /// <summary>The F22 key (USB HID 0x71).</summary>
    F22 = 113,
    /// <summary>The F23 key (USB HID 0x72).</summary>
    F23 = 114,
    /// <summary>The F24 key (USB HID 0x73).</summary>
    F24 = 115,
    /// <summary>The Execute key (USB HID 0x74).</summary>
    Execute = 116,
    /// <summary>The Help key (USB HID 0x75).</summary>
    Help = 117,
    /// <summary>The Menu key (USB HID 0x76).</summary>
    Menu = 118,
    /// <summary>The Select key (USB HID 0x77).</summary>
    Select = 119,
    /// <summary>The Stop key (USB HID 0x78).</summary>
    Stop = 120,
    /// <summary>The Again key (USB HID 0x79).</summary>
    Again = 121,
    /// <summary>The Undo key (USB HID 0x7A).</summary>
    Undo = 122,
    /// <summary>The Cut key (USB HID 0x7B).</summary>
    Cut = 123,
    /// <summary>The Copy key (USB HID 0x7C).</summary>
    Copy = 124,
    /// <summary>The Paste key (USB HID 0x7D).</summary>
    Paste = 125,
    /// <summary>The Find key (USB HID 0x7E).</summary>
    Find = 126,
    /// <summary>The Mute key (USB HID 0x7F).</summary>
    Mute = 127,
    /// <summary>The Volume Up key (USB HID 0x80).</summary>
    Volumeup = 128,
    /// <summary>The Volume Down key (USB HID 0x81).</summary>
    Volumedown = 129,

    /// <summary>The keypad comma key (USB HID 0x85).</summary>
    KpComma = 133,
    /// <summary>The keypad equals key of AS/400 keyboards (USB HID 0x86).</summary>
    KpEqualsas400 = 134,
    /// <summary>The International 1 key (USB HID 0x87).</summary>
    International1 = 135,
    /// <summary>The International 2 key (USB HID 0x88).</summary>
    International2 = 136,
    /// <summary>The International 3 key (USB HID 0x89).</summary>
    International3 = 137,
    /// <summary>The International 4 key (USB HID 0x8A).</summary>
    International4 = 138,
    /// <summary>The International 5 key (USB HID 0x8B).</summary>
    International5 = 139,
    /// <summary>The International 6 key (USB HID 0x8C).</summary>
    International6 = 140,
    /// <summary>The International 7 key (USB HID 0x8D).</summary>
    International7 = 141,
    /// <summary>The International 8 key (USB HID 0x8E).</summary>
    International8 = 142,
    /// <summary>The International 9 key (USB HID 0x8F).</summary>
    International9 = 143,
    /// <summary>The Language 1 key (USB HID 0x90).</summary>
    Lang1 = 144,
    /// <summary>The Language 2 key (USB HID 0x91).</summary>
    Lang2 = 145,
    /// <summary>The Language 3 key (USB HID 0x92).</summary>
    Lang3 = 146,
    /// <summary>The Language 4 key (USB HID 0x93).</summary>
    Lang4 = 147,
    /// <summary>The Language 5 key (USB HID 0x94).</summary>
    Lang5 = 148,
    /// <summary>The Language 6 key (USB HID 0x95).</summary>
    Lang6 = 149,
    /// <summary>The Language 7 key (USB HID 0x96).</summary>
    Lang7 = 150,
    /// <summary>The Language 8 key (USB HID 0x97).</summary>
    Lang8 = 151,
    /// <summary>The Language 9 key (USB HID 0x98).</summary>
    Lang9 = 152,
    /// <summary>The Alternate Erase key (USB HID 0x99).</summary>
    Alterase = 153,
    /// <summary>The SysReq (Attention) key (USB HID 0x9A).</summary>
    Sysreq = 154,
    /// <summary>The Cancel key (USB HID 0x9B).</summary>
    Cancel = 155,
    /// <summary>The Clear key (USB HID 0x9C).</summary>
    Clear = 156,
    /// <summary>The Prior key (USB HID 0x9D).</summary>
    Prior = 157,
    /// <summary>The second Return key (USB HID 0x9E).</summary>
    Return2 = 158,
    /// <summary>The Separator key (USB HID 0x9F).</summary>
    Separator = 159,
    /// <summary>The Out key (USB HID 0xA0).</summary>
    Out = 160,
    /// <summary>The Oper key (USB HID 0xA1).</summary>
    Oper = 161,
    /// <summary>The Clear/Again key (USB HID 0xA2).</summary>
    Clearagain = 162,
    /// <summary>The CrSel/Props key (USB HID 0xA3).</summary>
    Crsel = 163,
    /// <summary>The ExSel key (USB HID 0xA4).</summary>
    Exsel = 164,

    /// <summary>The keypad 00 key (USB HID 0xB0).</summary>
    Kp00 = 176,
    /// <summary>The keypad 000 key (USB HID 0xB1).</summary>
    Kp000 = 177,
    /// <summary>The thousands separator key (USB HID 0xB2).</summary>
    Thousandsseparator = 178,
    /// <summary>The decimal separator key (USB HID 0xB3).</summary>
    Decimalseparator = 179,
    /// <summary>The currency unit key (USB HID 0xB4).</summary>
    Currencyunit = 180,
    /// <summary>The currency sub-unit key (USB HID 0xB5).</summary>
    Currencysubunit = 181,
    /// <summary>The keypad left parenthesis key (USB HID 0xB6).</summary>
    KpLeftparen = 182,
    /// <summary>The keypad right parenthesis key (USB HID 0xB7).</summary>
    KpRightparen = 183,
    /// <summary>The keypad left brace key (USB HID 0xB8).</summary>
    KpLeftbrace = 184,
    /// <summary>The keypad right brace key (USB HID 0xB9).</summary>
    KpRightbrace = 185,
    /// <summary>The keypad Tab key (USB HID 0xBA).</summary>
    KpTab = 186,
    /// <summary>The keypad Backspace key (USB HID 0xBB).</summary>
    KpBackspace = 187,
    /// <summary>The keypad A key (USB HID 0xBC).</summary>
    KpA = 188,
    /// <summary>The keypad B key (USB HID 0xBD).</summary>
    KpB = 189,
    /// <summary>The keypad C key (USB HID 0xBE).</summary>
    KpC = 190,
    /// <summary>The keypad D key (USB HID 0xBF).</summary>
    KpD = 191,
    /// <summary>The keypad E key (USB HID 0xC0).</summary>
    KpE = 192,
    /// <summary>The keypad F key (USB HID 0xC1).</summary>
    KpF = 193,
    /// <summary>The keypad XOR key (USB HID 0xC2).</summary>
    KpXor = 194,
    /// <summary>The keypad caret (power) key (USB HID 0xC3).</summary>
    KpPower = 195,
    /// <summary>The keypad percent key (USB HID 0xC4).</summary>
    KpPercent = 196,
    /// <summary>The keypad less-than key (USB HID 0xC5).</summary>
    KpLess = 197,
    /// <summary>The keypad greater-than key (USB HID 0xC6).</summary>
    KpGreater = 198,
    /// <summary>The keypad ampersand key (USB HID 0xC7).</summary>
    KpAmpersand = 199,
    /// <summary>The keypad double ampersand key (USB HID 0xC8).</summary>
    KpDblampersand = 200,
    /// <summary>The keypad vertical bar key (USB HID 0xC9).</summary>
    KpVerticalbar = 201,
    /// <summary>The keypad double vertical bar key (USB HID 0xCA).</summary>
    KpDblverticalbar = 202,
    /// <summary>The keypad colon key (USB HID 0xCB).</summary>
    KpColon = 203,
    /// <summary>The keypad hash key (USB HID 0xCC).</summary>
    KpHash = 204,
    /// <summary>The keypad space key (USB HID 0xCD).</summary>
    KpSpace = 205,
    /// <summary>The keypad at-sign key (USB HID 0xCE).</summary>
    KpAt = 206,
    /// <summary>The keypad exclamation mark key (USB HID 0xCF).</summary>
    KpExclam = 207,
    /// <summary>The keypad memory store key (USB HID 0xD0).</summary>
    KpMemstore = 208,
    /// <summary>The keypad memory recall key (USB HID 0xD1).</summary>
    KpMemrecall = 209,
    /// <summary>The keypad memory clear key (USB HID 0xD2).</summary>
    KpMemclear = 210,
    /// <summary>The keypad memory add key (USB HID 0xD3).</summary>
    KpMemadd = 211,
    /// <summary>The keypad memory subtract key (USB HID 0xD4).</summary>
    KpMemsubtract = 212,
    /// <summary>The keypad memory multiply key (USB HID 0xD5).</summary>
    KpMemmultiply = 213,
    /// <summary>The keypad memory divide key (USB HID 0xD6).</summary>
    KpMemdivide = 214,
    /// <summary>The keypad plus-minus key (USB HID 0xD7).</summary>
    KpPlusminus = 215,
    /// <summary>The keypad Clear key (USB HID 0xD8).</summary>
    KpClear = 216,
    /// <summary>The keypad Clear Entry key (USB HID 0xD9).</summary>
    KpClearentry = 217,
    /// <summary>The keypad binary key (USB HID 0xDA).</summary>
    KpBinary = 218,
    /// <summary>The keypad octal key (USB HID 0xDB).</summary>
    KpOctal = 219,
    /// <summary>The keypad decimal key (USB HID 0xDC).</summary>
    KpDecimal = 220,
    /// <summary>The keypad hexadecimal key (USB HID 0xDD).</summary>
    KpHexadecimal = 221,

    /// <summary>The left Control key (USB HID 0xE0).</summary>
    Lctrl = 224,
    /// <summary>The left Shift key (USB HID 0xE1).</summary>
    Lshift = 225,
    /// <summary>The left Alt key (USB HID 0xE2).</summary>
    Lalt = 226,
    /// <summary>The left GUI key: Windows, Command or Meta (USB HID 0xE3).</summary>
    Lgui = 227,
    /// <summary>The right Control key (USB HID 0xE4).</summary>
    Rctrl = 228,
    /// <summary>The right Shift key (USB HID 0xE5).</summary>
    Rshift = 229,
    /// <summary>The right Alt key (USB HID 0xE6).</summary>
    Ralt = 230,
    /// <summary>The right GUI key: Windows, Command or Meta (USB HID 0xE7).</summary>
    Rgui = 231,

    /// <summary>The mode switch key (SDL value 257, outside the USB HID keyboard page).</summary>
    Mode = 257,

    /// <summary>The Sleep key (SDL value 258, outside the USB HID keyboard page).</summary>
    Sleep = 258,
    /// <summary>The Wake key (SDL value 259, outside the USB HID keyboard page).</summary>
    Wake = 259,
    /// <summary>The channel up key (SDL value 260, outside the USB HID keyboard page).</summary>
    ChannelIncrement = 260,
    /// <summary>The channel down key (SDL value 261, outside the USB HID keyboard page).</summary>
    ChannelDecrement = 261,
    /// <summary>The media Play key (SDL value 262, outside the USB HID keyboard page).</summary>
    MediaPlay = 262,
    /// <summary>The media Pause key (SDL value 263, outside the USB HID keyboard page).</summary>
    MediaPause = 263,
    /// <summary>The media Record key (SDL value 264, outside the USB HID keyboard page).</summary>
    MediaRecord = 264,
    /// <summary>The media Fast Forward key (SDL value 265, outside the USB HID keyboard page).</summary>
    MediaFastForward = 265,
    /// <summary>The media Rewind key (SDL value 266, outside the USB HID keyboard page).</summary>
    MediaRewind = 266,
    /// <summary>The media Next Track key (SDL value 267, outside the USB HID keyboard page).</summary>
    MediaNextTrack = 267,
    /// <summary>The media Previous Track key (SDL value 268, outside the USB HID keyboard page).</summary>
    MediaPreviousTrack = 268,
    /// <summary>The media Stop key (SDL value 269, outside the USB HID keyboard page).</summary>
    MediaStop = 269,
    /// <summary>The media Eject key (SDL value 270, outside the USB HID keyboard page).</summary>
    MediaEject = 270,
    /// <summary>The media Play/Pause key (SDL value 271, outside the USB HID keyboard page).</summary>
    MediaPlayPause = 271,
    /// <summary>The media Select key (SDL value 272, outside the USB HID keyboard page).</summary>
    MediaSelect = 272,
    /// <summary>The application-control New key (SDL value 273, outside the USB HID keyboard page).</summary>
    AcNew = 273,
    /// <summary>The application-control Open key (SDL value 274, outside the USB HID keyboard page).</summary>
    AcOpen = 274,
    /// <summary>The application-control Close key (SDL value 275, outside the USB HID keyboard page).</summary>
    AcClose = 275,
    /// <summary>The application-control Exit key (SDL value 276, outside the USB HID keyboard page).</summary>
    AcExit = 276,
    /// <summary>The application-control Save key (SDL value 277, outside the USB HID keyboard page).</summary>
    AcSave = 277,
    /// <summary>The application-control Print key (SDL value 278, outside the USB HID keyboard page).</summary>
    AcPrint = 278,
    /// <summary>The application-control Properties key (SDL value 279, outside the USB HID keyboard page).</summary>
    AcProperties = 279,
    /// <summary>The application-control Search key (SDL value 280, outside the USB HID keyboard page).</summary>
    AcSearch = 280,
    /// <summary>The application-control Home key (SDL value 281, outside the USB HID keyboard page).</summary>
    AcHome = 281,
    /// <summary>The application-control Back key (SDL value 282, outside the USB HID keyboard page).</summary>
    AcBack = 282,
    /// <summary>The application-control Forward key (SDL value 283, outside the USB HID keyboard page).</summary>
    AcForward = 283,
    /// <summary>The application-control Stop key (SDL value 284, outside the USB HID keyboard page).</summary>
    AcStop = 284,
    /// <summary>The application-control Refresh key (SDL value 285, outside the USB HID keyboard page).</summary>
    AcRefresh = 285,
    /// <summary>The application-control Bookmarks key (SDL value 286, outside the USB HID keyboard page).</summary>
    AcBookmarks = 286,

    /// <summary>The left soft key of phones (SDL value 287, outside the USB HID keyboard page).</summary>
    Softleft = 287,
    /// <summary>The right soft key of phones (SDL value 288, outside the USB HID keyboard page).</summary>
    Softright = 288,
    /// <summary>The call key of phones (SDL value 289, outside the USB HID keyboard page).</summary>
    Call = 289,
    /// <summary>The end call key of phones (SDL value 290, outside the USB HID keyboard page).</summary>
    Endcall = 290,
}
