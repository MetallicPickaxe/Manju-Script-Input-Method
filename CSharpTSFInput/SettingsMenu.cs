using System.Collections.Generic;

namespace CSharpTSFInput
{
    /// <summary>
    /// The settings menu opened by Ctrl+Shift+`: whether it is open, which row is highlighted, and what
    /// each menu key does. It changes no setting and draws nothing. <see cref="OnKey"/> says which row
    /// to move, the caller moves it through <see cref="ManjuEngine.CycleSettingsMenuRow"/>, and the
    /// window shows <see cref="Lines"/>.
    ///
    /// Menu keys, while the menu is open: Up and Down move the highlight and wrap at the ends; Enter or
    /// Space moves the highlighted row to its next value; 1 to 9 and 0 highlight rows 1 to 10 and move
    /// that row; Esc closes. The main digit row and the numeric keypad count alike. Every other key is
    /// not a menu key.
    /// </summary>
    internal sealed class SettingsMenu
    {
        public const uint VK_RETURN = 0x0D;
        public const uint VK_ESCAPE = 0x1B;
        public const uint VK_SPACE = 0x20;
        public const uint VK_UP = 0x26;
        public const uint VK_DOWN = 0x28;

        /// <summary> The combination that opens and closes the menu: Ctrl+Shift and the key that
        /// Windows reports as VK_OEM_3; where it sits depends on the layout.</summary>
        public const uint VK_OEM_3 = 0xC0;

        public enum Outcome { NotAMenuKey, Moved, Cycle, Closed }

        public bool IsOpen { get; private set; }
        public int Highlight { get; private set; }

        public static int RowCount => ManjuEngine.SettingsMenuRowCount;

        /// <summary> Opens with the first row highlighted.</summary>
        public void Open()
        {
            IsOpen = true;
            Highlight = 0;
        }

        public void Close() => IsOpen = false;

        /// <summary> A key pressed while the menu is open, without Ctrl, Alt or Win. Returns what it did
        /// and, for <see cref="Outcome.Cycle"/>, the row to move.</summary>
        public (Outcome What, int Row) OnKey(uint vk)
        {
            if (!IsOpen) return (Outcome.NotAMenuKey, -1);
            switch (vk)
            {
                case VK_UP:
                    Highlight = (Highlight + RowCount - 1) % RowCount;
                    return (Outcome.Moved, Highlight);
                case VK_DOWN:
                    Highlight = (Highlight + 1) % RowCount;
                    return (Outcome.Moved, Highlight);
                case VK_RETURN:
                case VK_SPACE:
                    return (Outcome.Cycle, Highlight);
                case VK_ESCAPE:
                    Close();
                    return (Outcome.Closed, -1);
            }
            int row = DigitRow(vk);
            if (row < 0) return (Outcome.NotAMenuKey, -1);
            Highlight = row;
            return (Outcome.Cycle, row);
        }

        /// <summary> Is <paramref name="vk"/> one of the keys <see cref="OnKey"/> acts on?</summary>
        public static bool IsMenuKey(uint vk) =>
            vk == VK_UP || vk == VK_DOWN || vk == VK_RETURN || vk == VK_SPACE || vk == VK_ESCAPE || DigitRow(vk) >= 0;

        /// <summary> The row a digit key selects: 1 to 9 give rows 0 to 8, 0 gives row 9. -1 for any
        /// other key. Main row VK 0x30-0x39, keypad VK_NUMPAD0-9 0x60-0x69.</summary>
        public static int DigitRow(uint vk)
        {
            int digit = vk >= 0x30 && vk <= 0x39 ? (int)(vk - 0x30)
                      : vk >= 0x60 && vk <= 0x69 ? (int)(vk - 0x60)
                      : -1;
            if (digit < 0) return -1;
            int row = digit == 0 ? 9 : digit - 1;
            return row < RowCount ? row : -1;
        }

        /// <summary> The key printed at the start of a row: 1 to 9, then 0, then none.</summary>
        public static string KeyLabel(int row) => row < 9 ? (row + 1).ToString() : row == 9 ? "0" : " ";

        /// <summary> One line per row: key and topic, a tab, current value. The panel draws the text after
        /// the tab as a second column.</summary>
        public static IReadOnlyList<string> Lines(IReadOnlyList<(string Topic, string Value)> rows)
        {
            var lines = new List<string>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
                lines.Add($"{KeyLabel(i)}  {rows[i].Topic}\t{rows[i].Value}");
            return lines;
        }
    }
}
