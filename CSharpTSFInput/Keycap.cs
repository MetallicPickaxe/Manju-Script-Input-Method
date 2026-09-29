using System;

namespace CSharpTSFInput
{
    /// <summary>
    /// The text a key produces on the keyboard layout of the current thread, with or without Shift. The
    /// punctuation rule appends it to the composition for every symbol key outside the Manchu punctuation
    /// table (CompositionProcessorEngine.ManchuPunctuation).
    /// </summary>
    internal static class Keycap
    {
        private const uint MAPVK_VK_TO_VSC = 0;
        // ToUnicodeEx flag bit 2: leave the keyboard state as it is, so a dead key looked up here does
        // not change what the next key produces in the host.
        private const uint LeaveKeyboardStateAlone = 0x4;
        private const int VK_SHIFT = 0x10, VK_CAPITAL = 0x14, VK_LSHIFT = 0xA0;


        /// <summary> The text key <paramref name="vk"/> produces, or null when it produces none, a control
        /// character, or a character the composition buffer reads as markup: the ' of the separator, the -
        /// that several schemes map to the suffix connector, and the separator and connector code points
        /// themselves.</summary>
        public static string? For(uint vk, bool shift)
        {
            string? text = FromLayout(vk, shift, NativeMethods.GetKeyboardLayout(0));
            if (string.IsNullOrEmpty(text)) return null;
            foreach (char c in text)
            {
                if (char.IsControl(c) || c == '\'' || c == '-' || c == ManjuEngine.SeparatorMarker
                    || c == (char)0x180E || c == (char)0x202F)
                    return null;
            }
            return text;
        }

        /// <summary> What ToUnicodeEx gives for the key on layout <paramref name="hkl"/>, with Shift down or
        /// up and Caps Lock as it currently is. A dead key gives its spacing character.</summary>
        internal static unsafe string? FromLayout(uint vk, bool shift, nint hkl)
        {
            byte* state = stackalloc byte[256];
            for (int i = 0; i < 256; i++) state[i] = 0;
            if (shift) { state[VK_SHIFT] = 0x80; state[VK_LSHIFT] = 0x80; }
            if ((NativeMethods.GetKeyState(VK_CAPITAL) & 1) != 0) state[VK_CAPITAL] = 0x01;
            uint scan = NativeMethods.MapVirtualKeyEx(vk, MAPVK_VK_TO_VSC, hkl);
            char* buffer = stackalloc char[8];
            int n = NativeMethods.ToUnicodeEx(vk, scan, state, buffer, 8, LeaveKeyboardStateAlone, hkl);
            if (n < 0) return new string(buffer, 0, 1);
            return n > 0 ? new string(buffer, 0, n) : null;
        }
    }
}
