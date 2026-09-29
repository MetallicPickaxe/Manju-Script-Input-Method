using System;

namespace CSharpTSFInput.ManjuShaper
{
    public static class ManjuArabicShaper
    {
        // joining actions (the same set as hb-ot-shaper-arabic.cc)
        public enum Action : byte
        {
            NONE = 0,
            ISOL = 1,
            FINA = 2,
            INIT = 3,
            MEDI = 4,
            FIN2 = 5,
            FIN3 = 6,
            MED2 = 7
        }

        private struct StateEntry
        {
            public Action PrevAction;
            public Action CurrAction;
            public ushort NextState;

            public StateEntry(Action prev, Action curr, ushort next)
            {
                PrevAction = prev;
                CurrAction = curr;
                NextState = next;
            }
        }

        // a 1:1 copy of HarfBuzz's arabic_state_table (the 6x6 form, tuned for Manju)
        private static readonly StateEntry[,] StateTable = new StateEntry[7, 6]
        {
            /* jt_U, jt_L, jt_R, jt_D, jg_ALAPH, jg_DALATH */
            /* 0 */ { new(Action.NONE, Action.NONE, 0), new(Action.NONE, Action.ISOL, 2), new(Action.NONE, Action.ISOL, 1), new(Action.NONE, Action.ISOL, 2), new(Action.NONE, Action.ISOL, 1), new(Action.NONE, Action.ISOL, 6) },
            /* 1 */ { new(Action.NONE, Action.NONE, 0), new(Action.NONE, Action.ISOL, 2), new(Action.NONE, Action.ISOL, 1), new(Action.NONE, Action.ISOL, 2), new(Action.NONE, Action.FIN2, 5), new(Action.NONE, Action.ISOL, 6) },
            /* 2 */ { new(Action.NONE, Action.NONE, 0), new(Action.NONE, Action.ISOL, 2), new(Action.INIT, Action.FINA, 1), new(Action.INIT, Action.FINA, 3), new(Action.INIT, Action.FINA, 4), new(Action.INIT, Action.FINA, 6) },
            /* 3 */ { new(Action.NONE, Action.NONE, 0), new(Action.NONE, Action.ISOL, 2), new(Action.MEDI, Action.FINA, 1), new(Action.MEDI, Action.FINA, 3), new(Action.MEDI, Action.FINA, 4), new(Action.MEDI, Action.FINA, 6) },
            /* 4 */ { new(Action.NONE, Action.NONE, 0), new(Action.NONE, Action.ISOL, 2), new(Action.MED2, Action.ISOL, 1), new(Action.MED2, Action.ISOL, 2), new(Action.MED2, Action.FIN2, 5), new(Action.MED2, Action.ISOL, 6) },
            /* 5 */ { new(Action.NONE, Action.NONE, 0), new(Action.NONE, Action.ISOL, 2), new(Action.ISOL, Action.ISOL, 1), new(Action.ISOL, Action.ISOL, 2), new(Action.ISOL, Action.FIN2, 5), new(Action.ISOL, Action.ISOL, 6) },
            /* 6 */ { new(Action.NONE, Action.NONE, 0), new(Action.NONE, Action.ISOL, 2), new(Action.NONE, Action.ISOL, 1), new(Action.NONE, Action.ISOL, 2), new(Action.NONE, Action.FIN3, 5), new(Action.NONE, Action.ISOL, 6) }
        };

        public static void Join(ManjuBuffer buffer)
        {
            ushort state = 0;
            int prev = -1;

            for (int i = 0; i < buffer.Length; i++)
            {
                uint u = buffer.Info[i].Codepoint;
				ManjuJoiningType jt = ManjuJoiningData.GetJoiningType(u);

                // jt (X) is already resolved to T/U by ManjuJoiningData

                buffer.Info[i].JoiningType = (byte)jt;

                if (jt == ManjuJoiningType.T) // Transparent (a variation selector, for instance)
                {
                    buffer.Info[i].ArabicAction = (byte)Action.NONE;
                    continue;
                }

                // work out the state transition
                int col = (int)jt;
                if (jt == ManjuJoiningType.C) col = 3; // C (Nirugu) behaves like D (Dual Joining)

				// col should be 0, 1, 2, 3 now.

				StateEntry entry = StateTable[state, col];

                if (entry.PrevAction != Action.NONE && prev != -1)
                {
                    buffer.Info[prev].ArabicAction = ( Byte ) entry.PrevAction;
                }

                buffer.Info[i].ArabicAction = ( Byte ) entry.CurrAction;
                prev = i;
                state = entry.NextState;
            }

            // special case: Mongolian FVS inheritance
            ApplyMongolianFvsFix(buffer);
        }

        private static void ApplyMongolianFvsFix(ManjuBuffer buffer)
        {
            for (int i = 1; i < buffer.Length; i++)
            {
                uint u = buffer.Info[i].Codepoint;
                // FVS1-3 (180B-180D) and FVS4 (180F)
                if ((u >= 0x180B && u <= 0x180D) || u == 0x180F)
                {
                    buffer.Info[i].ArabicAction = buffer.Info[i - 1].ArabicAction;
                }
            }
        }
    }
}
