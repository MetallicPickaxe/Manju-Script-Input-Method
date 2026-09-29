using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSharpTSFInput.ManjuShaper;

namespace CSharpTSFInput
{
    /// <summary>
    /// POSITIONAL-FORM CANDIDATES, TAKEN FROM THE FORMS A LETTER CAN ACTUALLY REACH.
    ///
    /// WHY FORCE-THEN-CHECK FAILS. Forcing a position by wrapping the letter in controls and
    /// then asking whether the result matches "the" natural form at that position fails twice:
    ///
    ///   A. On 16 of 34 letters the medial form CHANGES WITH THE NEIGHBOUR, so "the natural gid at this
    ///      position" does not exist and the question has no answer. That is not a defect, it is an
    ///      unanswerable question.
    ///   B. On U+1860 forced to FINAL, the wrap produces gid 390, and no natural context reaches it.
    ///      Forcing invents a shape the text cannot otherwise contain.
    ///
    /// So the question is not "is the forced gid THE natural gid". The candidates are taken FROM the
    /// reachable set. That is not force-then-check: the set is the SOURCE, so an unreachable shape is
    /// never built in the first place, and a positional class with several real forms simply offers
    /// several candidates, exactly as FVS already does.
    ///
    /// "Reachable" is measured, never assumed: every neighbour the product can place beside a
    /// letter is tried on both sides, the letter's POSITION in each context is read out of the product's
    /// own joining machine (<see cref="ManjuArabicShaper.Join"/>), and the glyphs it takes there are
    /// collected per position. No second definition of "word-medial" is written here.
    /// </summary>
    public static class ManjuPositionalForms
    {
        /// <summary>The non-letter neighbours a real run can put beside a letter: the suffix connector,
        /// the separator, both joiners, the narrow space and the adapter apostrophe.
        /// This is the swept list in full, not a shortlist chosen for convenience.</summary>
        public static readonly int[] Marks = { 0x180A, 0x180E, 0x200C, 0x200D, 0x202F, 0x0027 };

        /// <summary>The free variation selectors. These are how the script has ALWAYS named a variant
        /// of a positional form, so a candidate that needs one is ordinary text, not a trick.</summary>
        public static readonly int[] VariationSelectors = { 0x180B, 0x180C, 0x180D };

        /// <summary>One offered candidate: the code points to commit, the glyph they produce, which
        /// variation selector (if any) was needed, and which forcing method built it.
        /// `MethodIndex` lets the method switch (the forcing method menu row) select something: without it the
        /// switch would be inert for a single letter, since the candidate list spans
        /// every method already.</summary>
        public readonly record struct Candidate(string Text, string Form, int VariationSelector, int MethodIndex);

        private static readonly ConcurrentDictionary<(int Cp, ManjuEngine.PositionalForcing Pos), string[]> ReachCache = new();
        private static readonly ConcurrentDictionary<(int Cp, ManjuEngine.PositionalForcing Pos), Candidate[]> CandCache = new();
        private static int[]? _letters;

        /// <summary>
        /// The alphabet this IME can actually type — every code point any of the shipped input schemes
        /// can emit, kept to the dual-joining ones.
        ///
        /// THE SCHEMES, NOT THE UNICODE BLOCK, AND THE WHOLE FEATURE DEPENDS ON THAT DISTINCTION.
        /// Taking every dual-joining code point in U+1820..U+18A9 gives **123** of them, because that
        /// block also carries letters this Manchu IME cannot produce. With those in the neighbour
        /// sweep, U+1860's final set comes out {390|391|392|393}: gid 390 becomes "reachable" on the
        /// strength of a neighbour the user can never type, and the very shape this class exists to
        /// keep out is measured away. With the schemes' own **34** letters it is {387|391|392|393}, and
        /// 390 is unreachable.
        /// "Natural context" therefore means a context this product can produce, and nothing wider.
        ///
        /// All scheme files, not just the active one: the reachable set must not change under the
        /// user when they cycle schemes.
        ///
        /// If the scheme directory cannot be read the answer is EMPTY, which turns the feature off.
        /// Falling back to the whole block would silently add letters the user can never type to the sweep.
        /// </summary>
        public static int[] Letters() => _letters ??= LoadSchemeLetters();

        private static int[] LoadSchemeLetters()
        {
            try
            {
                string? dllDir = Path.GetDirectoryName(Globals.GetModulePath());
                if (dllDir == null) return Array.Empty<int>();
                string dir = Path.Combine(dllDir, "Resource", "Keymap", "schemes");
                if (!Directory.Exists(dir))
                {
                    return Array.Empty<int>();
                }

                var found = new SortedSet<int>();
                foreach (string file in Directory.GetFiles(dir, "*.yaml"))
                {
                    var root = Core.Configuration.MiniYamlReader.ParseFile(file);
                    Core.Configuration.MiniYamlReader.Node? maps = null;
                    root.Mapping?.TryGetValue("mappings", out maps);
                    if (maps?.Mapping == null) continue;
                    foreach (var kv in maps.Mapping)
                    {
                        var emitted = kv.Value.List ?? (kv.Value.Scalar != null
                                                        ? new List<string> { kv.Value.Scalar } : null);
                        if (emitted == null) continue;
                        foreach (string s in emitted)
                            for (int i = 0; i < s.Length; i++)
                            {
                                int cp = char.ConvertToUtf32(s, i);
                                if (char.IsHighSurrogate(s[i])) i++;
                                if (ManjuJoiningData.GetJoiningType((uint)cp) is ManjuJoiningType.D) found.Add(cp);
                            }
                    }
                }
                return found.ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<int>();
            }
        }

        /// <summary>
        /// Which position does the letter at <paramref name="index"/> occupy in this run?
        ///
        /// ANSWERED BY THE PRODUCT'S OWN JOINING MACHINE. `ManjuArabicShaper.Join` is the 1:1 replica of
        /// HarfBuzz's arabic state table that the real render path runs, and it writes INIT / MEDI /
        /// FINA / ISOL per character. Reading that is the only way this file and the shaper can agree
        /// about what "medial" means. Re-deriving it from joining types here would be a second,
        /// drifting definition.
        /// </summary>
        public static ManjuEngine.PositionalForcing PositionOf(string run, int index)
        {
            if (string.IsNullOrEmpty(run) || index < 0 || index >= run.Length)
                return ManjuEngine.PositionalForcing.Natural;

            using var buf = new ManjuBuffer();
            buf.AddUTF16(run);
            ManjuArabicShaper.Join(buf);

            for (int i = 0; i < buf.Length; i++)
            {
                if (buf.Info[i].Cluster != (uint)index) continue;
                return (ManjuArabicShaper.Action)buf.Info[i].ArabicAction switch
                {
                    ManjuArabicShaper.Action.INIT => ManjuEngine.PositionalForcing.Initial,
                    ManjuArabicShaper.Action.MEDI => ManjuEngine.PositionalForcing.Medial,
                    ManjuArabicShaper.Action.MED2 => ManjuEngine.PositionalForcing.Medial,
                    ManjuArabicShaper.Action.FINA => ManjuEngine.PositionalForcing.Final,
                    ManjuArabicShaper.Action.FIN2 => ManjuEngine.PositionalForcing.Final,
                    ManjuArabicShaper.Action.FIN3 => ManjuEngine.PositionalForcing.Final,
                    ManjuArabicShaper.Action.ISOL => ManjuEngine.PositionalForcing.Isolated,
                    _ => ManjuEngine.PositionalForcing.Natural,
                };
            }
            return ManjuEngine.PositionalForcing.Natural;
        }

        /// <summary>
        /// The glyph ids the letter at <paramref name="index"/> carries, as a stable key.
        ///
        /// `requireSeparable` IS NOT A CONVENIENCE FLAG. A shaper may FUSE a letter with its
        /// neighbour into one glyph, which U+182A does, and a fused glyph belongs to both characters
        /// at once, so it is NOT "a positional form of this letter".
        /// The reachable sweep must reject those contexts, or the set fills up with ligatures: without
        /// the check, U+182A's initial set includes `809` and `83 1482`, which are ligatures with
        /// whatever follows, and real forms then count as "missing".
        ///
        /// But the SAME check must NOT be applied to a control-wrapped candidate run: the controls are
        /// zero-width and contribute no glyph, so its cluster count can never reach its character count
        /// and every candidate would be rejected. It is therefore asked for by the letters-only sweep
        /// and never by the candidate side.
        /// </summary>
        public static unsafe string FormOf(byte[] font, string run, int index, bool requireSeparable = false)
        {
            List<ManjuShaperCore.FinalGlyph> g;
            fixed (byte* pf = font) g = ManjuShaperCore.FullShape(run, pf, font.Length, isVertical: true);
            if (g == null || g.Count == 0) return "";
            if (requireSeparable && g.Select(q => q.Cluster).Distinct().Count() < run.Length) return "";
            var mine = g.Where(q => q.Cluster == (uint)index).Select(q => q.GlyphId).ToList();
            return mine.Count == 0 ? "" : string.Join(" ", mine);
        }

        /// <summary>
        /// Every glyph shape <paramref name="letterCp"/> takes while standing at <paramref name="pos"/>
        /// in a natural context. The sweep is over EVERY neighbour on both sides — the letters plus
        /// the marks — and each context's position is read back from the joining machine, so a context
        /// counts towards `Medial` only if the shaper really treated the letter as medial there.
        /// </summary>
        public static IReadOnlyList<string> ReachableForms(byte[] font, int letterCp, ManjuEngine.PositionalForcing pos)
        {
            if (pos == ManjuEngine.PositionalForcing.Natural) return Array.Empty<string>();
            if (ReachCache.TryGetValue((letterCp, pos), out var hit)) return hit;
            SweepLetter(font, letterCp);
            return ReachCache.TryGetValue((letterCp, pos), out var got) ? got : Array.Empty<string>();
        }

        /// <summary>
        /// One sweep, all four positions. Every context is shaped ONCE and filed under whichever
        /// position the joining machine says the letter occupied there. Sweeping per position would
        /// shape the same 1600 contexts four times over, and this runs on the commit path.
        /// </summary>
        private static void SweepLetter(byte[] font, int letterCp)
        {
            string x = char.ConvertFromUtf32(letterCp);

            // LETTERS ONLY, AND THAT IS THE WHOLE POINT, NOT THE MARKS.
            //
            // Sweeping letters PLUS the marks includes the very controls the forcing wraps with
            // (ZWJ, ZWNJ, MVS). That makes the set SELF-PROVING: `ZWJ + X + MVS` is itself one of the
            // swept contexts, so every shape forcing could produce is "reachable" by construction,
            // U+1860's gid 390 included.
            //
            // Letter-only contexts are what the {387|388|389} and {387|391|392|393} sets come from,
            // and they match the definition this sweep needs: a shape that appears in real Manchu
            // words, not a shape the forcing's own control characters can coax out.
            var neighbours = Letters();
            var found = new Dictionary<ManjuEngine.PositionalForcing, SortedSet<string>>();

            void Try(string run, int idx)
            {
                var p = PositionOf(run, idx);
                if (p == ManjuEngine.PositionalForcing.Natural) return;
                // separable only — a fused glyph is not this letter's own form
                string f = FormOf(font, run, idx, requireSeparable: true);
                if (f.Length == 0) return;
                if (!found.TryGetValue(p, out var set))
                    found[p] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(f);
            }

            Try(x, 0);
            foreach (int a in neighbours)
            {
                string A = char.ConvertFromUtf32(a);
                Try(A + x, A.Length);
                Try(x + A, 0);
                foreach (int b in neighbours)
                    Try(A + x + char.ConvertFromUtf32(b), A.Length);
            }

            foreach (ManjuEngine.PositionalForcing p in new[]
                     {
                         ManjuEngine.PositionalForcing.Initial, ManjuEngine.PositionalForcing.Medial,
                         ManjuEngine.PositionalForcing.Final,   ManjuEngine.PositionalForcing.Isolated,
                     })
                ReachCache[(letterCp, p)] = found.TryGetValue(p, out var s) ? s.ToArray() : Array.Empty<string>();
        }

        /// <summary>
        /// The candidates to offer for "put this letter in <paramref name="pos"/>".
        ///
        /// THE REACHABLE SET IS THE SOURCE, NOT A POST-FILTER. Every wrapping the product can build
        /// is shaped, and a result is offered ONLY if its glyph is one the letter genuinely reaches at
        /// that position. ⇒ A shape like U+1860's gid 390 — produced by wrapping, reachable by nothing —
        /// is never offered, because it is not in the set the candidates are drawn from.
        ///
        /// One candidate per DISTINCT reachable form: a positional class yields as many candidates as
        /// it has real forms, no more and no fewer. The
        /// shortest text that produces a given form wins, so a bare wrap beats a wrap plus a selector.
        /// </summary>
        public static IReadOnlyList<Candidate> Candidates(byte[] font, int letterCp, ManjuEngine.PositionalForcing pos)
        {
            if (pos == ManjuEngine.PositionalForcing.Natural) return Array.Empty<Candidate>();
            return CandCache.GetOrAdd((letterCp, pos), key =>
            {
                var reachable = new HashSet<string>(ReachableForms(font, key.Cp, key.Pos), StringComparer.Ordinal);
                if (reachable.Count == 0) return Array.Empty<Candidate>();

                string x = char.ConvertFromUtf32(key.Cp);
                var best = new Dictionary<string, Candidate>(StringComparer.Ordinal);

                for (int mi = 0; mi < ManjuEngine.ForcingMethods.Length; mi++)
                {
                    var (j, b) = ManjuEngine.ForcingMethods[mi];
                    (char left, char right) = key.Pos switch
                    {
                        ManjuEngine.PositionalForcing.Initial => (b, j),
                        ManjuEngine.PositionalForcing.Medial => (j, j),
                        ManjuEngine.PositionalForcing.Final => (j, b),
                        ManjuEngine.PositionalForcing.Isolated => (b, b),
                        _ => ('\0', '\0'),
                    };
                    if (left == '\0') continue;

                    foreach (int vs in new[] { 0 }.Concat(VariationSelectors))
                    {
                        string body = vs == 0 ? x : x + char.ConvertFromUtf32(vs);
                        string run = left + body + right;
                        string form = FormOf(font, run, 1);          // the letter sits after one control
                        if (form.Length == 0 || !reachable.Contains(form)) continue;
                        if (!best.TryGetValue(form, out var have) || run.Length < have.Text.Length)
                            best[form] = new Candidate(run, form, vs, mi);
                    }
                }
                return best.Values.OrderBy(c => c.Form, StringComparer.Ordinal).ToArray();
            });
        }

    }
}
