using System;
using System.Buffers;
using System.Collections.Generic;

namespace CSharpTSFInput.ManjuShaper
{
    /// <summary>
    /// ManjuBuffer: the counterpart of hb_buffer_t.
    /// Double-buffered, so characters can be substituted and reordered during shaping.
    /// </summary>
    public class ManjuBuffer : IDisposable
    {
        public ManjuGlyphInfo[] Info;
        public ManjuGlyphPosition[] Pos;
        public int Length;
        
        private const int DefaultCapacity = 256;

        public ManjuBuffer()
        {
            Info = ArrayPool<ManjuGlyphInfo>.Shared.Rent(DefaultCapacity);
            Pos = ArrayPool<ManjuGlyphPosition>.Shared.Rent(DefaultCapacity);
            Length = 0;
        }

        public void AddUTF16(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // make sure the capacity is enough
            EnsureCapacity(text.Length);

            for (int i = 0; i < text.Length; i++)
            {
                uint codepoint;
                uint cluster = (uint)i;

                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    codepoint = (uint)char.ConvertToUtf32(text[i], text[i + 1]);
                    i++; // skip the low surrogate
                }
                else
                {
                    codepoint = text[i];
                }

                Info[Length] = new ManjuGlyphInfo { Codepoint = codepoint, Cluster = cluster };
                Pos[Length] = new ManjuGlyphPosition();
                Length++;
            }
        }


        public void EnsureCapacity(int requiredAddition)
        {
            int needed = Length + requiredAddition;
            if (needed > Info.Length)
            {
                int newSize = Math.Max(needed, Info.Length * 2);

				ManjuGlyphInfo [] newInfo = ArrayPool<ManjuGlyphInfo>.Shared.Rent(newSize);
				ManjuGlyphPosition [] newPos = ArrayPool<ManjuGlyphPosition>.Shared.Rent(newSize);

                if (Length > 0)
                {
                    Array.Copy(Info, newInfo, Length);
                    Array.Copy(Pos, newPos, Length);
                }

                ArrayPool<ManjuGlyphInfo>.Shared.Return(Info);
                ArrayPool<ManjuGlyphPosition>.Shared.Return(Pos);

                Info = newInfo;
                Pos = newPos;
            }
        }

        public void Dispose()
        {
            ArrayPool<ManjuGlyphInfo>.Shared.Return(Info);
            ArrayPool<ManjuGlyphPosition>.Shared.Return(Pos);
        }
    }
}
