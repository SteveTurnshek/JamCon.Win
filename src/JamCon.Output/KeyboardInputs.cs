using JamCon.Core.Mapping;
using static JamCon.Output.NativeMethods;

namespace JamCon.Output;

/// <summary>Builds keyboard INPUT records for a key combination.</summary>
internal static class KeyboardInputs
{
    public const ushort VkShift = 0x10;
    public const ushort VkControl = 0x11;
    public const ushort VkAlt = 0x12;
    public const ushort VkLeftWin = 0x5B;

    /// <summary>
    /// Down: modifiers in a fixed order, then the key. Up: the key, then modifiers in reverse.
    /// Sent as one SendInput call so nothing else can interleave mid-combination.
    /// </summary>
    public static Input[] For(KeyCombo combo, bool isPressed)
    {
        var keys = new List<ushort>(5);
        if (combo.Modifiers.HasFlag(KeyModifiers.Control)) keys.Add(VkControl);
        if (combo.Modifiers.HasFlag(KeyModifiers.Alt)) keys.Add(VkAlt);
        if (combo.Modifiers.HasFlag(KeyModifiers.Shift)) keys.Add(VkShift);
        if (combo.Modifiers.HasFlag(KeyModifiers.Win)) keys.Add(VkLeftWin);
        keys.Add(combo.VirtualKey);
        if (!isPressed) keys.Reverse();

        return keys.Select(vk => new Input { Type = InputKeyboard, Union = new InputUnion { Keyboard = Key(vk, isPressed) } }).ToArray();
    }

    /// <summary>
    /// Browser, media and launch keys (VK 0xA6–0xB7). Windows turns these into WM_APPCOMMAND from
    /// the virtual key, so they are injected by virtual key rather than scan code.
    /// </summary>
    public static bool IsAppCommandKey(ushort vk) => vk is >= 0xA6 and <= 0xB7;

    /// <summary>
    /// Keys that are E0-prefixed on a real keyboard even though MapVirtualKey does not say so.
    /// </summary>
    /// <remarks>
    /// Verified on this machine: MAPVK_VK_TO_VSC_EX returns the Windows key as 0xE05B but the arrow
    /// keys as bare 0x48/0x50, which are the numeric keypad's scan codes. Injected without the
    /// extended flag, Down arrow arrives as numpad 2 and types "2" when NumLock is on.
    /// </remarks>
    public static bool IsExtendedByVirtualKey(ushort vk) => vk is
        >= 0x21 and <= 0x28   // Page Up, Page Down, End, Home, arrows
        or 0x2C or 0x2D or 0x2E // Print Screen, Insert, Delete
        or 0x5B or 0x5C or 0x5D // Windows keys, Menu
        or 0x6F                 // numpad divide
        or 0x90                 // Num Lock
        or 0xA3 or 0xA5;        // right Ctrl, right Alt

    /// <summary>
    /// Scan-code injection where possible: many games read scan codes and ignore virtual-key
    /// injection (plan, Phase 3). Falls back to the virtual key when the layout has no scan code.
    /// </summary>
    public static KeyboardInput Key(ushort vk, bool isPressed)
    {
        var up = isPressed ? 0 : KeyEventKeyUp;

        if (!IsAppCommandKey(vk))
        {
            var scan = MapVirtualKey(vk, MapVkToVscEx);
            if (scan != 0)
            {
                var prefix = scan >> 8;
                var extended = prefix is 0xE0 or 0xE1 || IsExtendedByVirtualKey(vk) ? KeyEventExtendedKey : 0;
                return new KeyboardInput { ScanCode = (ushort)(scan & 0xFF), Flags = KeyEventScanCode | extended | up };
            }
        }

        // App-command keys are all E0-prefixed on real keyboards, so they carry the extended flag.
        return new KeyboardInput
        {
            VirtualKey = vk,
            ScanCode = (ushort)(MapVirtualKey(vk, MapVkToVscEx) & 0xFF),
            Flags = (IsAppCommandKey(vk) ? KeyEventExtendedKey : 0) | up,
        };
    }
}
